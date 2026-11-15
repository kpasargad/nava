using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Nava.Services;

public sealed class GlobalHotkey : IDisposable
{
    private readonly HwndSource _source;
    private readonly IntPtr _window;
    private readonly KeyboardCallback _keyboardCallback;
    private readonly DispatcherTimer _watchdog;
    private readonly HotkeyHoldState _hold = new();
    private IntPtr _keyboardHook;
    private bool _disposed;
    private bool _observedAsyncPrimaryDown;
    private int _id = 100;
    private (uint Modifiers,uint Key)? _registered;
    public event Action? Pressed;
    public event Action? Released;
    public bool IsHeld => _hold.IsHeld;
    public GlobalHotkey(IntPtr window)
    {
        _source=HwndSource.FromHwnd(window) ?? throw new ArgumentException("پنجرهٔ میانبر آماده نیست."); _window=window;
        _keyboardCallback=KeyboardHook;
        _keyboardHook=SetWindowsHookEx(13,_keyboardCallback,GetModuleHandle(null),0);
        if(_keyboardHook==IntPtr.Zero) throw new InvalidOperationException("دریافت میانبر سراسری ممکن نشد. نوا را دوباره اجرا کن.");
        _source.AddHook(Hook);
        _watchdog=new DispatcherTimer(DispatcherPriority.Input,_source.Dispatcher) { Interval=TimeSpan.FromMilliseconds(60) };
        _watchdog.Tick+=Watchdog; _watchdog.Start();
    }
    public static (uint Modifiers,uint Key) Parse(string text)
    {
        var parts=text.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0) throw new ArgumentException("یک کلید انتخاب کنید.");
        uint mods=0;
        for(int i=0;i<parts.Length-1;i++) mods|=parts[i].ToLowerInvariant() switch { "ctrl" or "control"=>2u,"alt"=>1u,"shift"=>4u,"win" or "windows"=>8u,_=>throw new ArgumentException("ترکیب میانبر معتبر نیست.") };
        if(!Enum.TryParse<Key>(parts[^1],true,out var key) || !Enum.IsDefined(key) || key==Key.None || key==Key.F12 || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) throw new ArgumentException("این کلید برای میانبر مناسب نیست.");
        if(mods==0 && key!=Key.NumLock && key!=Key.Scroll && key!=Key.Pause && !(key>=Key.F1 && key<=Key.F24)) throw new ArgumentException("برای کلیدهای معمولی از Ctrl یا Alt هم استفاده کنید.");
        return (mods|0x4000,(uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public void Register(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        var (mods,key)=Parse(text); int next=_id+1;
        if(_registered==(mods,key)) return;
        if(!RegisterHotKey(_window,next,mods,key)) throw new InvalidOperationException("این میانبر در اختیار برنامهٔ دیگری است؛ ترکیب دیگری انتخاب کنید.");
        UnregisterHotKey(_window,_id); _id=next; _registered=(mods,key); _observedAsyncPrimaryDown=false;
        Publish(_hold.Configure(mods,key));
    }
    // RegisterHotKey reserves the chord. The low-level hook supplies both edges;
    // swallowing its primary key also preserves NumLock's existing toggle state.
    private IntPtr Hook(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled) { if(msg==0x312 && wp.ToInt32()==_id) handled=true; return IntPtr.Zero; }
    private IntPtr KeyboardHook(int code,IntPtr message,IntPtr dataPointer)
    {
        if(code<0 || _disposed || _registered==null) return CallNextHookEx(_keyboardHook,code,message,dataPointer);
        try
        {
            int kind=message.ToInt32();
            if(kind is not (0x100 or 0x101 or 0x104 or 0x105)) return CallNextHookEx(_keyboardHook,code,message,dataPointer);
            var data=Marshal.PtrToStructure<KeyboardData>(dataPointer);
            // No injected events (including our own paste) activate voice capture.
            if((data.Flags&0x12)!=0) return CallNextHookEx(_keyboardHook,code,message,dataPointer);
            bool down=kind is 0x100 or 0x104;
            var transition=_hold.Process(data.Key,down,Modifiers(data.Key,down));
            if(transition.Pressed || (data.Key==_registered.Value.Key && !down)) _observedAsyncPrimaryDown=false;
            Publish(transition);
            if(transition.Suppress) return new IntPtr(1);
        }
        catch { /* Never propagate managed failures into the native keyboard chain. */ }
        return CallNextHookEx(_keyboardHook,code,message,dataPointer);
    }
    private void Watchdog(object? sender,EventArgs args)
    {
        if(_disposed || _registered==null) return;
        bool asyncDown=IsKeyDown(_registered.Value.Key);
        if(_hold.IsHeld && asyncDown) _observedAsyncPrimaryDown=true;
        // Suppressed keys can be absent from Windows' asynchronous state. Trust
        // an async keyup only after observing down; physical hook-up is authoritative.
        bool lostPrimaryUp=_observedAsyncPrimaryDown && !asyncDown;
        Publish(_hold.Poll(!lostPrimaryUp,Modifiers()));
        if(lostPrimaryUp) _observedAsyncPrimaryDown=false;
    }
    private void Publish(HotkeyTransition transition)
    {
        // Keep the system hook short. WPF processes edge notifications in order.
        if(transition.Pressed) _source.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=> { if(!_disposed) Pressed?.Invoke(); }));
        if(transition.Released) _source.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=> { if(!_disposed) Released?.Invoke(); }));
    }
    private static bool IsKeyDown(uint key)=>(GetAsyncKeyState((int)key)&0x8000)!=0;
    private static uint Modifiers(uint eventKey=0,bool down=false)
    {
        bool State(uint key)=>eventKey==key?down:IsKeyDown(key);
        uint result=0;
        if(State(0xA4)||State(0xA5)) result|=1;
        if(State(0xA2)||State(0xA3)) result|=2;
        if(State(0xA0)||State(0xA1)) result|=4;
        if(State(0x5B)||State(0x5C)) result|=8;
        // A hook normally receives left/right virtual keys; handle generic codes too.
        uint generic=eventKey switch { 0x12=>1u,0x11=>2u,0x10=>4u,_=>0u };
        return generic==0?result:down?result|generic:result&~generic;
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _watchdog.Stop();
        bool wasHeld=_hold.IsHeld; _hold.Reset();
        UnregisterHotKey(_window,_id);
        try { _source.RemoveHook(Hook); } catch(InvalidOperationException) { }
        if(_keyboardHook!=IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook=IntPtr.Zero; }
        if(wasHeld) { try { Released?.Invoke(); } catch { } }
        GC.KeepAlive(_keyboardCallback);
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key,Scan,Flags,Time; public UIntPtr ExtraInfo; }
    private delegate IntPtr KeyboardCallback(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true,EntryPoint="SetWindowsHookExW")] private static extern IntPtr SetWindowsHookEx(int hook,KeyboardCallback callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
}

