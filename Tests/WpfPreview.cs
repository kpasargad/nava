using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nava.Core;
using Nava.Views;

namespace Nava.Tests;

/// <summary>Renders only Nava's own detached visual trees with synthetic data. Never creates or captures a desktop window.</summary>
public static class WpfPreview
{
    public static void Run(string rootOutputDir)
    {
        string output = Path.GetFullPath(rootOutputDir);
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() => {
            string? previousRoot = Environment.GetEnvironmentVariable("NAVA_DATA_DIR");
            App? app = null; AppController? controller = null; OverlayWindow? overlay = null; WorkspaceWindow? workspace = null;
            // Application queues Startup when constructed. Suppress dispatcher pumping for this
            // entire thread so Nava.App.OnStartup can never execute or initialize desktop services.
            using var disabledProcessing = Dispatcher.CurrentDispatcher.DisableProcessing();
            try
            {
                Environment.SetEnvironmentVariable("NAVA_DATA_DIR", Path.Combine(output,"wpf-smoke-data",Guid.NewGuid().ToString("N")));
                app = new App(); app.InitializeComponent();
                VerifyDurationBindingUpdates();
                RenderCharacterGallery(Path.Combine(output,"character-gallery.png"));
                controller = new AppController(); // Enumerates device names only; Initialize() and recording are deliberately never called.
                controller.Settings.ApiKey = "synthetic-preview-key";
                controller.Settings.ProxyUrl = "http://demo:synthetic@127.0.0.1:12345";
                State(controller, nameof(AppController.ConnectionLabel), "پیش‌نمایش آزمایشی • بدون اتصال شبکه");
                var sample = SampleRecord(); controller.Records.Add(sample); controller.SelectedRecord = sample;
                controller.Records.Add(new TranscriptRecord { Title="ایده‌های مسیر خانه", CreatedAt=new DateTime(2026,11,14,18,25,0), Text="یک راه ساده برای شروع هفته: کارهای کوچک و روشن، با زمان کافی برای فکر کردن.", DurationMs=43000 });
                controller.Records.Add(new TranscriptRecord { Title="Product stand-up", CreatedAt=new DateTime(2026,11,13,9,40,0), Text="We discussed the next release, customer feedback and the first usability session.", DurationMs=97000 });
                overlay = new OverlayWindow(controller);
                workspace = new WorkspaceWindow(controller);
                if(overlay.ShowInTaskbar || workspace.ShowInTaskbar) throw new InvalidOperationException("A Nava main window is still enabled for the taskbar/Alt+Tab list.");
                if(!overlay.IsEnabled || !workspace.IsEnabled) throw new InvalidOperationException("Shell visibility policy disabled window interaction.");
                VerifyCoalescedTranscriptUpdate(overlay,controller);
                var hosts = new Dictionary<Window,ContentControl>();

                overlay.RestorePosition(-1,-1);
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-preview.png"));
                MiniDockInputRegression.Verify(overlay,hosts[overlay],Path.Combine(output,"mini-input-regression.json"));
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-mini-preview.png"));
                overlay.Expand();
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-expanded-preview.png"));
                overlay.Collapse();
                sample.Status="recording"; controller.SelectedRecord=sample;
                State(controller,nameof(AppController.IsRecording),true);
                State(controller,nameof(AppController.IsConnecting),true);
                State(controller,nameof(AppController.Status),"در حال اتصال…");
                State(controller,nameof(AppController.Hint),"صدایت ضبط می‌شود؛ برای پایان رها کن");
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-connecting-preview.png"));
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-connecting-preview.png"));
                State(controller,nameof(AppController.IsConnecting),false);
                State(controller,nameof(AppController.IsLiveConnected),true);
                State(controller,nameof(AppController.Status),"دارم می‌نویسم");
                State(controller,nameof(AppController.Hint),"برای پایان، میانبر یا دکمه را رها کن");
                State(controller,nameof(AppController.Level),.48);
                State(controller,nameof(AppController.TimeLabel),"00:18");
                State(controller,nameof(AppController.FinalText),"سلام! امروز دربارهٔ نسخهٔ تازه صحبت می‌کنیم. ");
                State(controller,nameof(AppController.PartialText),"اول از بازخوردهای مشتری‌ها شروع کنیم");
                State(controller,nameof(AppController.LiveText),controller.FinalText+controller.PartialText);
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-recording-preview.png"));
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-live-preview.png"));
                State(controller,nameof(AppController.IsRecording),false);
                State(controller,nameof(AppController.IsLiveConnected),false);
                State(controller,nameof(AppController.FinalText),sample.Text);
                State(controller,nameof(AppController.PartialText),"");
                State(controller,nameof(AppController.LiveText),sample.Text);
                sample.Status="completed"; controller.SelectedRecord=sample;
                State(controller,nameof(AppController.Level),0d);
                State(controller,nameof(AppController.Status),"متن آماده است");
                State(controller,nameof(AppController.Hint),"در یادداشت‌ها ذخیره شد");
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-completed-preview.png"));
                overlay.Collapse();
                Render(overlay,hosts,overlay.Width,overlay.Height,Path.Combine(output,"overlay-completed-mini-preview.png"));

                workspace.ShowTab("history");
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-preview.png"));
                Render(workspace,hosts,780,620,Path.Combine(output,"workspace-minimum-preview.png"));
                if(workspace.FindName("SearchBox") is TextBox search)
                {
                    search.Text="no-matching-synthetic-note";
                    Render(workspace,hosts,780,620,Path.Combine(output,"workspace-search-empty-preview.png"));
                    search.Clear();
                    controller.SelectedRecord=sample;
                }
                ClickLocalTab(workspace,"EditNav");
                Render(workspace,hosts,780,620,Path.Combine(output,"workspace-editor-preview.png"));
                ClickLocalTab(workspace,"SegmentsNav");
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-segments-preview.png"));
                ClickLocalTab(workspace,"AnalysisNav");
                controller.SelectAnalysis("summarize");
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-analysis-preview.png"));
                sample.ApiKind="soniox"; controller.SelectedRecord=sample;
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-native-capability-preview.png"));
                sample.ApiKind="custom"; controller.SelectedRecord=sample;
                workspace.ShowTab("settings");
                Render(workspace,hosts,990,760,Path.Combine(output,"settings-preview.png"));
                if (workspace.FindName("SettingsPanel") is ScrollViewer settingsScroll) settingsScroll.ScrollToBottom();
                Render(workspace,hosts,990,760,Path.Combine(output,"settings-bottom-preview.png"));
                workspace.ShowTab("history"); controller.SelectedRecord=null; controller.Records.Clear();
                State(controller,nameof(AppController.Status),"آماده‌ام");
                State(controller,nameof(AppController.Hint),"میانبر را نگه دار و صحبت کن");
                Render(workspace,hosts,990,760,Path.Combine(output,"workspace-empty-preview.png"));
                File.WriteAllText(Path.Combine(output,"visual-smoke.txt"),"Own-control WPF RenderTargetBitmap previews completed. Synthetic records only. No Window.Show, Application.Run, desktop capture, recording, network requests, hotkey registration or startup changes.\n");
            }
            catch(Exception ex) { failure=ex; }
            finally
            {
                try { workspace?.ForceClose(); overlay?.Close(); controller?.Dispose(); } catch(Exception ex) { failure ??= ex; }
                Environment.SetEnvironmentVariable("NAVA_DATA_DIR",previousRoot);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new TimeoutException("WPF own-control rendering did not finish within 55 seconds.");
        if (failure != null) throw new InvalidOperationException("WPF own-control rendering failed.",failure);
    }
    private static void State(AppController controller,string property,object value) => typeof(AppController).GetProperty(property,BindingFlags.Public|BindingFlags.Instance)!.SetValue(controller,value);
    private static void VerifyDurationBindingUpdates()
    {
        var record=new TranscriptRecord();
        var label=new TextBlock();
        BindingOperations.SetBinding(label,TextBlock.TextProperty,new Binding(nameof(TranscriptRecord.DurationLabel)) { Source=record });
        label.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
        if(label.Text!="00:00") throw new InvalidOperationException("Duration binding did not initialize.");
        record.DurationMs=65000;
        label.Measure(new Size(200,40)); label.Arrange(new Rect(0,0,200,40)); label.UpdateLayout();
        if(label.Text!="01:05") throw new InvalidOperationException("Recording duration label did not update after capture completed.");
    }
    private static void VerifyCoalescedTranscriptUpdate(OverlayWindow overlay,AppController controller)
    {
        bool wasExpanded=overlay.IsExpanded;
        string beforeFinal=controller.FinalText,beforePartial=controller.PartialText,beforeLive=controller.LiveText;
        bool beforeRecording=controller.IsRecording;
        var field=typeof(OverlayWindow).GetField("pendingPresentation",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var flush=typeof(OverlayWindow).GetMethod("FlushPresentationUpdate",BindingFlags.NonPublic|BindingFlags.Instance)!;
        try
        {
            State(controller,nameof(AppController.IsRecording),true);
            State(controller,nameof(AppController.FinalText),"synthetic final ");
            object? first=field.GetValue(overlay);
            State(controller,nameof(AppController.PartialText),"interim");
            State(controller,nameof(AppController.LiveText),"synthetic final interim");
            if(first==null || !ReferenceEquals(first,field.GetValue(overlay))) throw new InvalidOperationException("Live properties scheduled duplicate presentation operations.");
            flush.Invoke(overlay,null);
            var final=overlay.FindName("FinalLiveRun") as System.Windows.Documents.Run;
            var partial=overlay.FindName("PartialLiveRun") as System.Windows.Documents.Run;
            if(final?.Text!="synthetic final " || partial?.Text!="interim") throw new InvalidOperationException("Coalesced presentation lost the latest final/interim text.");
        }
        finally
        {
            State(controller,nameof(AppController.FinalText),beforeFinal); State(controller,nameof(AppController.PartialText),beforePartial); State(controller,nameof(AppController.LiveText),beforeLive);
            State(controller,nameof(AppController.IsRecording),beforeRecording); flush.Invoke(overlay,null);
            if(!wasExpanded) overlay.Collapse();
        }
    }
    private static void RenderCharacterGallery(string path)
    {
        var ink=new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181F25"));
        var surface=new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26323A"));
        var paper=new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0F2EB"));
        var muted=new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A7B8BF"));
        var content=new Grid(); content.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(new TextBlock { Text="چهره‌های نوا", Foreground=paper, FontFamily=new FontFamily("Segoe UI"), FontSize=25, FontWeight=FontWeights.SemiBold, FlowDirection=FlowDirection.RightToLeft });
        var subtitle=new TextBlock { Text="ده چهره برای نوا، در دو اندازه", Foreground=muted, FontSize=13, Margin=new Thickness(0,9,0,22), FlowDirection=FlowDirection.RightToLeft }; Grid.SetRow(subtitle,1); content.Children.Add(subtitle);
        var cards=new System.Windows.Controls.Primitives.UniformGrid { Columns=5,Rows=2,FlowDirection=FlowDirection.RightToLeft }; Grid.SetRow(cards,2); content.Children.Add(cards);
        foreach(var character in CharacterCatalog.All)
        {
            var faceRow=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,FlowDirection=FlowDirection.LeftToRight,Height=63 };
            faceRow.Children.Add(new CharacterAvatar { CharacterId=character.Id,Width=48,Height=48,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,16,0) });
            faceRow.Children.Add(new CharacterAvatar { CharacterId=character.Id,Width=28,Height=32,VerticalAlignment=VerticalAlignment.Center });
            var stack=new StackPanel { VerticalAlignment=VerticalAlignment.Center }; stack.Children.Add(faceRow);
            stack.Children.Add(new TextBlock { Text=character.Name,Foreground=paper,FontSize=14,FontWeight=FontWeights.SemiBold,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,8,0,0),FlowDirection=FlowDirection.RightToLeft });
            stack.Children.Add(new TextBlock { Text=character.Description,Foreground=muted,FontSize=10.5,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0),FlowDirection=FlowDirection.RightToLeft });
            cards.Children.Add(new Border { Child=stack,Background=surface,CornerRadius=new CornerRadius(15),Padding=new Thickness(11),Margin=new Thickness(6),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(character.Accent)) });
        }
        var host=new Border { Child=content,Background=ink,Padding=new Thickness(24),Width=900,Height=480 };
        host.Measure(new Size(900,480)); host.Arrange(new Rect(0,0,900,480)); host.UpdateLayout();
        var bitmap=new RenderTargetBitmap(900,480,96,96,PixelFormats.Pbgra32); bitmap.Render(host);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output=File.Create(path); encoder.Save(output);
    }
    private static void ClickLocalTab(WorkspaceWindow workspace,string name)
    {
        if (workspace.FindName(name) is not Button button) throw new InvalidOperationException("Missing own-view tab "+name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Render(Window window,Dictionary<Window,ContentControl> hosts,double width,double? height,string path)
    {
        if(window is OverlayWindow)
        {
            typeof(OverlayWindow).GetMethod("FlushPresentationUpdate",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
            width=window.Width; height=window.Height;
        }
        if (!hosts.TryGetValue(window,out var host))
        {
            if (window.Content is not FrameworkElement root) throw new InvalidOperationException("Own view has no visual content.");
            window.Content=null;
            root.DataContext=window.DataContext;
            if(root.ReadLocalValue(FrameworkElement.FlowDirectionProperty)==DependencyProperty.UnsetValue) root.FlowDirection=window.FlowDirection;
            host=new ContentControl { Content=root, HorizontalContentAlignment=HorizontalAlignment.Stretch, VerticalContentAlignment=VerticalAlignment.Stretch, Background=window is OverlayWindow ? Brushes.Transparent : window.Background, FlowDirection=window.FlowDirection, DataContext=window.DataContext };
            TextElementFont(host,window);
            hosts.Add(window,host);
        }
        host.Width=width; host.Height=height??double.NaN;
        host.Measure(new Size(width,height??double.PositiveInfinity));
        double actualHeight=height??Math.Ceiling(host.DesiredSize.Height);
        if (actualHeight<1 || double.IsInfinity(actualHeight) || double.IsNaN(actualHeight)) throw new InvalidOperationException("Own view has invalid height.");
        host.Arrange(new Rect(0,0,width,actualHeight)); host.UpdateLayout();
        host.Measure(new Size(width,actualHeight)); host.Arrange(new Rect(0,0,width,actualHeight)); host.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(width),(int)Math.Ceiling(actualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(host); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path); encoder.Save(stream);
    }
    private static void TextElementFont(ContentControl host,Window window)
    {
        host.FontFamily=window.FontFamily; host.FontSize=window.FontSize; host.Foreground=window.Foreground;
    }
    private static TranscriptRecord SampleRecord() => new() {
        Title="جلسهٔ تیم محصول", CreatedAt=new DateTime(2026,11,15,10,30,0), DurationMs=192000,
        Text="سلام بچه‌ها. امروز می‌خواهیم دربارهٔ تجربهٔ نسخهٔ تازه صحبت کنیم. بازخورد اصلی این بود که شروع کار باید ساده‌تر باشد.\n\nLet's start with the customer feedback. Three people asked for a quicker way to capture their thoughts without switching apps.\n\nپس برای این هفته، اول میانبر ضبط را نهایی می‌کنیم. سارا تجربهٔ شروع کار را بررسی می‌کند و Alex هم نتیجهٔ آزمایش را تا سه‌شنبه می‌فرستد.\n\nGreat. We will review the first prototype together on Tuesday.",
        Segments=new() {
            new() { Text="سلام بچه‌ها. امروز می‌خواهیم دربارهٔ تجربهٔ نسخهٔ تازه صحبت کنیم. بازخورد اصلی این بود که شروع کار باید ساده‌تر باشد.", StartMs=0,EndMs=16000,Speaker="1",Language="fa" },
            new() { Text="Let's start with the customer feedback. Three people asked for a quicker way to capture their thoughts without switching apps.",StartMs=16500,EndMs=31000,Speaker="2",Language="en" },
            new() { Text="پس برای این هفته، اول میانبر ضبط را نهایی می‌کنیم. سارا تجربهٔ شروع کار را بررسی می‌کند و Alex هم نتیجهٔ آزمایش را تا سه‌شنبه می‌فرستد.",StartMs=32000,EndMs=48500,Speaker="1",Language="fa" },
            new() { Text="Great. We will review the first prototype together on Tuesday.",StartMs=49000,EndMs=55000,Speaker="2",Language="en" }
        },
        SpeakerNames=new() { ["1"]="سارا", ["2"]="Alex" },
        Analyses=new() { ["summarize"]="تیم روی ساده‌تر شدن شروع کار و ضبط سریع با میانبر توافق کرد. نمونهٔ اولیه سه‌شنبه بررسی می‌شود.\n\n• سارا: بررسی تجربهٔ شروع کار\n• Alex: ارسال نتیجهٔ آزمایش تا سه‌شنبه\n• تیم: مرور نمونهٔ اولیه در جلسهٔ بعد" }
    };
}
