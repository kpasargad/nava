using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nava.Views;

/// <summary>A held pointer/key owns one PTT gesture; every release path ends it exactly once.</summary>
internal sealed class PushToTalkGesture : IDisposable
{
    private readonly Window window;
    private readonly AppController controller;
    private readonly List<Button> buttons;
    private Button? heldButton;
    private Key heldKey=Key.None;
    private bool disposed;
    public PushToTalkGesture(Window window, AppController controller, params Button[] buttons)
    {
        this.window=window; this.controller=controller; this.buttons=new(buttons);
        foreach(var button in buttons)
        {
            button.PreviewMouseLeftButtonDown+=MouseDown; button.PreviewMouseLeftButtonUp+=MouseUp;
            button.LostMouseCapture+=LostCapture; button.PreviewKeyDown+=KeyDown; button.PreviewKeyUp+=KeyUp;
            button.LostKeyboardFocus+=LostFocus;
            button.ToolTip="دکمهٔ موس، Space یا Enter را نگه دار و صحبت کن؛ برای پایان رها کن.";
        }
        window.Deactivated+=Deactivated;
    }
    private bool CanBegin(Button button) => !disposed && heldButton==null && button.IsEnabled && !controller.IsRecording && !controller.IsBusy && !controller.AnalysisBusy;
    private async void MouseDown(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true; var button=(Button)sender;
        if(!CanBegin(button)) return;
        button.Focus(); heldButton=button; heldKey=Key.None;
        if(!Mouse.Capture(button,CaptureMode.Element)) { heldButton=null; return; }
        await controller.StartPushToTalkAsync();
    }
    private async void MouseUp(object sender,MouseButtonEventArgs e) { e.Handled=true; if(heldButton==sender && heldKey==Key.None) await ReleaseAsync(); }
    private async void LostCapture(object sender,MouseEventArgs e) { if(heldButton==sender && heldKey==Key.None) await ReleaseAsync(); }
    private async void KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled=true; var button=(Button)sender;
        if(e.IsRepeat || !CanBegin(button)) return;
        heldButton=button; heldKey=e.Key;
        await controller.StartPushToTalkAsync();
    }
    private async void KeyUp(object sender,KeyEventArgs e)
    {
        if(e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled=true;
        if(heldButton==sender && heldKey==e.Key) await ReleaseAsync();
    }
    private async void LostFocus(object sender,KeyboardFocusChangedEventArgs e) { if(heldButton==sender && heldKey!=Key.None) await ReleaseAsync(); }
    private async void Deactivated(object? sender,EventArgs e) { if(heldButton!=null) await ReleaseAsync(); }
    private async Task ReleaseAsync()
    {
        var button=heldButton; if(button==null) return;
        heldButton=null; heldKey=Key.None;
        if(Mouse.Captured==button) button.ReleaseMouseCapture();
        await controller.EndPushToTalkAsync();
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true;
        if(heldButton!=null) _=ReleaseAsync();
        foreach(var button in buttons)
        {
            button.PreviewMouseLeftButtonDown-=MouseDown; button.PreviewMouseLeftButtonUp-=MouseUp;
            button.LostMouseCapture-=LostCapture; button.PreviewKeyDown-=KeyDown; button.PreviewKeyUp-=KeyUp;
            button.LostKeyboardFocus-=LostFocus;
        }
        window.Deactivated-=Deactivated;
    }
}