/// <summary>Deterministic physical-key state; stores only the chosen chord, never typed content.</summary>
public sealed class HotkeyHoldState
{
    private uint _modifiers,_key;
    private bool _ownsPrimary;
    public bool IsHeld { get; private set; }
    public HotkeyTransition Configure(uint modifiers,uint key)
    {
        bool released=IsHeld; Reset(); _modifiers=modifiers&15; _key=key;
        return new(false,false,released);
    }
    public HotkeyTransition Process(uint key,bool down,uint activeModifiers,bool injected=false)
    {
        if(injected || _key==0) return default;
        activeModifiers&=15;
        if(key==_key)
        {
            if(!down)
            {
                bool suppress=_ownsPrimary,released=IsHeld;
                _ownsPrimary=false; IsHeld=false;
                return new(suppress,false,released);
            }
            if(_ownsPrimary) return new(true,false,false); // Keyboard auto-repeat.
            if(activeModifiers==_modifiers)
            {
                _ownsPrimary=true; IsHeld=true;
                return new(true,true,false);
            }
        }
        if(IsHeld && (activeModifiers&_modifiers)!=_modifiers)
        {
            IsHeld=false; // Still swallow owned primary repeats/up until it is released.
            return new(false,false,true);
        }
        return default;
    }
    public HotkeyTransition Poll(bool primaryDown,uint activeModifiers)
    {
        if(!primaryDown) _ownsPrimary=false;
        if(IsHeld && (!primaryDown || ((activeModifiers&15)&_modifiers)!=_modifiers)) { IsHeld=false; return new(false,false,true); }
        return default;
    }
    public void Reset() { IsHeld=false; _ownsPrimary=false; }
}

public readonly record struct HotkeyTransition(bool Suppress,bool Pressed,bool Released);

public static class WindowsIntegration
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count,Input[] inputs,int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key,Scan; public uint Flags,Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Data,Flags,Time; public UIntPtr ExtraInfo; }
    public static bool Paste(IntPtr target)
    {
        if(target==IntPtr.Zero || !IsWindow(target) || !SetForegroundWindow(target) || GetForegroundWindow()!=target) return false;
        var input=new[] { Key(0x11,false),Key(0x56,false),Key(0x56,true),Key(0x11,true) };
        return SendInput(4,input,Marshal.SizeOf<Input>())==4;
    }
    private static Input Key(ushort key,bool up) => new() { Type=1,Union=new() { Keyboard=new() { Key=key,Flags=up?2u:0u } } };
}
