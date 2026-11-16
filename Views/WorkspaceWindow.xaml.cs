using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nava.Core;
using Nava.Services;

namespace Nava.Views;

public partial class WorkspaceWindow : Window
{
    private readonly AppController controller;
    private bool ready;
    private bool allowClose;
    private bool savingSettings;
    private bool testingConnection;
    private bool loadingCharacters;
    private string pane = "read";
    private TranscriptRecord? observedRecord;
    private string? liveRecordId;
    private readonly PushToTalkGesture pushToTalk;
    private DispatcherOperation? pendingLiveScroll;
    private bool closed;
    public bool IsCapturingHotkey => HotkeyBox.IsKeyboardFocusWithin;
    public WorkspaceWindow(AppController controller)
    {
        this.controller = controller; InitializeComponent(); DataContext = controller;
        ShellWindowPolicy.Attach(this);
        pushToTalk=new PushToTalkGesture(this,controller,RecordButton,WelcomeRecordButton);
        ready = true; controller.PropertyChanged += ControllerChanged; controller.Records.CollectionChanged += RecordsChanged;
        Closing += OnClosing;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !IsCapturingHotkey) { Hide(); e.Handled = true; } };
        LoadSettings(); RefreshSelection(); UpdateState();
    }
    public void ShowTab(string tab) { if (tab == "settings") { HistoryPanel.Visibility = Visibility.Collapsed; SettingsPanel.Visibility = Visibility.Visible; LoadSettings(); } else { SettingsPanel.Visibility = Visibility.Collapsed; HistoryPanel.Visibility = Visibility.Visible; } SetNav(HistoryNav, tab != "settings"); SetNav(SettingsNav, tab == "settings"); }
    public void ForceClose() { allowClose = true; Close(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!allowClose) { e.Cancel = true; controller.SaveRecord(); Hide(); } else { closed=true; pendingLiveScroll?.Abort(); pendingLiveScroll=null; pushToTalk.Dispose(); controller.PropertyChanged -= ControllerChanged; controller.Records.CollectionChanged -= RecordsChanged; if(observedRecord!=null) observedRecord.PropertyChanged-=RecordChanged; } }
    private void RecordsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => RecordsChanged(sender,e)); return; }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && !string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Clear();
        UpdateState();
    }
    private void ControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppController.FinalText) or nameof(AppController.PartialText))
        {
            if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(()=>ControllerChanged(sender,e)); return; }
            if(closed) return;
            WorkspaceLivePlaceholder.Visibility=string.IsNullOrWhiteSpace(controller.FinalText+controller.PartialText)?Visibility.Visible:Visibility.Collapsed;
            if(IsVisible && WorkspaceLivePanel.Visibility==Visibility.Visible && (pendingLiveScroll==null || pendingLiveScroll.Status!=DispatcherOperationStatus.Pending))
                pendingLiveScroll=Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=> { pendingLiveScroll=null; if(!closed && IsVisible && WorkspaceLivePanel.Visibility==Visibility.Visible) WorkspaceLiveScroller.ScrollToEnd(); }));
            return;
        }
        if (e.PropertyName is nameof(AppController.Level) or nameof(AppController.TimeLabel) or nameof(AppController.LiveText) or nameof(AppController.Status) or nameof(AppController.Hint) or nameof(AppController.ConnectionLabel) or nameof(AppController.ErrorMessage) or nameof(AppController.HasError) or nameof(AppController.AnalysisText)) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ControllerChanged(sender,e)); return; }
        if (e.PropertyName is nameof(AppController.SelectedRecord) or null or "") RefreshSelection();
        UpdateState();
    }
    private void UpdateState()
    {
        if (!ready) return;
        bool occupied = controller.IsBusy || controller.IsRecording || controller.AnalysisBusy;
        bool serverAnalysis = controller.SelectedRecord?.ApiKind != "soniox";
        RefreshEmptyHistory();
        RecordButton.Content = controller.IsRecording ? "برای پایان رها کن" : "نگه دار و صحبت کن";
        RecordButton.IsEnabled = !occupied || controller.IsRecording;
        WelcomeRecordButton.IsEnabled = !occupied || controller.IsRecording;
        ImportButton.IsEnabled = !occupied;
        CancelButton.Visibility = occupied ? Visibility.Visible : Visibility.Collapsed;
        AnalyzeButton.IsEnabled = !occupied && serverAnalysis && controller.SelectedRecord != null;
        AnalysisAction.IsEnabled = !controller.AnalysisBusy && serverAnalysis;
        CustomPrompt.IsReadOnly = controller.AnalysisBusy || !serverAnalysis;
        CapabilityNotice.Visibility = serverAnalysis ? Visibility.Collapsed : Visibility.Visible;
        AnalyzeButton.Content = controller.AnalysisBusy ? "در حال آماده‌سازی…" : AnalysisActionLabel();
        FormatButton.IsEnabled = !occupied && serverAnalysis && controller.SelectedRecord?.Status == "completed";
        FormatButton.ToolTip = serverAnalysis ? "متن را مرتب و نشانه‌گذاری کن." : "در این نوع اتصال، مرتب‌سازی متن در دسترس نیست.";
        TranscriptEditor.IsReadOnly = controller.AnalysisBusy || controller.SelectedRecord?.Status is "recording" or "processing";
        SpeakersButton.IsEnabled = DeleteButton.IsEnabled = !occupied && controller.SelectedRecord != null;
        SaveSettingsButton.IsEnabled = !savingSettings && !occupied;
        TestButton.IsEnabled = !testingConnection;
        var record = controller.SelectedRecord;
        RetryButton.Visibility = record?.Status != "completed" && record?.AudioPath != null ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !occupied;
        AudioButton.Visibility = record?.AudioPath != null && File.Exists(record.AudioPath) ? Visibility.Visible : Visibility.Collapsed;
        if(controller.IsRecording && record?.Status=="recording") liveRecordId=record.Id;
        bool live=(controller.IsRecording || controller.IsConnecting || controller.IsBusy) && record?.Id==liveRecordId && liveRecordId!=null;
        WorkspaceLivePanel.Visibility=live?Visibility.Visible:Visibility.Collapsed;
        WorkspaceLivePlaceholder.Visibility=string.IsNullOrWhiteSpace(controller.FinalText+controller.PartialText)?Visibility.Visible:Visibility.Collapsed;
        WorkspaceLivePlaceholder.Text=controller.IsBusy?"در حال تکمیل متن…":controller.IsRecording && controller.IsConnecting?"صدایت ضبط می‌شود؛ متن پس از اتصال ظاهر می‌شود.":controller.IsRecording && controller.IsLiveConnected?"گوش می‌کنم؛ صحبت کن.":controller.IsRecording?"صدایت ضبط می‌شود؛ اتصال زنده برقرار نیست.":"میانبر را نگه دار و صحبت کن.";
        TranscriptReader.Visibility=!live && pane=="read"?Visibility.Visible:Visibility.Collapsed;
        TranscriptEditor.Visibility=!live && pane=="edit"?Visibility.Visible:Visibility.Collapsed;
        SegmentsPane.Visibility=!live && pane=="segments"?Visibility.Visible:Visibility.Collapsed;
        AnalysisPane.Visibility=!live && pane=="analysis"?Visibility.Visible:Visibility.Collapsed;
        DirectionButton.Visibility=!live && pane=="edit"?Visibility.Visible:Visibility.Collapsed;
        TextNav.IsEnabled=EditNav.IsEnabled=SegmentsNav.IsEnabled=AnalysisNav.IsEnabled=!live;
    }
    private void RefreshSelection()
    {
        if (!ready) return;
        bool selected = controller.SelectedRecord != null;
        WelcomePanel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        EditorPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (pane == "segments") RefreshSegments();
        var record = controller.SelectedRecord;
        if (record != observedRecord) { if(observedRecord!=null) observedRecord.PropertyChanged-=RecordChanged; observedRecord=record; if(observedRecord!=null) observedRecord.PropertyChanged+=RecordChanged; }
        RefreshReader();
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => {
            if (controller.SelectedRecord == record && !controller.AnalysisBusy)
                controller.SelectAnalysis((AnalysisAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "summarize");
        }));
    }
    private void RecordChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(TranscriptRecord.Text) && pane == "read" && observedRecord?.Status != "recording") { if(Dispatcher.CheckAccess()) RefreshReader(); else Dispatcher.BeginInvoke(RefreshReader); } }
    private static FlowDirection TextDirection(string text)
    {
        foreach(char character in text) if(char.IsLetter(character)) return character is >= '\u0590' and <= '\u08ff' or >= '\ufb1d' and <= '\ufdff' or >= '\ufe70' and <= '\ufeff' ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return FlowDirection.RightToLeft;
    }
    private void RefreshReader()
    {
        if (!ready) return;
        var document = new FlowDocument { FlowDirection=FlowDirection.LeftToRight, FontFamily=FontFamily, FontSize=15, Foreground=(Brush)FindResource("Paper"), Background=(Brush)FindResource("Surface"), PagePadding=new Thickness(17), LineHeight=27 };
        foreach(string line in (controller.SelectedRecord?.Text??"").Replace("\r\n","\n").Split('\n'))
        {
            if(string.IsNullOrWhiteSpace(line)) continue;
            var direction=TextDirection(line);
            document.Blocks.Add(new Paragraph(new Run(line)) { FlowDirection=direction, TextAlignment=TextAlignment.Left, Margin=new Thickness(0,0,0,14) });
        }
        TranscriptReader.Document=document;
    }
    private void RefreshSegments()
    {
        var record = controller.SelectedRecord;
        if (record == null) { SegmentsList.ItemsSource = null; return; }
        var groups = new List<TranscriptSegment>();
        foreach (var segment in record.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;
            var last = groups.LastOrDefault();
            bool sentenceEnd = last != null && last.Text.TrimEnd().Length > 65 && ".!?؟".Contains(last.Text.TrimEnd()[^1]);
            if (last != null && last.Speaker == segment.Speaker && last.Language == segment.Language && segment.StartMs-last.EndMs < 1800 && segment.EndMs-last.StartMs < 20000 && last.Text.Length < 360 && !sentenceEnd)
            {
                bool join = char.IsWhiteSpace(last.Text[^1]) || char.IsWhiteSpace(segment.Text[0]) || ".,!?؟،؛:;)»".Contains(segment.Text[0]) || last.Text[^1] == '\u200c';
                last.Text += (join ? "" : " ") + segment.Text; last.EndMs = segment.EndMs;
            }
            else groups.Add(new TranscriptSegment { Text=segment.Text, StartMs=segment.StartMs, EndMs=segment.EndMs, Speaker=segment.Speaker, Language=segment.Language });
        }
        SegmentsList.ItemsSource = groups.Select(segment => new {
            Text = segment.Text,
            Speaker = record.SpeakerNames.TryGetValue(segment.Speaker, out var name) ? name : string.IsNullOrWhiteSpace(segment.Speaker) ? "گوینده" : "گویندهٔ " + segment.Speaker,
            Time = TimeSpan.FromMilliseconds(Math.Max(0,segment.StartMs)).ToString(@"hh\:mm\:ss") + " – " + TimeSpan.FromMilliseconds(Math.Max(0,segment.EndMs)).ToString(@"hh\:mm\:ss"),
            Language = segment.Language
            ,Direction = TextDirection(segment.Text), Alignment = TextAlignment.Left
        }).ToList();
    }
    private void SetNav(Button button, bool selected) { button.Style = (Style)FindResource(selected ? "PrimaryButton" : "QuietButton"); }
    private void SetPane(string value)
    {
        pane = value; TranscriptEditor.Visibility = value == "edit" ? Visibility.Visible : Visibility.Collapsed;
        TranscriptReader.Visibility = value == "read" ? Visibility.Visible : Visibility.Collapsed;
        DirectionButton.Visibility = value == "edit" ? Visibility.Visible : Visibility.Collapsed;
        SegmentsPane.Visibility = value == "segments" ? Visibility.Visible : Visibility.Collapsed;
        AnalysisPane.Visibility = value == "analysis" ? Visibility.Visible : Visibility.Collapsed;
        SetNav(TextNav,value == "read"); SetNav(EditNav,value == "edit"); SetNav(SegmentsNav,value == "segments"); SetNav(AnalysisNav,value == "analysis");
        if(value == "read") RefreshReader();
        if (value == "segments") RefreshSegments();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if(SearchPlaceholder!=null) SearchPlaceholder.Visibility=string.IsNullOrEmpty((sender as TextBox)?.Text)?Visibility.Visible:Visibility.Collapsed;
        if (!ready) return;
        string query = SearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(controller.Records);
        view.Filter = item => item is TranscriptRecord r && (string.IsNullOrEmpty(query) || r.Title.Contains(query,StringComparison.OrdinalIgnoreCase) || r.Text.Contains(query,StringComparison.OrdinalIgnoreCase));
        view.Refresh();
        RefreshEmptyHistory();
    }
    private void RefreshEmptyHistory()
    {
        bool firstNote=controller.Records.Count==0;
        bool noMatches=CollectionViewSource.GetDefaultView(controller.Records).IsEmpty;
        EmptyHistory.Visibility=noMatches?Visibility.Visible:Visibility.Collapsed;
        EmptyHistory.Text=firstNote?"هنوز یادداشتی نداری":"یادداشتی پیدا نشد.\nبا واژهٔ دیگری جست‌وجو کن.";
        WelcomeTitle.Text=firstNote?"از یک جمله شروع کن":noMatches?"یادداشتی پیدا نشد":"یادداشتی را انتخاب کن";
        WelcomeDescription.Text=firstNote?"فارسی، انگلیسی یا ترکیبی از هر دو صحبت کن؛ نوا هم‌زمان می‌نویسد.":noMatches?"با واژهٔ دیگری جست‌وجو کن یا عبارت جست‌وجو را پاک کن.":"از فهرست کنار صفحه، یادداشتی را برای خواندن یا ویرایش انتخاب کن.";
    }
    private void LoadSettings()
    {
        var settings = controller.Settings;
        loadingCharacters=true;
        try { CharacterPicker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,controller.CharacterId); }
        finally { loadingCharacters=false; }
        EndpointBox.Text = settings.BaseUrl; ApiKeyBox.Password = settings.ApiKey; ProxyBox.Password = settings.ProxyUrl; HotkeyBox.Text = settings.Hotkey;
        MicrophoneBox.ItemsSource = controller.Microphones; MicrophoneBox.SelectedValue = settings.MicrophoneId;
        LiveCheck.IsChecked = true; DiarizationCheck.IsChecked = settings.Diarization; SoundsCheck.IsChecked = settings.Sounds;
        AutoCopyCheck.IsChecked = settings.AutoCopy; AutoPasteCheck.IsChecked = settings.AutoPaste; KeepAudioCheck.IsChecked = settings.KeepAudio; StartupCheck.IsChecked = settings.StartWithWindows;
        SettingsFeedback.Text = "";
    }
    private void CharacterSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!ready || loadingCharacters || CharacterPicker.SelectedItem is not CharacterDefinition character || character.Id==controller.CharacterId) return;
        controller.SelectCharacter(character.Id);
        if(controller.CharacterId!=character.Id)
        {
            loadingCharacters=true;
            try { CharacterPicker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,controller.CharacterId); }
            finally { loadingCharacters=false; }
        }
    }
    private async void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var proposed = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(controller.Settings))!;
        proposed.CharacterId=CharacterCatalog.Normalize(CharacterPicker.SelectedValue as string??controller.CharacterId);
        proposed.BaseUrl = EndpointBox.Text.Trim().TrimEnd('/'); proposed.ApiKey = ApiKeyBox.Password.Trim(); proposed.ProxyUrl = ProxyBox.Password.Trim(); proposed.Hotkey = HotkeyBox.Text.Trim();
        proposed.MicrophoneId = MicrophoneBox.SelectedValue is int microphone ? microphone : -1;
        proposed.LiveTranscription = true; proposed.Diarization = DiarizationCheck.IsChecked == true; proposed.Sounds = SoundsCheck.IsChecked == true;
        proposed.AutoCopy = AutoCopyCheck.IsChecked == true; proposed.AutoPaste = AutoPasteCheck.IsChecked == true; proposed.KeepAudio = KeepAudioCheck.IsChecked == true; proposed.StartWithWindows = StartupCheck.IsChecked == true;
        savingSettings = true; UpdateState();
        try { bool saved = await controller.SaveSettingsAsync(proposed); SettingsFeedback.Text = saved ? "تنظیمات ذخیره شد" : "تنظیمات ذخیره نشد"; }
        finally { savingSettings = false; UpdateState(); }
    }
    private async void TestConnectionClick(object sender, RoutedEventArgs e) { testingConnection = true; UpdateState(); try { await controller.TestConnectionAsync(); SettingsFeedback.Text = controller.ConnectionLabel; } finally { testingConnection = false; UpdateState(); } }
    private void HotkeyFocus(object sender, KeyboardFocusChangedEventArgs e) => HotkeyHelp.Text = "میانبر دلخواهت را بزن. برای انصراف، Escape را بزن.";
    private void HotkeyBlur(object sender, KeyboardFocusChangedEventArgs e) => HotkeyHelp.Text = "برای تغییر میانبر اینجا کلیک کن.";
    private void HotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { HotkeyBox.Text = controller.Settings.Hotkey; Keyboard.ClearFocus(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.F12) { HotkeyHelp.Text = "F12 قابل انتخاب نیست؛ کلید دیگری بزن."; return; }
        var pieces = new List<string>(); ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0) pieces.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) pieces.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) pieces.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) { HotkeyHelp.Text = "ترکیب‌های Windows قابل انتخاب نیستند؛ Ctrl یا Alt را امتحان کن."; return; }
        pieces.Add(key.ToString()); HotkeyBox.Text = string.Join("+",pieces); HotkeyHelp.Text = "میانبر انتخاب شد. برای اعمال، تنظیمات را ذخیره کن.";
    }
    private async void SpeakersClick(object sender, RoutedEventArgs e)
    {
        var record = controller.SelectedRecord; if (record == null) return;
        var speakers = record.Segments.Select(s => s.Speaker).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (speakers.Count == 0) { MessageBox.Show(this,"در این یادداشت گوینده‌ای تشخیص داده نشده است.","نام گوینده‌ها",MessageBoxButton.OK,MessageBoxImage.Information); return; }
        var dialog = new Window { Owner=this, Title="نام گوینده‌ها", ShowInTaskbar=false, Width=380, SizeToContent=SizeToContent.Height, MaxHeight=620, WindowStartupLocation=WindowStartupLocation.CenterOwner, ResizeMode=ResizeMode.NoResize, FlowDirection=FlowDirection.RightToLeft, Background=(Brush)FindResource("Ink"), Foreground=(Brush)FindResource("Paper") };
        ShellWindowPolicy.Attach(dialog);
        var stack = new StackPanel { Margin=new Thickness(22) }; stack.Children.Add(new TextBlock { Text="گوینده‌ها را نام‌گذاری کن", FontSize=20, Margin=new Thickness(0,0,0,16) });
        var entries = new Dictionary<string,TextBox>();
        foreach (var speaker in speakers) { stack.Children.Add(new TextBlock { Text="گویندهٔ " + speaker, Margin=new Thickness(0,10,0,6) }); var entry=new TextBox { Text=record.SpeakerNames.TryGetValue(speaker,out var name)?name:"", MaxLength=80 }; stack.Children.Add(entry); entries[speaker]=entry; }
        var save=new Button { Content="ذخیرهٔ نام‌ها", Margin=new Thickness(0,22,0,0), Style=(Style)FindResource("PrimaryButton") }; save.Click += (_,_) => { dialog.DialogResult=true; }; stack.Children.Add(save);
        dialog.Content=new ScrollViewer { Content=stack, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        if (dialog.ShowDialog() == true) { await controller.RenameSpeakersAsync(entries.ToDictionary(p=>p.Key,p=>p.Value.Text.Trim()).Where(p=>p.Value.Length>0).ToDictionary(p=>p.Key,p=>p.Value)); RefreshSegments(); }
    }
    private void AnalysisActionChanged(object sender, SelectionChangedEventArgs e)
    {
        string action = (AnalysisAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "summarize";
        if (CustomPrompt != null) CustomPrompt.Visibility = action == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (ready) { controller.SelectAnalysis(action); UpdateState(); }
    }
    private string AnalysisActionLabel() => (AnalysisAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
    {
        "summarize"=>"خلاصه کن",
        "extract_key_points"=>"یافتن نکته‌ها",
        "to_do_list"=>"ساخت فهرست کارها",
        "named_entities"=>"یافتن نام‌ها",
        "summary_per_speaker"=>"خلاصهٔ گوینده‌ها",
        "speaker_quotation"=>"یافتن نقل‌قول‌ها",
        "custom"=>"اجرای درخواست",
        _=>"تحلیل متن"
    };
    private async void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        var action=(AnalysisAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "summarize";
        if (action == "custom" && string.IsNullOrWhiteSpace(CustomPrompt.Text)) { CustomPrompt.Focus(); return; }
        await controller.AnalyzeSelectedAsync(action,action == "custom" ? CustomPrompt.Text.Trim() : null);
    }
    private void ExportClick(object sender, RoutedEventArgs e) { var button=(Button)sender; button.ContextMenu.PlacementTarget=button; button.ContextMenu.IsOpen=true; }
    private void ExportFormatClick(object sender, RoutedEventArgs e) { if (sender is MenuItem item && item.Tag is string format) controller.ExportSelected(format); }
    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        var record=controller.SelectedRecord; if(record==null) return;
        string message="این یادداشت از این دستگاه حذف شود؟";
        if(record.AudioPath is string audioPath)
        {
            try
            {
                bool owned=Path.GetFullPath(audioPath).StartsWith(Path.GetFullPath(controller.Store.AudioDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
                message+=owned?"\nفایل صدای ضبط‌شده هم حذف می‌شود.":"\nفایل اصلیِ واردشده حذف نمی‌شود.";
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if(MessageBox.Show(this,message,"حذف یادداشت",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes) controller.DeleteSelected();
    }
    private async void RetryClick(object sender, RoutedEventArgs e) => await controller.RetrySelectedAsync();
    private async void FormatClick(object sender, RoutedEventArgs e) => await controller.FormatSelectedAsync();
    private void AudioClick(object sender, RoutedEventArgs e) => controller.OpenAudioSelected();
    private void SaveText(object sender, RoutedEventArgs e) { if (ready) controller.SaveRecord(); }
    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e) { if (ready) RefreshSelection(); }
    private void CopyClick(object sender, RoutedEventArgs e) => controller.CopySelected();
    private async void CancelClick(object sender, RoutedEventArgs e) => await controller.CancelAsync();
    private async void ImportClick(object sender, RoutedEventArgs e) => await controller.ImportFileAsync();
    private void ShowOverlayClick(object sender, RoutedEventArgs e) => controller.ShowOverlay();
    private void HistoryClick(object sender, RoutedEventArgs e) => ShowTab("history");
    private void SettingsClick(object sender, RoutedEventArgs e) => ShowTab("settings");
    private void TextTabClick(object sender, RoutedEventArgs e) => SetPane("read");
    private void EditTabClick(object sender, RoutedEventArgs e) => SetPane("edit");
    private void DirectionClick(object sender, RoutedEventArgs e) => TranscriptEditor.FlowDirection=TranscriptEditor.FlowDirection==FlowDirection.RightToLeft?FlowDirection.LeftToRight:FlowDirection.RightToLeft;
    private void SegmentsTabClick(object sender, RoutedEventArgs e) => SetPane("segments");
    private void AnalysisTabClick(object sender, RoutedEventArgs e) => SetPane("analysis");
    private void CloseClick(object sender, RoutedEventArgs e) { controller.SaveRecord(); Hide(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState=WindowState.Minimized;
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } }
}
