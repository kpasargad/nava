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
    public string ServerFeatureNotice=>SelectedRecord?.ApiKind=="soniox"?"این نسخهٔ سرور، تبدیل گفتار Soniox را دارد؛ تحلیل و مرتب‌سازی متن روی سرور فعال نیست. نام گوینده‌ها روی همین رایانه ذخیره می‌شود.":"";
    private string _status="نگه دار و صحبت کن",_hint="با رها کردن کلید، ضبط تمام می‌شود",_liveText="",_finalText="",_partialText="",_time="00:00",_connection="در حال بررسی سرویس…",_error="",_analysis="";
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
        foreach(var record in Store.LoadRecords()) { if(record.Status is "recording" or "processing") { record.Status="failed"; record.Text=string.IsNullOrWhiteSpace(record.Text)?"ضبط یا پردازش قبلی کامل نشده. برای بازیابی، تلاش دوباره را بزنید.":record.Text; Store.Save(record); } Records.Add(record); }
        _clock.Tick+=(_,__)=> { if(IsRecording) { var elapsed=DateTime.UtcNow-_started; TimeLabel=elapsed.ToString(elapsed.TotalHours>=1?@"hh\:mm\:ss":@"mm\:ss"); if(elapsed>TimeSpan.FromHours(2)) _=EndPushToTalkAsync(); } };
    }
    public void Initialize()
    {
        _overlay=new OverlayWindow(this); _overlay.Show();
        _overlay.RestorePosition(Settings.OverlayLeft,Settings.OverlayTop);
        _hotkey=new GlobalHotkey(new WindowInteropHelper(_overlay).Handle);
        _hotkey.Pressed+=()=> { if(_workspace?.IsCapturingHotkey!=true && !IsRecording && !IsBusy && !AnalysisBusy) { _globalOwnsHold=true; _=StartPushToTalkAsync(); } };
        _hotkey.Released+=()=> { if(_globalOwnsHold) { _globalOwnsHold=false; _=EndPushToTalkAsync(); } };
        try { _hotkey.Register(Settings.Hotkey); } catch(Exception e) { Fail(e.Message); }
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("نمایش نوا",null,(_,__)=>UI(ShowOverlay));
        menu.Items.Add("دکمهٔ نگه‌دار و صحبت کن",null,(_,__)=>UI(ShowOverlay));
        menu.Items.Add("تاریخچه و ابزارها",null,(_,__)=>UI(()=>OpenWorkspace()));
        menu.Items.Add("تنظیمات",null,(_,__)=>UI(()=>OpenWorkspace("settings")));
        menu.Items.Add("پنهان کردن روح",null,(_,__)=>UI(()=>_overlay?.Hide()));
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("خروج",null,(_,__)=>UI(Quit));
        var iconPath=Path.Combine(AppContext.BaseDirectory,"nava.ico");
        _tray=new Forms.NotifyIcon { Text="نوا • "+Settings.Hotkey,Icon=File.Exists(iconPath)?new System.Drawing.Icon(iconPath):System.Drawing.SystemIcons.Information,ContextMenuStrip=menu,Visible=true };
        _tray.DoubleClick+=(_,__)=>UI(()=>OpenWorkspace());
        if(string.IsNullOrWhiteSpace(Settings.BaseUrl) || string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            Status="اول اتصال را تنظیم کن"; Hint="آدرس سرویس و کلید را وارد کن"; ConnectionLabel="اتصال تنظیم نشده";
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
        catch(OperationCanceledException) { Status="لغو شد"; Hint="هر وقت خواستی، دوباره شروع کن"; }
        catch(Exception e) { _operation?.Cancel(); _clock.Stop(); try { _recorder?.Stop(); } catch { } IsRecording=false; IsBusy=false; Level=0; Fail(Friendly(e)); if(_draft!=null) { _draft.Status="failed"; try { Store.Save(_draft); } catch { } } }
        finally { _transition.Release(); }
    }
    public async Task EndPushToTalkAsync()
    {
        _pttRequested=false;
        if(_disposed) return;
        await _transition.WaitAsync();
        try { if(IsRecording) await StopRecordingAsync(); }
        catch(OperationCanceledException) { Status="لغو شد"; Hint="کلید را نگه دار و دوباره صحبت کن"; }
        catch(Exception e) { _operation?.Cancel(); _clock.Stop(); try { _recorder?.Stop(); } catch { } IsRecording=false; IsBusy=false; IsConnecting=false; IsLiveConnected=false; Fail(Friendly(e)); }
        finally { _transition.Release(); }
    }
    // Kept for older integrations; interactive controls use the explicit press/release pair.
    public Task ToggleRecordingAsync()=>IsRecording?EndPushToTalkAsync():StartPushToTalkAsync();
    private Task StartRecordingAsync()
    {
        if(string.IsNullOrWhiteSpace(Settings.ApiKey)) { ErrorMessage="کلید سرویس را در تنظیمات وارد کنید."; OpenWorkspace("settings"); return Task.CompletedTask; }
        ErrorMessage=""; LiveText=""; FinalText=""; PartialText=""; AnalysisText=""; TimeLabel="00:00"; _pasteTarget=WindowsIntegration.GetForegroundWindow();
        _operation?.Dispose(); _operation=new(); _streamFailed=false; _streamFailure=null; _realtime=null; _sendTask=null;
        _streamOperation?.Dispose(); _streamOperation=CancellationTokenSource.CreateLinkedTokenSource(_operation.Token);
        _draft=new() { Title="یادداشت "+DateTime.Now.ToString("MM/dd HH:mm"),Status="recording" };
        _draft.AudioPath=Path.Combine(Store.AudioDirectory,_draft.Id+".wav");
        _recorder=_audioFactory();
        _recorder.LevelChanged+=value=>UI(()=>Level=value);
        _recorder.Failed+=()=>UI(async ()=> { await CancelAsync(); Fail("ذخیرهٔ صدا متوقف شد. فضای دیسک و دسترسی پوشه را بررسی کن."); });
        _recorder.DataAvailable+=data=>
        {
            if(_audio!=null && !_audio.Writer.TryWrite(data) && !_streamFailed)
            {
                _streamFailed=true; _streamFailure=new ServiceException("اتصال زنده بیش از حد طول کشید. صدا برای تلاش دوباره نگه داشته شد.");
                UI(()=> { IsConnecting=false; IsLiveConnected=false; ErrorMessage=_streamFailure.Message; Hint="صدا محفوظ است؛ کلید را رها کن و اتصال را بررسی کن"; });
            }
        };
        _audio=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(150) { SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait });
        try { _recorder.Start(_draft.AudioPath,Settings.MicrophoneId); }
        catch { _recorder.Dispose(); _recorder=null; _audio=null; throw; }
        Records.Insert(0,_draft); SelectedRecord=_draft; Store.Save(_draft);
        IsRecording=true; IsConnecting=true; IsLiveConnected=false; Status="دارم وصل می‌شوم…"; Hint="صحبت کن؛ برای پایان کلید را رها کن";
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
                if(IsRecording) Status="دارم می‌نویسم…";
            }),ct);
            ct.ThrowIfCancellationRequested(); _realtime=session;
            IsConnecting=false; IsLiveConnected=true; if(IsRecording) { Status="دارم گوش می‌کنم…"; Hint="متن همین‌جا ظاهر می‌شود؛ کلید را نگه دار"; }
            _sendTask=SendAudioAsync(session,channel.Reader,ct);
        }
        catch(Exception e) { if(session!=null) await session.DisposeAsync(); if(!ct.IsCancellationRequested) { _streamFailed=true; _streamFailure=e; UI(()=> { IsConnecting=false; IsLiveConnected=false; ErrorMessage=Friendly(e); Hint="اتصال زنده برقرار نشد؛ صدا برای تلاش دوباره محفوظ است"; }); } }
    }
    private async Task SendAudioAsync(RealtimeSession session,ChannelReader<byte[]> reader,CancellationToken ct)
    {
        try { await foreach(var frame in reader.ReadAllAsync(ct)) await session.SendAudioAsync(frame,ct); }
        catch(Exception e) { if(!ct.IsCancellationRequested) { _streamFailed=true; _streamFailure=e; UI(()=> { IsLiveConnected=false; ErrorMessage=Friendly(e); Hint="ارتباط زنده قطع شد؛ متن و صدا محفوظ‌اند"; }); } }
    }
    private async Task StopRecordingAsync()
    {
        IsRecording=false; IsBusy=true; _clock.Stop(); _recorder?.Stop(); Level=0; if(Settings.Sounds) AudioCues.PlayStopped();
        var draft=_draft!; draft.DurationMs=_recorder?.DurationMs??0; _recorder?.Dispose(); _recorder=null;
        _audio?.Writer.TryComplete(); draft.Status="processing"; Store.Save(draft);
        Status="دارم واژه‌های آخر را نهایی می‌کنم…"; Hint="متن گفته‌شده همین‌جا آماده است";
        var ct=_operation!.Token;
        try
        {
            if(draft.DurationMs<350) { Store.Delete(draft); Records.Remove(draft); SelectedRecord=Records.FirstOrDefault(); Status="ضبط خیلی کوتاه بود"; Hint="کمی بیشتر صحبت کن"; return; }
            if(_connectTask!=null) await _connectTask.WaitAsync(TimeSpan.FromSeconds(18),ct);
            if(_sendTask!=null) await _sendTask.WaitAsync(TimeSpan.FromSeconds(15),ct);
            if(_streamFailed || _realtime==null) throw _streamFailure??new ServiceException("اتصال زنده کامل نشد. صدا در تاریخچه محفوظ است.");
            var result=await _realtime.FinishAsync(ct).WaitAsync(TimeSpan.FromSeconds(12),ct);
            ct.ThrowIfCancellationRequested();
            _streamOperation?.Cancel();
            ApplyResult(draft,result);
            Complete(draft,true);
        }
        catch(OperationCanceledException) { draft.Status="failed"; Store.Save(draft); Status="پردازش متوقف شد"; Hint="فایل صدا در تاریخچه است؛ می‌توانی دوباره تلاش کنی"; }
        catch(Exception e) { if(e is ServiceException se && se.RemoteId!=null) draft.RemoteId=se.RemoteId; draft.Status="failed"; Store.Save(draft); Fail(Friendly(e)); Hint="صدا در تاریخچه محفوظ است؛ تلاش دوباره را بزن"; }
        finally { _streamOperation?.Cancel(); if(_realtime!=null) await _realtime.DisposeAsync(); _realtime=null; _audio=null; IsBusy=false; IsConnecting=false; IsLiveConnected=false; _draft=null; }
    }
    private static void ApplyResult(TranscriptRecord target,TranscriptRecord result)
    {
        target.Text=result.Text; target.RemoteId=result.RemoteId; target.ApiKind=result.ApiKind; target.Segments=result.Segments; target.Status="completed";
        if(result.DurationMs>0) target.DurationMs=Math.Max(target.DurationMs,result.DurationMs);
    }
    private void Complete(TranscriptRecord draft,bool allowPaste)
    {
        draft.Status="completed";
        if(!Settings.KeepAudio) { Store.DeleteOwnedAudio(draft.AudioPath); draft.AudioPath=null; }
        Store.Save(draft); SelectedRecord=draft; LiveText=draft.Text; FinalText=draft.Text; PartialText="";
        Status=string.IsNullOrWhiteSpace(draft.Text)?"گفتاری تشخیص داده نشد":"نوشتم؛ آماده‌ست!";
        Hint="متن در تاریخچه ذخیره شد";
        if(Settings.AutoCopy && !string.IsNullOrWhiteSpace(draft.Text)) { CopyText(draft.Text); Hint="متن کپی شد • در هر برنامه‌ای بچسبان"; }
        if(allowPaste && Settings.AutoPaste && !string.IsNullOrWhiteSpace(draft.Text)) { CopyText(draft.Text); Hint=WindowsIntegration.Paste(_pasteTarget)?"متن در پنجرهٔ قبلی چسبانده شد":"متن کپی شد؛ با Ctrl+V بچسبان"; }
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
            IsBusy=true; IsRecording=false; _clock.Stop(); _recorder?.Stop();
            if(Settings.Sounds) AudioCues.PlayStopped();
            if(_draft!=null) { _draft.DurationMs=_recorder?.DurationMs??0; _draft.Status="failed"; Store.Save(_draft); }
            _recorder?.Dispose(); _recorder=null; _audio?.Writer.TryComplete();
            try { if(_connectTask!=null) await _connectTask.WaitAsync(TimeSpan.FromSeconds(3)); if(_sendTask!=null) await _sendTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            if(_realtime!=null) await _realtime.DisposeAsync(); _realtime=null;
            _audio=null; _draft=null;
            Status="ضبط متوقف شد"; Hint="صدا برای تلاش دوباره در تاریخچه نگه داشته شد"; Level=0; IsBusy=false; IsConnecting=false; IsLiveConnected=false;
        }
        }
        finally { _transition.Release(); }
    }
    public async Task FormatSelectedAsync()
    {
        if(SelectedRecord==null || IsBusy || IsRecording || AnalysisBusy || string.IsNullOrWhiteSpace(SelectedRecord.Text)) return;
        if(SelectedRecord.ApiKind=="soniox") { ErrorMessage="مسیر مرتب‌سازی متن روی نسخهٔ فعلی سرور فعال نیست."; return; }
        var record=SelectedRecord; AnalysisBusy=true; _operation?.Dispose(); _operation=new();
        try { var formatted=await Client.SmartFormatAsync(record.Text,Math.Max(1000,record.DurationMs),_operation.Token); record.Analyses["original_text"]=record.Text; record.Text=formatted; Store.Save(record); Hint="متن مرتب شد؛ نسخهٔ قبلی در خروجی JSON محفوظ است"; }
        catch(OperationCanceledException) { Hint="مرتب‌سازی متوقف شد"; }
        catch(Exception e) { Fail(Friendly(e)); }
        finally { AnalysisBusy=false; }
    }
    public async Task ImportFileAsync()
    {
        if(IsBusy || IsRecording || AnalysisBusy) return;
        var dialog=new OpenFileDialog { Title="انتخاب فایل صوتی یا ویدیو",Filter="صوت و ویدیو|*.wav;*.m4a;*.mp3;*.mp4;*.ogg;*.webm;*.flac;*.aac;*.opus|همهٔ فایل‌ها|*.*" };
        if(dialog.ShowDialog()!=true) return;
        var record=new TranscriptRecord { Title=Path.GetFileNameWithoutExtension(dialog.FileName),AudioPath=dialog.FileName,Status="failed" };
        Records.Insert(0,record); SelectedRecord=record; Store.Save(record); OpenWorkspace(); await RetrySelectedAsync();
    }
    public async Task RetrySelectedAsync()
    {
        if(IsBusy || IsRecording || AnalysisBusy || SelectedRecord==null) return;
        var record=SelectedRecord;
        if(record.AudioPath==null || !File.Exists(record.AudioPath)) { Fail("فایل صدا در دسترس نیست. فایل اصلی را دوباره وارد کنید."); return; }
        if(record.AudioPath.StartsWith(Store.AudioDirectory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) AudioRecorder.RepairHeader(record.AudioPath);
        IsBusy=true; ErrorMessage=""; _operation?.Dispose(); _operation=new(); record.Status="processing"; Store.Save(record);
        try { var result=await Client.TranscribeFileAsync(record.AudioPath,record.Title,new Progress<string>(x=>Status=x),_operation.Token,record.RemoteId,id=> { record.RemoteId=id; Store.Save(record); }); ApplyResult(record,result); Complete(record,false); }
        catch(OperationCanceledException) { record.Status="failed"; Status="پردازش متوقف شد"; Store.Save(record); }
        catch(Exception e) { if(e is ServiceException se && se.RemoteId!=null) record.RemoteId=se.RemoteId; record.Status="failed"; Store.Save(record); Fail(Friendly(e)); }
        finally { IsBusy=false; }
    }
    public async Task AnalyzeSelectedAsync(string action,string? customPrompt)
    {
        if(AnalysisBusy || IsBusy || IsRecording || SelectedRecord==null) return;
        var record=SelectedRecord; AnalysisBusy=true; ErrorMessage="";
        _operation?.Dispose(); _operation=new();
        try
        {
            if(record.ApiKind=="soniox") { AnalysisText="نسخهٔ فعلی سرویس شما تبدیل گفتار را پشتیبانی می‌کند، اما مسیر تحلیل /v1/analysis روی آن فعال نیست. پس از استقرار API توضیح‌داده‌شده، خلاصه و فهرست کارها از اینجا اجرا می‌شوند."; return; }
            if(record.RemoteId==null)
            {
                // Live sessions may not create a persisted transcription ID. Create a text-backed remote transcript only if retained audio is available.
                if(record.AudioPath!=null && File.Exists(record.AudioPath)) { var uploaded=await Client.TranscribeFileAsync(record.AudioPath,record.Title,null,_operation.Token); record.RemoteId=uploaded.RemoteId; Store.Save(record); }
                else { AnalysisText="برای تحلیل سرور، فایل صدا را وارد کن یا «نگهداری صدا» را در تنظیمات روشن کن. این نشست زنده شناسهٔ قابل تحلیل برنگردانده است."; return; }
            }
            if(action=="custom" && string.IsNullOrWhiteSpace(customPrompt)) { AnalysisText="درخواست دلخواهت را بنویس."; return; }
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
        try { Store.SaveSettings(Settings); Raise(nameof(CharacterId)); Raise(nameof(Settings)); Hint="همراه تازه‌ات انتخاب شد"; }
        catch { Settings.CharacterId=previous; Fail("ذخیرهٔ شخصیت انجام نشد. دسترسی پوشه را بررسی کن."); }
    }
    public void DeleteSelected()
    {
        if(SelectedRecord==null || IsRecording || IsBusy || AnalysisBusy) return;
        var record=SelectedRecord; Store.Delete(record); Records.Remove(record); SelectedRecord=Records.FirstOrDefault();
    }
    public void CopySelected() { if(SelectedRecord!=null) { CopyText(SelectedRecord.Text); Hint="متن کپی شد"; } }
    public void OpenAudioSelected()
    {
        if(SelectedRecord?.AudioPath is string p && File.Exists(p)) try { Process.Start(new ProcessStartInfo(p) { UseShellExecute=true }); } catch { Fail("فایل با پخش‌کنندهٔ پیش‌فرض باز نشد."); }
        else Fail("فایل صدا نگه داشته نشده است. برای ضبط‌های بعدی «نگهداری صدا» را روشن کن.");
    }
    public void ExportSelected(string format)
    {
        if(SelectedRecord==null) return;
        var ext=format.ToLowerInvariant() is "srt"?"srt":format.ToLowerInvariant() is "json"?"json":"txt";
        var safeTitle=string.Concat(SelectedRecord.Title.Where(c=>!Path.GetInvalidFileNameChars().Contains(c)));
        var dialog=new SaveFileDialog { Title="ذخیرهٔ متن",FileName=safeTitle,DefaultExt="."+ext,Filter=ext.ToUpperInvariant()+"|*."+ext };
        if(dialog.ShowDialog()!=true) return;
        var content=ext=="srt"?LocalStore.ToSrt(SelectedRecord):ext=="json"?JsonSerializer.Serialize(SelectedRecord,new JsonSerializerOptions { WriteIndented=true }):SelectedRecord.Text;
        File.WriteAllText(dialog.FileName,content,new UTF8Encoding(true)); Hint="فایل ذخیره شد";
    }
    public async Task<bool> SaveSettingsAsync(AppSettings proposed)
    {
        if(IsRecording || IsBusy || AnalysisBusy) { Fail("بعد از پایان ضبط یا پردازش، تنظیمات را تغییر بده."); return false; }
        try
        {
            proposed.CharacterId=CharacterCatalog.Normalize(proposed.CharacterId);
            if(!Uri.TryCreate(proposed.BaseUrl,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("آدرس سرویس معتبر نیست.");
            proposed.ApiKey=proposed.ApiKey.Trim(); if(proposed.ApiKey.Length==0) throw new ArgumentException("کلید سرویس را وارد کن.");
            proposed.BaseUrl=proposed.BaseUrl.Trim().TrimEnd('/');
            if(!string.IsNullOrWhiteSpace(proposed.ProxyUrl) && (!Uri.TryCreate(proposed.ProxyUrl,UriKind.Absolute,out var proxy) || proxy.Scheme!="http")) throw new ArgumentException("آدرس پروکسی باید با http:// شروع شود.");
            if(proposed.Hotkey!=Settings.Hotkey) _hotkey?.Register(proposed.Hotkey);
            if(proposed.StartWithWindows!=Settings.StartWithWindows)
            {
                using var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);
                if(run==null) throw new IOException("تنظیم شروع خودکار در دسترس نیست.");
                if(proposed.StartWithWindows) run.SetValue("Nava","\""+Environment.ProcessPath+"\""); else run.DeleteValue("Nava",false);
            }
            Store.SaveSettings(proposed); Settings=proposed; Raise(nameof(Settings)); Raise(nameof(HotkeyLabel)); Raise(nameof(CharacterId)); if(_tray!=null) _tray.Text="نوا • "+Settings.Hotkey;
            ErrorMessage=""; Hint="تنظیمات ذخیره شد"; await TestConnectionAsync(); return true;
        }
        catch(Exception e) { try { _hotkey?.Register(Settings.Hotkey); } catch { } Fail(e is ArgumentException or InvalidOperationException?e.Message:"ذخیرهٔ تنظیمات انجام نشد. دسترسی پوشه را بررسی کن."); return false; }
    }
    public async Task TestConnectionAsync()
    {
        ConnectionLabel="در حال بررسی سرویس…";
        try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20)); bool ok=await Client.HealthAsync(timeout.Token); ConnectionLabel=ok?"سرویس متصل است":"سرویس آماده نیست"; }
        catch(Exception e) { ConnectionLabel="سرویس در دسترس نیست"; if(_workspace?.IsVisible==true) ErrorMessage=Friendly(e); }
    }
    public void OpenWorkspace(string tab="history") { if(_workspace==null) { _workspace=new WorkspaceWindow(this); _workspace.Closed+=(_,__)=>_workspace=null; } _workspace.ShowTab(tab); _workspace.Show(); _workspace.Activate(); }
    public void ShowOverlay() { _overlay?.Show(); _overlay?.Expand(); }
    public void HideOverlay() { _overlay?.Collapse(); }
    public void SaveOverlayPosition(double left,double top) { Settings.OverlayLeft=left; Settings.OverlayTop=top; try { Store.SaveSettings(Settings); } catch { } }
    public void Quit()
    {
        if(IsRecording && MessageBox.Show("ضبط متوقف می‌شود و صدا برای بازیابی در تاریخچه می‌ماند. خارج شوم؟","خروج از نوا",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        _workspace?.ForceClose(); Application.Current.Shutdown();
    }
    private static void UI(Action action) { if(Application.Current.Dispatcher.CheckAccess()) action(); else Application.Current.Dispatcher.BeginInvoke(action); }
    private void Fail(string message) { ErrorMessage=message; Status="یک جای کار گیر کرد"; Hint="تنظیمات یا تاریخچه را بررسی کن"; }
    private static string Friendly(Exception ex) => ex is IOException?"خواندن یا ذخیرهٔ فایل انجام نشد. دسترسی و فضای دیسک را بررسی کن.":ex is UnauthorizedAccessException?"دسترسی به فایل ممکن نیست.":ex is TimeoutException?"پاسخ سرویس طول کشید. صدا محفوظ است؛ دوباره تلاش کن.":ex.Message.Contains("http",StringComparison.OrdinalIgnoreCase)?"ارتباط با سرویس برقرار نشد. تنظیمات اتصال را بررسی کن.":ex.Message;
    private void CopyText(string text) { try { Clipboard.SetText(text); } catch { Hint="کپی نشد؛ متن را از تاریخچه کپی کن"; } }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _operation?.Cancel(); _clock.Stop(); _recorder?.Stop();
        if(_draft!=null) { _draft.DurationMs=_recorder?.DurationMs??_draft.DurationMs; _draft.Status="failed"; Store.Save(_draft); }
        _recorder?.Dispose(); _hotkey?.Dispose(); if(_tray!=null) { _tray.Visible=false; _tray.Dispose(); } _audio?.Writer.TryComplete();
        if(_realtime!=null) _= _realtime.DisposeAsync();
    }
}
