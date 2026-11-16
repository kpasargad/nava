using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Nava.Services;

/// <summary>Keep Nava's interactive windows out of the taskbar and Alt+Tab.</summary>
internal static class ShellWindowPolicy
{
    private const int ExtendedStyleIndex=-20;
    private const uint ToolWindow=0x80,AppWindow=0x40000;
    internal static uint ToolWindowStyle(uint style)=>(style|ToolWindow)&~AppWindow;
    internal static uint ExtendedStyle(IntPtr handle)=>unchecked((uint)(IntPtr.Size==8?GetWindowLongPtr(handle,ExtendedStyleIndex).ToInt64():GetWindowLong(handle,ExtendedStyleIndex)));

    internal static void Attach(Window window)
    {
        window.ShowInTaskbar=false;
        HwndSource? source=null;
        IntPtr Hook(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
        {
            // WPF/WindowChrome can rewrite these styles on show or restore.
            // Preserve all input/transparency flags while retaining shell policy.
            if(message==0x7C && wParam.ToInt64()==ExtendedStyleIndex && lParam!=IntPtr.Zero)
                Marshal.WriteInt32(lParam,4,unchecked((int)ToolWindowStyle(unchecked((uint)Marshal.ReadInt32(lParam,4)))));
            return IntPtr.Zero;
        }
        void Apply(object? sender,EventArgs args)
        {
            var handle=new WindowInteropHelper(window).Handle;
            source=HwndSource.FromHwnd(handle);
            source?.AddHook(Hook);
            uint style=ToolWindowStyle(ExtendedStyle(handle));
            if(IntPtr.Size==8) SetWindowLongPtr(handle,ExtendedStyleIndex,new IntPtr(style));
            else SetWindowLong(handle,ExtendedStyleIndex,unchecked((int)style));
            if(ExtendedStyle(handle)!=style) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetWindowPos(handle,IntPtr.Zero,0,0,0,0,0x37); // FRAMECHANGED, no move/resize/Z-order/activation
        }
        void Closed(object? sender,EventArgs args)
        {
            if(source is { IsDisposed:false }) source.RemoveHook(Hook);
            window.SourceInitialized-=Apply; window.Closed-=Closed;
        }
        window.SourceInitialized+=Apply; window.Closed+=Closed;
        if(new WindowInteropHelper(window).Handle!=IntPtr.Zero) Apply(null,EventArgs.Empty);
    }

    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW",SetLastError=true)] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW",SetLastError=true)] private static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)] private static extern int SetWindowLong(IntPtr window,int index,int value);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
