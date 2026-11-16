using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using Nava.Services;

namespace Nava.Views;

public partial class OverlayWindow : Window
{
    private const double MiniWidth=40, MiniHeight=44;
    private readonly AppController controller;
    private readonly DispatcherTimer animation;
    private readonly DispatcherTimer collapseTimer;
    private readonly PushToTalkGesture pushToTalk;
    private readonly TextBlock miniTip=new() { MaxWidth=300, TextWrapping=TextWrapping.Wrap };
    private DispatcherOperation? pendingPresentation;
    private bool presentationDirty;
    private double phase, anchorLeft=double.NaN, anchorTop=double.NaN;
    private bool expanded, wasActive, miniPressed, miniDragged, dragging, layingOut, closed;
    private Point miniPressPoint;
    private DateTime collapseAt=DateTime.MaxValue, lastKeyboardInteraction=DateTime.MinValue;
    private string lastError="";
    public bool IsExpanded=>expanded;
    public double MiniLeft=>anchorLeft;
    public double MiniTop=>anchorTop;
    private bool Activity=>controller.IsRecording || controller.IsConnecting || controller.IsBusy;

    public OverlayWindow(AppController controller)
    {
        InitializeComponent(); this.controller=controller; DataContext=controller;
        ShellWindowPolicy.Attach(this);
        MiniDock.ToolTip=new ToolTip { Content=miniTip };
        pushToTalk=new PushToTalkGesture(this,controller,RecordButton);
        controller.PropertyChanged+=Changed;
        for(int i=0;i<27;i++) Bars.Children.Add(new Rectangle { Width=3, Height=4, RadiusX=1.5, RadiusY=1.5, Fill=(Brush)FindResource("Mint"), Margin=new Thickness(1.5,0,1.5,0), VerticalAlignment=VerticalAlignment.Center });
        animation=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) }; animation.Tick+=Animate;
        collapseTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(250) }; collapseTimer.Tick+=AutoCollapse;
        IsVisibleChanged+=(_,_)=>RefreshTimers();
        MouseEnter+=(_,_)=>RefreshIdleOpacity();
        MouseLeave+=(_,_)=> { RefreshIdleOpacity(); if(expanded && !Activity) ScheduleCollapse(TimeSpan.FromSeconds(3)); };
        PreviewKeyDown+=KeyInteraction;
        DpiChanged+=(_,_)=>LayoutPresentation();
        Closed+=(_,_)=> { closed=true; pendingPresentation?.Abort(); pendingPresentation=null; pushToTalk.Dispose(); animation.Stop(); collapseTimer.Stop(); controller.PropertyChanged-=Changed; };
        Changed(null,new PropertyChangedEventArgs(null));
    }
    public void RestorePosition(double miniLeft,double miniTop)
    {
        if(!double.IsNaN(miniLeft) && !double.IsNaN(miniTop) && miniLeft!=-1 && miniTop!=-1) { Left=miniLeft; Top=miniTop; }
        var work=WorkArea();
        anchorLeft=miniLeft==-1 || double.IsNaN(miniLeft)?work.Right-MiniWidth-18:miniLeft;
        anchorTop=miniTop==-1 || double.IsNaN(miniTop)?work.Bottom-MiniHeight-18:miniTop;
        ClampAnchor(work); LayoutPresentation();
    }
    public void Expand(bool manual=true)
    {
        if(closed) return;
        expanded=true; ExpandedCard.Visibility=Visibility.Visible;
        if(!Activity) ScheduleCollapse(TimeSpan.FromSeconds(manual?8:controller.HasError?8:3));
        else collapseAt=DateTime.MaxValue;
        LayoutPresentation(); RefreshTimers(); RefreshIdleOpacity();
    }
    public void Collapse()
    {
        // The microphone never disappears behind an idle-looking ghost while still held.
        if(closed || controller.IsRecording || controller.IsConnecting) return;
        expanded=false; collapseAt=DateTime.MaxValue; ExpandedCard.Visibility=Visibility.Collapsed;
        if(IsKeyboardFocusWithin) Keyboard.ClearFocus(); LayoutPresentation(); RefreshTimers(); RefreshIdleOpacity();
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(AppController.Level) or nameof(AppController.TimeLabel) or nameof(AppController.Status) or nameof(AppController.Hint) or nameof(AppController.ConnectionLabel) or nameof(AppController.AnalysisText)) return;
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(()=>Changed(sender,e)); return; }
        if(closed) return;
        if(e.PropertyName is nameof(AppController.LiveText) or nameof(AppController.FinalText) or nameof(AppController.PartialText))
        {
            presentationDirty=true;
            if(pendingPresentation==null || pendingPresentation.Status!=DispatcherOperationStatus.Pending)
                pendingPresentation=Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(FlushPresentationUpdate));
            return;
        }
        pendingPresentation?.Abort(); pendingPresentation=null; presentationDirty=false;
        bool active=Activity;
        if(active && !wasActive) Expand(false);
        if(!active && wasActive) { lastKeyboardInteraction=DateTime.MinValue; ScheduleCollapse(TimeSpan.FromSeconds(controller.HasError?8:3)); }
        wasActive=active;
        if(controller.ErrorMessage!=lastError)
        {
            lastError=controller.ErrorMessage;
            if(controller.HasError && !active) { Expand(false); ScheduleCollapse(TimeSpan.FromSeconds(8)); }
        }
        MicDot.Visibility=controller.IsRecording?Visibility.Collapsed:Visibility.Visible;
        StopSquare.Visibility=controller.IsRecording?Visibility.Visible:Visibility.Collapsed;
        RecordButton.Background=(Brush)FindResource(controller.IsRecording?"Coral":"Mint");
        RecordButton.IsEnabled=(!controller.IsBusy && !controller.AnalysisBusy) || controller.IsRecording;
        ImportButton.IsEnabled=!controller.IsBusy && !controller.AnalysisBusy && !controller.IsRecording;
        WavePanel.Visibility=controller.IsRecording?Visibility.Visible:Visibility.Collapsed;
        CancelButton.Visibility=Activity || controller.AnalysisBusy?Visibility.Visible:Visibility.Collapsed;
        TranscriptPanel.Visibility=active || !string.IsNullOrWhiteSpace(controller.LiveText)?Visibility.Visible:Visibility.Collapsed;
        ConnectingProgress.Visibility=controller.IsConnecting?Visibility.Visible:Visibility.Collapsed;
        LiveCaption.Text=controller.IsBusy?"نهایی‌سازی واژه‌های آخر":active?"متن زنده":"آخرین نوشته";
        LiveConnection.Text=controller.IsConnecting?"در حال اتصال…":controller.IsLiveConnected?"وصل است • زنده":controller.IsRecording?"در حال ضبط":controller.IsBusy?"نهایی‌سازی…":"آماده";
        LiveIndicator.Fill=(Brush)FindResource(controller.IsLiveConnected?"Mint":"Coral");
        UpdateTranscriptVisuals();
        LivePlaceholder.Text=controller.IsConnecting?"دارم وصل می‌شوم؛ صدایت از همین لحظه ضبط می‌شود.":controller.IsLiveConnected?"گوش می‌کنم… صحبت کن تا همین‌جا بنویسم.":controller.IsRecording?"صدایت ضبط می‌شود؛ منتظر اتصال زنده هستم.":"دارم آخرین کلمه‌ها را نهایی می‌کنم…";
        ReleaseHint.Visibility=controller.IsRecording?Visibility.Visible:Visibility.Collapsed;
        MiniBadge.Visibility=controller.HasError?Visibility.Visible:Visibility.Collapsed;
        miniTip.Text=controller.HasError?controller.ErrorMessage+"\nبرای دیدن جزئیات کلیک کن.":controller.IsRecording?"دارم گوش می‌کنم؛ برای پایان میانبر را رها کن.":"نوا • "+controller.HotkeyLabel+" را نگه دار و صحبت کن.\nکلیک: باز کردن • کشیدن: جابه‌جایی";
        LayoutPresentation(); RefreshTimers(); RefreshIdleOpacity();
    }
    private void UpdateTranscriptVisuals()
    {
        bool active=Activity;
        TranscriptPanel.Visibility=active || !string.IsNullOrWhiteSpace(controller.LiveText)?Visibility.Visible:Visibility.Collapsed;
        bool haveTokens=!string.IsNullOrEmpty(controller.FinalText)||!string.IsNullOrEmpty(controller.PartialText);
        FinalLiveRun.Text=active && haveTokens?controller.FinalText:controller.LiveText;
        PartialLiveRun.Text=active?controller.PartialText:"";
        LivePlaceholder.Visibility=string.IsNullOrWhiteSpace(FinalLiveRun.Text+PartialLiveRun.Text)?Visibility.Visible:Visibility.Collapsed;
    }
    private void FlushPresentationUpdate()
    {
        var pending=pendingPresentation; pendingPresentation=null;
        if(pending?.Status==DispatcherOperationStatus.Pending) pending.Abort();
        if(closed || !presentationDirty) return;
        presentationDirty=false; UpdateTranscriptVisuals();
        if(expanded) { LayoutPresentation(); LiveScroller.ScrollToEnd(); }
    }
    private void ScheduleCollapse(TimeSpan delay) { collapseAt=DateTime.UtcNow+delay; RefreshTimers(); }
    private void AutoCollapse(object? sender,EventArgs e)
    {
        if(!expanded || Activity || DateTime.UtcNow<collapseAt) return;
        bool recentKeys=IsKeyboardFocusWithin && DateTime.UtcNow-lastKeyboardInteraction<TimeSpan.FromSeconds(8);
        if(IsMouseOver || IsMouseCaptureWithin || recentKeys) return;
        Collapse();
    }
    private async void KeyInteraction(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape)
        {
            e.Handled=true;
            if(controller.IsRecording || controller.IsConnecting) await controller.CancelAsync();
            Collapse(); return;
        }
        if(!e.IsRepeat && e.Key is not (Key.Space or Key.Enter)) { lastKeyboardInteraction=DateTime.UtcNow; if(expanded && !Activity) ScheduleCollapse(TimeSpan.FromSeconds(8)); }
    }
    private void RefreshTimers()
    {
        if(closed) return;
        if(expanded && IsVisible && controller.IsRecording) animation.Start(); else animation.Stop();
        if(expanded && IsVisible) collapseTimer.Start(); else collapseTimer.Stop();
    }
    private void RefreshIdleOpacity() => MiniDock.Opacity=MiniDock.IsMouseOver || Activity || expanded?1:.65;
    private void Animate(object? sender,EventArgs e)
    {
        phase+=.25; double level=Math.Clamp(controller.Level,0,1);
        for(int i=0;i<Bars.Children.Count;i++) ((Rectangle)Bars.Children[i]).Height=3+level*(6+14*Math.Abs(Math.Sin(i*.63+phase)));
        FaceScale.ScaleY=SystemParameters.ClientAreaAnimation?1+level*.06:1;
    }
    private Rect WorkArea()
    {
        var source=PresentationSource.FromVisual(this);
        if(source?.CompositionTarget==null) return SystemParameters.WorkArea;
        var screen=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var tl=source.CompositionTarget.TransformFromDevice.Transform(new Point(screen.Left,screen.Top));
        var br=source.CompositionTarget.TransformFromDevice.Transform(new Point(screen.Right,screen.Bottom));
        return new Rect(tl,br);
    }
    private void ClampAnchor(Rect work)
    {
        anchorLeft=Math.Clamp(anchorLeft,work.Left,Math.Max(work.Left,work.Right-MiniWidth));
        anchorTop=Math.Clamp(anchorTop,work.Top,Math.Max(work.Top,work.Bottom-MiniHeight));
    }
    private void LayoutPresentation()
    {
        if(layingOut || closed || dragging) return; layingOut=true;
        try
        {
            var work=WorkArea();
            if(double.IsNaN(anchorLeft) || double.IsNaN(anchorTop)) { anchorLeft=work.Right-MiniWidth-18; anchorTop=work.Bottom-MiniHeight-18; }
            ClampAnchor(work);
            if(!expanded)
            {
                ExpandedCard.Visibility=Visibility.Collapsed; Canvas.SetLeft(MiniDock,0); Canvas.SetTop(MiniDock,0);
                PresentationRoot.Width=Width=MiniWidth; PresentationRoot.Height=Height=MiniHeight; Left=anchorLeft; Top=anchorTop;
                return;
            }
            ExpandedCard.Visibility=Visibility.Visible;
            double outerWidth=Math.Min(Activity?420:348,work.Width);
            ExpandedCard.Width=Math.Max(200,outerWidth-20);
            ExpandedCard.Measure(new Size(ExpandedCard.Width,double.PositiveInfinity));
            double cardHeight=Math.Ceiling(ExpandedCard.DesiredSize.Height);
            double outerHeight=cardHeight+20;
            double cardLeft=Math.Clamp(anchorLeft+MiniWidth-outerWidth,work.Left,Math.Max(work.Left,work.Right-outerWidth));
            double cardTop=Math.Clamp(anchorTop-outerHeight,work.Top,Math.Max(work.Top,work.Bottom-outerHeight));
            double windowLeft=Math.Min(cardLeft,anchorLeft), windowTop=Math.Min(cardTop,anchorTop);
            double windowRight=Math.Max(cardLeft+outerWidth,anchorLeft+MiniWidth), windowBottom=Math.Max(cardTop+outerHeight,anchorTop+MiniHeight);
            Canvas.SetLeft(ExpandedCard,cardLeft-windowLeft+10); Canvas.SetTop(ExpandedCard,cardTop-windowTop+10);
            Canvas.SetLeft(MiniDock,anchorLeft-windowLeft); Canvas.SetTop(MiniDock,anchorTop-windowTop);
            PresentationRoot.Width=Width=windowRight-windowLeft; PresentationRoot.Height=Height=windowBottom-windowTop;
            Left=windowLeft; Top=windowTop;
        }
        finally { layingOut=false; }
    }
    private void SaveDraggedAnchor()
    {
        anchorLeft=Left+Canvas.GetLeft(MiniDock); anchorTop=Top+Canvas.GetTop(MiniDock);
        ClampAnchor(WorkArea()); LayoutPresentation(); controller.SaveOverlayPosition(anchorLeft,anchorTop);
    }
    private void MiniMouseDown(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true; miniPressed=true; miniDragged=false; miniPressPoint=e.GetPosition(this); MiniDock.CaptureMouse();
    }
    private void MiniMouseMove(object sender,MouseEventArgs e)
    {
        if(!miniPressed || dragging || e.LeftButton!=MouseButtonState.Pressed) return;
        var current=e.GetPosition(this);
        if(Math.Abs(current.X-miniPressPoint.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y-miniPressPoint.Y)<SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled=true; miniDragged=true; dragging=true; MiniDock.ReleaseMouseCapture();
        try { DragMove(); SaveDraggedAnchor(); } catch(InvalidOperationException) { }
        finally { dragging=false; miniPressed=false; LayoutPresentation(); }
    }
    private void MiniMouseUp(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true; bool click=miniPressed && !miniDragged; miniPressed=false; MiniDock.ReleaseMouseCapture();
        if(click) ToggleMini();
    }
    private void MiniLostCapture(object sender,MouseEventArgs e) { if(!dragging) miniPressed=false; }
    private void MiniClick(object sender,RoutedEventArgs e) => ToggleMini();
    private void ToggleMini() { if(expanded) Collapse(); else Expand(); }
    private void MiniMouseEnter(object sender,MouseEventArgs e) => RefreshIdleOpacity();
    private void MiniMouseLeave(object sender,MouseEventArgs e) => RefreshIdleOpacity();
    private void DragHeader(object sender,MouseButtonEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed) return;
        for(DependencyObject? node=e.OriginalSource as DependencyObject;node!=null;node=VisualTreeHelper.GetParent(node)) if(node is Button) return;
        dragging=true;
        try { DragMove(); SaveDraggedAnchor(); } catch(InvalidOperationException) { }
        finally { dragging=false; LayoutPresentation(); }
    }
    private async void CancelClick(object sender,RoutedEventArgs e) => await controller.CancelAsync();
    private void HistoryClick(object sender,RoutedEventArgs e) => controller.OpenWorkspace();
    private void SettingsClick(object sender,RoutedEventArgs e) => controller.OpenWorkspace("settings");
    private async void ImportClick(object sender,RoutedEventArgs e) => await controller.ImportFileAsync();
    private async void HideClick(object sender,RoutedEventArgs e) { if(controller.IsRecording || controller.IsConnecting) await controller.CancelAsync(); Collapse(); }
}
