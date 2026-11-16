using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Nava.Core;
using Nava.Services;
using Nava.Views;
using Forms = System.Windows.Forms;

namespace Nava;

public sealed class AppController : Observable, IDisposable
{
    public LocalStore Store { get; }
    public AppSettings Settings { get; private set; }
    public IReadOnlyList<CharacterDefinition> Characters=>CharacterCatalog.All;
    public string CharacterId=>Settings.CharacterId;
    public ObservableCollection<TranscriptRecord> Records { get; } = new();
    public IReadOnlyList<MicrophoneDevice> Microphones { get; private set; } = AudioRecorder.Devices();
    private TranscriptRecord? _selected;
    public TranscriptRecord? SelectedRecord { get=>_selected; set { if(_selected==value) Raise(nameof(SelectedRecord)); else Set(ref _selected,value); AnalysisText=value?.Analyses.GetValueOrDefault("summarize")??value?.Analyses.Where(x=>x.Key!="original_text").Select(x=>x.Value).FirstOrDefault()??""; Raise(nameof(ServerFeatureNotice)); } }
    public string ServerFeatureNotice=>SelectedRecord?.ApiKind=="soniox"?"در این اتصال، خلاصه‌سازی و مرتب‌کردن متن در دسترس نیست. نام گوینده‌ها روی این دستگاه ذخیره می‌شود.":"";
    private string _status="آماده‌ام",_hint="میانبر را نگه دار و صحبت کن",_liveText="",_finalText="",_partialText="",_time="00:00",_connection="در حال بررسی اتصال…",_error="",_analysis="";
    private bool _recording,_busy,_analysisBusy,_connecting,_liveConnected,_pttRequested,_globalOwnsHold;
    private double _level;
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public string Hint { get=>_hint; private set=>Set(ref _hint,value); }
    public string LiveText { get=>_liveText; private set=>Set(ref _liveText,value); }
    public string FinalText { get=>_finalText; private set=>Set(ref _finalText,value); }
    public string PartialText { get=>_partialText; private set=>Set(ref _partialText,value); }
    public bool IsConnecting { get=>_connecting; private set=>Set(ref _connecting,value); }
    public bool IsLiveConnected { get=>_liveConnected; private set=>Set(ref _liveConnected,value); }
    public string TimeLabel { get=>_time; private set=>Set(ref _time,value); }
    public string HotkeyLabel=>Settings.Hotkey;
    public string ConnectionLabel { get=>_connection; private set=>Set(ref _connection,value); }
    public string ErrorMessage { get=>_error; private set { Set(ref _error,value); Raise(nameof(HasError)); } }
    public bool HasError=>!string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsRecording { get=>_recording; private set=>Set(ref _recording,value); }
    public bool IsBusy { get=>_busy; private set=>Set(ref _busy,value); }
    public double Level { get=>_level; private set=>Set(ref _level,value); }
    public string AnalysisText { get=>_analysis; private set=>Set(ref _analysis,value); }
    public bool AnalysisBusy { get=>_analysisBusy; private set=>Set(ref _analysisBusy,value); }
    private OverlayWindow? _overlay;
    private WorkspaceWindow? _workspace;
    private GlobalHotkey? _hotkey;
    private Forms.NotifyIcon? _tray;
    private IAudioCapture? _recorder;
    private readonly Func<IAudioCapture> _audioFactory;
    private RealtimeSession? _realtime;
    private Task? _connectTask,_sendTask;
    private Channel<byte[]>? _audio;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _streamOperation;
    private readonly DispatcherTimer _clock=new() { Interval=TimeSpan.FromMilliseconds(100) };
    private TranscriptRecord? _draft;
    private bool _streamFailed,_disposed;
    private Exception? _streamFailure;
    private IntPtr _pasteTarget;
    private readonly SemaphoreSlim _transition=new(1,1);
    private DateTime _started;
    private TranscriptionClient Client=>new(Settings);
    public AppController(LocalStore? store=null,Func<IAudioCapture>? audioFactory=null)
    {
        Store=store??new LocalStore(); _audioFactory=audioFactory??(()=>new AudioRecorder());
        Settings=Store.LoadSettings(); Settings.LiveTranscription=true;
        try
        {
            using var startup=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var command=startup?.GetValue("Nava") as string;
            Settings.StartWithWindows=string.Equals(command,"\""+Environment.ProcessPath+"\"",StringComparison.OrdinalIgnoreCase)
                || string.Equals(command,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase);
        }
        catch { Settings.StartWithWindows=false; }
        foreach(var record in Store.LoadRecords())
        {
            if(record.Status is "recording" or "processing")
            {
                record.Status="failed";
                try { Store.Save(record); }
                catch(Exception e) when(e is IOException or UnauthorizedAccessException) { ErrorMessage="بعضی یادداشت‌ها به‌روز نشدند. فضای دیسک و دسترسی به پوشهٔ نوا را بررسی کن."; }
            }
            Records.Add(record);
        }
        _clock.Tick+=(_,__)=> { if(IsRecording) { var elapsed=DateTime.UtcNow-_started; TimeLabel=elapsed.ToString(elapsed.TotalHours>=1?@"hh\:mm\:ss":@"mm\:ss"); if(elapsed>TimeSpan.FromHours(2)) _=EndPushToTalkAsync(); } };
    }
    public void Initialize()
    {
        _overlay=new OverlayWindow(this); _overlay.Show();
        _overlay.RestorePosition(Settings.OverlayLeft,Settings.OverlayTop);
        _hotkey=new GlobalHotkey(new WindowInteropHelper(_overlay).Handle);
        _hotkey.Pressed+=()=> { if(_workspace?.IsCapturingHotkey!=true && !IsRecording && !IsBusy && !AnalysisBusy) { _globalOwnsHold=true; _=StartPushToTalkAsync(); } };
        _hotkey.Released+=()=> { if(_globalOwnsHold) { _globalOwnsHold=false; _=EndPushToTalkAsync(); } };
        SystemEvents.SessionSwitch+=SessionChanged;
        SystemEvents.PowerModeChanged+=PowerChanged;
        try { _hotkey.Register(Settings.Hotkey); } catch(Exception e) { Fail(e.Message); }
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("باز کردن نوا",null,(_,__)=>UI(ShowOverlay));
        menu.Items.Add("یادداشت‌ها",null,(_,__)=>UI(()=>OpenWorkspace()));
        menu.Items.Add("تنظیمات",null,(_,__)=>UI(()=>OpenWorkspace("settings")));
        menu.Items.Add("پنهان کردن نوا",null,(_,__)=>UI(()=>_overlay?.Hide()));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("خروج",null,(_,__)=>UI(Quit));
        var iconPath=Path.Combine(AppContext.BaseDirectory,"nava.ico");
        _tray=new Forms.NotifyIcon { Text="نوا • "+Settings.Hotkey,Icon=File.Exists(iconPath)?new System.Drawing.Icon(iconPath):System.Drawing.SystemIcons.Information,ContextMenuStrip=menu,Visible=true };
        _tray.DoubleClick+=(_,__)=>UI(()=>OpenWorkspace());
        if(string.IsNullOrWhiteSpace(Settings.BaseUrl) || string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            Status="اتصال را تنظیم کن"; Hint="آدرس سرویس و کلید دسترسی را وارد کن"; ConnectionLabel="اتصال تنظیم نشده";
            OpenWorkspace("settings");
        }
        else _=TestConnectionAsync();
    }
    public async Task StartPushToTalkAsync()
    {
        if(_disposed || IsRecording || IsBusy || AnalysisBusy) return;
        _pttRequested=true;
        await _transition.WaitAsync();
        try
        {
            if(_pttRequested && !IsRecording && !IsBusy && !AnalysisBusy) await StartRecordingAsync();
        }
        catch(Exception e)
        {
            var draft=_draft;
            await CleanupRecordingAsync();
            if(draft!=null) { draft.Status="failed"; TrySave(draft); }
            if(e is OperationCanceledException) { Status="لغو شد"; Hint="هر وقت آماده بودی، دوباره شروع کن"; }
            else Fail(Friendly(e));
        }
        finally { _transition.Release(); }
    }
    public async Task EndPushToTalkAsync()
    {
        _pttRequested=false;
        if(_disposed) return;
        await _transition.WaitAsync();
        try { if(IsRecording) await StopRecordingAsync(); }
        catch(Exception e) { await CleanupRecordingAsync(); Fail(Friendly(e)); }
        finally { _transition.Release(); }
    }
    // Kept for older integrations; interactive controls use the explicit press/release pair.
    public Task ToggleRecordingAsync()=>IsRecording?EndPushToTalkAsync():StartPushToTalkAsync();
    private Task StartRecordingAsync()
    {
        if(string.IsNullOrWhiteSpace(Settings.ApiKey)) { ErrorMessage="برای شروع، کلید دسترسی را در تنظیمات وارد کن."; OpenWorkspace("settings"); return Task.CompletedTask; }
        ErrorMessage=""; LiveText=""; FinalText=""; PartialText=""; AnalysisText=""; TimeLabel="00:00"; _pasteTarget=WindowsIntegration.GetForegroundWindow();
        _operation?.Dispose(); _operation=new(); _streamFailed=false; _streamFailure=null; _realtime=null; _sendTask=null;
        _streamOperation?.Dispose(); _streamOperation=CancellationTokenSource.CreateLinkedTokenSource(_operation.Token);
        _draft=new() { Title="یادداشت "+DateTime.Now.ToString("MM/dd HH:mm"),Status="recording" };
        _draft.AudioPath=Path.Combine(Store.AudioDirectory,_draft.Id+".wav");
        _recorder=_audioFactory();
        // Verify persistence before opening the microphone.
        Store.Save(_draft);
        var captureDraft=_draft;
        _recorder.LevelChanged+=value=>UI(()=> { if(_draft==captureDraft && IsRecording) Level=value; });
        _recorder.Failed+=()=>UI(async ()=> { if(_draft!=captureDraft) return; await CancelAsync(); Fail("صدا ذخیره نشد. فضای دیسک و دسترسی به پوشهٔ نوا را بررسی کن."); });
        _recorder.DataAvailable+=data=>
        {
            if(_audio!=null && !_audio.Writer.TryWrite(data) && !_streamFailed)
            {
                _streamFailed=true; _streamFailure=new ServiceException("اتصال بیش از حد طول کشید. برای پایان، میانبر یا دکمه را رها کن.");
                UI(()=> { IsConnecting=false; IsLiveConnected=false; ErrorMessage=_streamFailure.Message; Hint="برای پایان رها کن؛ سپس اتصال را بررسی کن"; });
            }
        };
        _audio=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(150) { SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait });
        try { _recorder.Start(_draft.AudioPath,Settings.MicrophoneId); }
        catch { _recorder.Dispose(); _recorder=null; _audio=null; throw; }
        Records.Insert(0,_draft); SelectedRecord=_draft;
        IsRecording=true; IsConnecting=true; IsLiveConnected=false; Status="در حال اتصال…"; Hint="صدایت ضبط می‌شود؛ برای پایان رها کن";
        _started=DateTime.UtcNow; _clock.Start(); _overlay?.Show(); _overlay?.Expand(false); if(Settings.Sounds) AudioCues.PlayStarted();
        _connectTask=ConnectRealtimeAsync(_audio!,_streamOperation.Token);
        return Task.CompletedTask;
    }
    private async Task ConnectRealtimeAsync(Channel<byte[]> channel,CancellationToken ct)
    {
        RealtimeSession? session=null;
        var draft=_draft;
        try
        {
            session=await Client.StartRealtimeAsync((segments,interim)=>UI(()=>
            {
                if(draft==null || _draft!=draft || ct.IsCancellationRequested) return;
                FinalText=string.Concat(segments.Select(x=>x.Text)); PartialText=interim;
                LiveText=FinalText+PartialText;
                draft.Text=FinalText; draft.Segments=segments.ToList();
                IsConnecting=false; IsLiveConnected=true;
                if(IsRecording) Status="دارم می‌نویسم";
            }),ct);
            ct.ThrowIfCancellationRequested(); _realtime=session;
            IsConnecting=false; IsLiveConnected=true; if(IsRecording) { Status="گوش می‌کنم"; Hint="برای پایان، میانبر یا دکمه را رها کن"; }
            _sendTask=SendAudioAsync(session,channel.Reader,ct);
        }
        catch(Exception e) { if(session!=null) await session.DisposeAsync(); if(!ct.IsCancellationRequested) { _streamFailed=true; _streamFailure=e; UI(()=> { IsConnecting=false; IsLiveConnected=false; ErrorMessage=Friendly(e); Hint="برای پایان رها کن؛ از یادداشت‌ها دوباره امتحان کن"; }); } }
    }
    private async Task SendAudioAsync(RealtimeSession session,ChannelReader<byte[]> reader,CancellationToken ct)
    {
        try { await foreach(var frame in reader.ReadAllAsync(ct)) await session.SendAudioAsync(frame,ct); }
        catch(Exception e) { if(!ct.IsCancellationRequested) { _streamFailed=true; _streamFailure=e; UI(()=> { IsLiveConnected=false; ErrorMessage=Friendly(e); Hint="برای پایان رها کن؛ از یادداشت‌ها دوباره امتحان کن"; }); } }
    }
    private async Task StopRecordingAsync()
    {
        IsBusy=true; IsRecording=false; _clock.Stop();
        var draft=_draft!;
        try
        {
            await StopCaptureAsync(); Level=0; if(Settings.Sounds) AudioCues.PlayStopped();
            _audio?.Writer.TryComplete(); draft.Status="processing"; Store.Save(draft);
            Status="در حال تکمیل متن…"; Hint="کمی صبر کن تا متن کامل شود";
            var ct=_operation!.Token;
            if(draft.DurationMs<350) { Store.Delete(draft); Records.Remove(draft); SelectedRecord=Records.FirstOrDefault(); Status="ضبط خیلی کوتاه بود"; Hint="میانبر را نگه دار و کمی بیشتر صحبت کن"; return; }
            if(_connectTask!=null) await _connectTask.WaitAsync(TimeSpan.FromSeconds(18),ct);
            if(_sendTask!=null) await _sendTask.WaitAsync(TimeSpan.FromSeconds(15),ct);
            if(_streamFailed || _realtime==null) throw _streamFailure??new ServiceException("اتصال زنده کامل نشد. تنظیمات اتصال را بررسی کن.");
            var result=await _realtime.FinishAsync(ct).WaitAsync(TimeSpan.FromSeconds(12),ct);
            ct.ThrowIfCancellationRequested();
            _streamOperation?.Cancel();
            ApplyResult(draft,result);
            Complete(draft,true);
        }
        catch(OperationCanceledException) { draft.Status="failed"; bool saved=TrySave(draft); Status="تبدیل متوقف شد"; Hint=RecoveryHint(draft,saved); }
        catch(Exception e) { if(e is ServiceException se && se.RemoteId!=null) draft.RemoteId=se.RemoteId; draft.Status="failed"; bool saved=TrySave(draft); Fail(saved?Friendly(e):ErrorMessage); Hint=RecoveryHint(draft,saved); }
        finally { await CleanupRecordingAsync(); }
    }
    private static void ApplyResult(TranscriptRecord target,TranscriptRecord result)
    {
        target.Text=result.Text; target.RemoteId=result.RemoteId; target.ApiKind=result.ApiKind; target.Segments=result.Segments; target.Status="completed";
        if(result.DurationMs>0) target.DurationMs=Math.Max(target.DurationMs,result.DurationMs);
    }
    private void Complete(TranscriptRecord draft,bool allowPaste)
    {
        draft.Status="completed";
        // The transcript must be durable before any recoverable audio is removed.
        Store.Save(draft);
        if(!Settings.KeepAudio)
        {
            var audio=draft.AudioPath;
            try { Store.DeleteOwnedAudio(audio); draft.AudioPath=null; Store.Save(draft); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { draft.AudioPath=audio; ErrorMessage="متن ذخیره شد؛ پاک‌سازی فایل صدا کامل نشد."; }
        }
        SelectedRecord=draft; LiveText=draft.Text; FinalText=draft.Text; PartialText="";
        bool hasText=!string.IsNullOrWhiteSpace(draft.Text);
        Status=hasText?"متن آماده است":"گفتاری تشخیص داده نشد";
        Hint=hasText?"در یادداشت‌ها ذخیره شد":"میکروفن را بررسی کن و دوباره صحبت کن";
        if((Settings.AutoCopy || allowPaste && Settings.AutoPaste) && !string.IsNullOrWhiteSpace(draft.Text) && CopyText(draft.Text))
        {
            Hint="متن کپی شد؛ با Ctrl+V بچسبان";
            if(allowPaste && Settings.AutoPaste) Hint=WindowsIntegration.Paste(_pasteTarget,draft.Text)?"متن کپی شد؛ چسباندن را در برنامه بررسی کن":"متن کپی شد؛ با Ctrl+V بچسبان";
        }
    }
    public async Task CancelAsync()
    {
        _pttRequested=false;
        _operation?.Cancel();
        await _transition.WaitAsync();
        try
        {
            if(IsRecording)
            {
                IsBusy=true; IsRecording=false; _clock.Stop();
                var draft=_draft;
                try
                {
                    await StopCaptureAsync();
                    if(Settings.Sounds) AudioCues.PlayStopped();
                    if(draft!=null) { draft.Status="failed"; Store.Save(draft); }
                    Status="ضبط متوقف شد"; Hint="برای تبدیل دوباره، این ضبط را در یادداشت‌ها باز کن";
                }
                catch(Exception e) { Fail(Friendly(e)); }
                finally { await CleanupRecordingAsync(); }
            }
        }
        finally { _transition.Release(); }
    }
    private async Task StopCaptureAsync()
    {
        var recorder=_recorder; _recorder=null;
        if(recorder==null) return;
        try { await Task.Run(recorder.Stop); if(_draft!=null) _draft.DurationMs=recorder.DurationMs; }
        finally { await Task.Run(recorder.Dispose); }
    }
    private async Task CleanupRecordingAsync()
    {
        _pttRequested=false; _operation?.Cancel(); _streamOperation?.Cancel(); _clock.Stop();
        try { await StopCaptureAsync(); } catch { }
        _audio?.Writer.TryComplete();
        try { await Task.WhenAll(_connectTask??Task.CompletedTask,_sendTask??Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        var session=_realtime; _realtime=null;
        if(session!=null) try { await session.DisposeAsync(); } catch { }
        _connectTask=null; _sendTask=null; _audio=null; _draft=null;
        IsRecording=false; IsBusy=false; IsConnecting=false; IsLiveConnected=false; Level=0;
    }
    private bool TrySave(TranscriptRecord record)
    {
        try { Store.Save(record); return true; }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { ErrorMessage="یادداشت ذخیره نشد. فضای دیسک و دسترسی به پوشهٔ نوا را بررسی کن."; return false; }
    }
    private static string RecoveryHint(TranscriptRecord record,bool saved)
    {
        if(!saved) return "";
        if(record.AudioPath is string audio && File.Exists(audio)) return "برای تبدیل دوباره، این ضبط را در یادداشت‌ها باز کن";
        return string.IsNullOrWhiteSpace(record.Text)?"فایل صدا در دسترس نیست؛ دوباره ضبط کن":"متن موجود را می‌توانی از یادداشت‌ها کپی کنی";
    }
    private void SessionChanged(object sender,SessionSwitchEventArgs e)
    {
        if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect) UI(ReleaseForSystemPause);
    }
    private void PowerChanged(object sender,PowerModeChangedEventArgs e) { if(e.Mode==PowerModes.Suspend) UI(ReleaseForSystemPause); }
    private void ReleaseForSystemPause()
    {
        _globalOwnsHold=false; _hotkey?.ReleaseHeld(); _pttRequested=false; _=CancelAsync();
    }
    public async Task FormatSelectedAsync()
    {
        if(SelectedRecord==null || IsBusy || IsRecording || AnalysisBusy || string.IsNullOrWhiteSpace(SelectedRecord.Text)) return;
        if(SelectedRecord.ApiKind=="soniox") { ErrorMessage="مرتب‌کردن متن در این اتصال در دسترس نیست."; return; }
        var record=SelectedRecord; AnalysisBusy=true; _operation?.Dispose(); _operation=new();
        try { var formatted=await Client.SmartFormatAsync(record.Text,Math.Max(1000,record.DurationMs),_operation.Token); record.Analyses["original_text"]=record.Text; record.Text=formatted; Store.Save(record); Hint="متن مرتب شد"; }
        catch(OperationCanceledException) { Hint="مرتب‌سازی متوقف شد"; }
        catch(Exception e) { Fail(Friendly(e)); }
        finally { AnalysisBusy=false; }
    }
    public async Task ImportFileAsync()
    {
        if(IsBusy || IsRecording || AnalysisBusy) return;
        var dialog=new OpenFileDialog { Title="تبدیل فایل صوتی یا ویدیویی",Filter="صوت و ویدیو|*.wav;*.m4a;*.mp3;*.mp4;*.ogg;*.webm;*.flac;*.aac;*.opus|همهٔ فایل‌ها|*.*" };
        if(dialog.ShowDialog(DialogOwner)!=true) return;
        var record=new TranscriptRecord { Title=Path.GetFileNameWithoutExtension(dialog.FileName),AudioPath=dialog.FileName,Status="failed" };
        Records.Insert(0,record); SelectedRecord=record; Store.Save(record); OpenWorkspace(); await RetrySelectedAsync();
    }
    public async Task RetrySelectedAsync()
    {
        if(IsBusy || IsRecording || AnalysisBusy || SelectedRecord==null) return;
        var record=SelectedRecord;
        if(record.AudioPath==null || !File.Exists(record.AudioPath)) { Fail("فایل صدا پیدا نشد. فایل اصلی را دوباره انتخاب کن."); return; }
        IsBusy=true; ErrorMessage=""; _operation?.Dispose(); _operation=new();
        try
        {
            if(record.AudioPath.StartsWith(Store.AudioDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) AudioRecorder.RepairHeader(record.AudioPath);
            record.Status="processing"; Store.Save(record);
            var result=await Client.TranscribeFileAsync(record.AudioPath,record.Title,new Progress<string>(x=>Status=x),_operation.Token,record.RemoteId,id=> { record.RemoteId=id; Store.Save(record); }); ApplyResult(record,result); Complete(record,false);
        }
        catch(OperationCanceledException) { record.Status="failed"; Status="تبدیل متوقف شد"; Hint=RecoveryHint(record,TrySave(record)); }
        catch(Exception e) { if(e is ServiceException se && se.RemoteId!=null) record.RemoteId=se.RemoteId; record.Status="failed"; bool saved=TrySave(record); Fail(saved?Friendly(e):ErrorMessage); Hint=RecoveryHint(record,saved); }
        finally { IsBusy=false; }
    }
    public async Task AnalyzeSelectedAsync(string action,string? customPrompt)
    {
        if(AnalysisBusy || IsBusy || IsRecording || SelectedRecord==null) return;
        var record=SelectedRecord; AnalysisBusy=true; ErrorMessage="";
        _operation?.Dispose(); _operation=new();
        try
        {
            if(record.ApiKind=="soniox") { AnalysisText="خلاصه‌سازی و ابزارهای متن در این اتصال در دسترس نیستند. متن را همچنان می‌توانی بخوانی، ویرایش کنی و کپی کنی."; return; }
            if(record.RemoteId==null)
            {
                // Live sessions may not create a persisted transcription ID. Create a text-backed remote transcript only if retained audio is available.
                if(record.AudioPath!=null && File.Exists(record.AudioPath)) { var uploaded=await Client.TranscribeFileAsync(record.AudioPath,record.Title,null,_operation.Token); record.RemoteId=uploaded.RemoteId; Store.Save(record); }
                else { AnalysisText="برای تحلیل این یادداشت، فایل صدای آن لازم است. اگر فایل اصلی را داری، دوباره واردش کن."; return; }
            }
            if(action=="custom" && string.IsNullOrWhiteSpace(customPrompt)) { AnalysisText="بنویس از این متن چه می‌خواهی."; return; }
            var result=await Client.AnalyzeAsync(record.RemoteId!,action,customPrompt,_operation.Token);
            record.Analyses[action]=result; Store.Save(record); if(SelectedRecord==record) AnalysisText=result;
        }
        catch(OperationCanceledException) { AnalysisText="تحلیل متوقف شد."; }
        catch(Exception e) { Fail(Friendly(e)); }
        finally { AnalysisBusy=false; }
    }
    public void SelectAnalysis(string action) => AnalysisText=SelectedRecord?.Analyses.GetValueOrDefault(action)??"";
    public async Task RenameSpeakersAsync(Dictionary<string,string> names)
    {
        var record=SelectedRecord; if(record==null || IsBusy || IsRecording || AnalysisBusy) return;
        AnalysisBusy=true; _operation?.Dispose(); _operation=new();
        try { if(record.RemoteId!=null && record.ApiKind!="soniox") await Client.NameSpeakersAsync(record.RemoteId,names,_operation.Token); record.SpeakerNames=names; if(Records.Contains(record)) Store.Save(record); Raise(nameof(SelectedRecord)); }
        catch(OperationCanceledException) { Hint="تغییر نام گوینده‌ها متوقف شد"; }
        catch(Exception e) { Fail(Friendly(e)); }
        finally { AnalysisBusy=false; }
    }
    public void SaveRecord() { if(SelectedRecord!=null) { Store.Save(SelectedRecord); Raise(nameof(SelectedRecord)); } }
    public void SelectCharacter(string id)
    {
        string next=CharacterCatalog.Normalize(id),previous=Settings.CharacterId;
        if(next==previous) return;
        Settings.CharacterId=next;
        try { Store.SaveSettings(Settings); Raise(nameof(CharacterId)); Raise(nameof(Settings)); Hint="چهرهٔ نوا عوض شد"; }
        catch { Settings.CharacterId=previous; Fail("چهرهٔ تازه ذخیره نشد. فضای دیسک و دسترسی به پوشهٔ نوا را بررسی کن."); }
    }
    public void DeleteSelected()
    {
        if(SelectedRecord==null || IsRecording || IsBusy || AnalysisBusy) return;
        var record=SelectedRecord; Store.Delete(record); Records.Remove(record); SelectedRecord=Records.FirstOrDefault();
    }
    public void CopySelected() { if(SelectedRecord!=null && CopyText(SelectedRecord.Text)) Hint="متن کپی شد"; }
    public void OpenAudioSelected()
    {
        if(SelectedRecord?.AudioPath is string p && File.Exists(p)) try { Process.Start(new ProcessStartInfo(p) { UseShellExecute=true }); } catch { Fail("فایل صدا باز نشد. پخش‌کنندهٔ پیش‌فرض ویندوز را بررسی کن."); }
        else Fail("فایل صدا در دسترس نیست. اگر فایل اصلی را داری، دوباره واردش کن.");
    }
    public void ExportSelected(string format)
    {
        if(SelectedRecord==null) return;
        var ext=format.ToLowerInvariant() is "srt"?"srt":format.ToLowerInvariant() is "json"?"json":"txt";
        var safeTitle=string.Concat(SelectedRecord.Title.Where(c=>!Path.GetInvalidFileNameChars().Contains(c)));
        var dialog=new SaveFileDialog { Title="ذخیرهٔ متن",FileName=safeTitle,DefaultExt="."+ext,Filter=ext.ToUpperInvariant()+"|*."+ext };
        if(dialog.ShowDialog(DialogOwner)!=true) return;
        var content=ext=="srt"?LocalStore.ToSrt(SelectedRecord):ext=="json"?JsonSerializer.Serialize(SelectedRecord,new JsonSerializerOptions { WriteIndented=true }):SelectedRecord.Text;
        File.WriteAllText(dialog.FileName,content,new UTF8Encoding(true)); Hint="فایل ذخیره شد";
    }
    public async Task<bool> SaveSettingsAsync(AppSettings proposed)
    {
        if(IsRecording || IsBusy || AnalysisBusy) { Fail("صبر کن این کار تمام شود؛ بعد تنظیمات را تغییر بده."); return false; }
        object? previousStartup=null; bool startupChanged=false;
        try
        {
            proposed.CharacterId=CharacterCatalog.Normalize(proposed.CharacterId);
            if(!Uri.TryCreate(proposed.BaseUrl,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("آدرس سرویس را با http:// یا https:// وارد کن.");
            proposed.ApiKey=(proposed.ApiKey??"").Trim(); if(proposed.ApiKey.Length==0) throw new ArgumentException("کلید دسترسی را وارد کن.");
            proposed.BaseUrl=proposed.BaseUrl.Trim().TrimEnd('/');
            if(!string.IsNullOrWhiteSpace(proposed.ProxyUrl) && (!Uri.TryCreate(proposed.ProxyUrl,UriKind.Absolute,out var proxy) || proxy.Scheme!="http")) throw new ArgumentException("آدرس پروکسی باید با http:// شروع شود.");
            if(proposed.Hotkey!=Settings.Hotkey) _hotkey?.Register(proposed.Hotkey);
            if(proposed.StartWithWindows!=Settings.StartWithWindows)
            {
                using var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);
                if(run==null) throw new IOException("اجرای نوا با ویندوز تنظیم نشد. دوباره امتحان کن.");
                previousStartup=run.GetValue("Nava"); startupChanged=true;
                if(proposed.StartWithWindows) run.SetValue("Nava","\""+Environment.ProcessPath+"\""); else run.DeleteValue("Nava",false);
            }
            Store.SaveSettings(proposed); Settings=proposed; Raise(nameof(Settings)); Raise(nameof(HotkeyLabel)); Raise(nameof(CharacterId)); if(_tray!=null) _tray.Text="نوا • "+Settings.Hotkey;
            ErrorMessage=""; Hint="تنظیمات ذخیره شد"; await TestConnectionAsync(); return true;
        }
        catch(Exception e)
        {
            try { _hotkey?.Register(Settings.Hotkey); } catch { }
            if(startupChanged) try { using var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true); if(previousStartup==null) run?.DeleteValue("Nava",false); else run?.SetValue("Nava",previousStartup); } catch { }
            Fail(e is ArgumentException or InvalidOperationException?e.Message:"تنظیمات ذخیره نشد. دوباره امتحان کن."); return false;
        }
    }
    public async Task TestConnectionAsync()
    {
        ConnectionLabel="در حال بررسی اتصال…";
        try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20)); bool ok=await Client.HealthAsync(timeout.Token); ConnectionLabel=ok?"سرویس پاسخ می‌دهد":"سرویس آماده نیست"; }
        catch(Exception e) { ConnectionLabel="اتصال برقرار نشد"; if(_workspace?.IsVisible==true) ErrorMessage=Friendly(e); }
    }
    private Window? DialogOwner=>_workspace?.IsVisible==true?_workspace:_overlay;
    public void OpenWorkspace(string tab="history") { if(_workspace==null) { _workspace=new WorkspaceWindow(this); _workspace.Closed+=(_,__)=>_workspace=null; } _workspace.ShowTab(tab); if(_workspace.WindowState==WindowState.Minimized) _workspace.WindowState=WindowState.Normal; _workspace.Show(); _workspace.Activate(); }
    public void ShowOverlay() { _overlay?.Show(); _overlay?.Expand(); }
    public void HideOverlay() { _overlay?.Collapse(); }
    public void SaveOverlayPosition(double left,double top) { Settings.OverlayLeft=left; Settings.OverlayTop=top; try { Store.SaveSettings(Settings); } catch { } }
    public void Quit()
    {
        if(IsRecording)
        {
            const string prompt="ضبط را متوقف کنیم و از نوا خارج شویم؟ صدای ضبط‌شده برای تبدیل دوباره نگه داشته می‌شود.";
            var answer=DialogOwner is Window owner?MessageBox.Show(owner,prompt,"خروج از نوا",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No):MessageBox.Show(prompt,"خروج از نوا",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No);
            if(answer!=MessageBoxResult.Yes) return;
        }
        _workspace?.ForceClose(); Application.Current.Shutdown();
    }
    private static void UI(Action action) { if(Application.Current.Dispatcher.CheckAccess()) action(); else Application.Current.Dispatcher.BeginInvoke(action); }
    private void Fail(string message) { ErrorMessage=message; Status="این مرحله کامل نشد"; Hint=""; }
    private static string Friendly(Exception ex) => ex switch
    {
        IOException=>"فایل باز یا ذخیره نشد. فضای دیسک و دسترسی به فایل را بررسی کن.",
        UnauthorizedAccessException=>"نوا به این فایل دسترسی ندارد. دسترسی پوشه را بررسی کن.",
        TimeoutException=>"پاسخ سرویس طول کشید. کمی بعد دوباره امتحان کن.",
        ServiceException=>ex.Message,
        _ when ex.Message.Any(c=>c>='\u0600' && c<='\u06ff')=>ex.Message,
        _=>"این کار کامل نشد. دوباره امتحان کن."
    };
    private bool CopyText(string text) { try { Clipboard.SetText(text); return true; } catch { Hint="متن کپی نشد. دوباره «کپی متن» را بزن."; return false; } }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _operation?.Cancel(); _streamOperation?.Cancel(); _clock.Stop();
        SystemEvents.SessionSwitch-=SessionChanged; SystemEvents.PowerModeChanged-=PowerChanged;
        try { _recorder?.Stop(); } catch { }
        if(_draft!=null) { _draft.DurationMs=_recorder?.DurationMs??_draft.DurationMs; _draft.Status="failed"; TrySave(_draft); }
        try { _recorder?.Dispose(); } catch { }
        _hotkey?.Dispose(); if(_tray!=null) { _tray.Visible=false; _tray.Dispose(); } _audio?.Writer.TryComplete();
        if(_realtime!=null) _= _realtime.DisposeAsync();
    }
}
