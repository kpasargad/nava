using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Nava;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _wait;
    private EventWaitHandle? _shutdownSignal;
    private RegisteredWaitHandle? _shutdownWait;
    private EventWaitHandle? _settingsSignal,_historySignal;
    private RegisteredWaitHandle? _settingsWait,_historyWait;
    private AppController? _controller;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--quit-for-update",StringComparer.OrdinalIgnoreCase))
        {
            try { using var signal=EventWaitHandle.OpenExisting(@"Local\Nava.Desktop.Quit.v1"); signal.Set(); } catch(WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        _mutex=new Mutex(true,@"Local\Nava.Desktop.v1",out bool first);
        _activation=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Nava.Desktop.Activate.v1");
        _settingsSignal=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Nava.Desktop.Settings.v1");
        _historySignal=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Nava.Desktop.History.v1");
        bool settings=e.Args.Contains("--settings",StringComparer.OrdinalIgnoreCase),history=e.Args.Contains("--history",StringComparer.OrdinalIgnoreCase);
        if(!first) { (settings?_settingsSignal:history?_historySignal:_activation).Set(); Shutdown(); return; }
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException+=(_,args)=> { args.Handled=true; ShowError("نوا به مشکلی برخورد کرد. ضبط یا فایل‌های ذخیره‌شده را از تاریخچه بررسی کنید.\n"+SafeMessage(args.Exception),MessageBoxImage.Warning); };
        try
        {
            _controller=new AppController(); _controller.Initialize();
            _wait=ThreadPool.RegisterWaitForSingleObject(_activation,(_,__)=>Dispatcher.BeginInvoke(()=>_controller?.ShowOverlay()),null,Timeout.Infinite,false);
            _settingsWait=ThreadPool.RegisterWaitForSingleObject(_settingsSignal,(_,__)=>Dispatcher.BeginInvoke(()=>_controller?.OpenWorkspace("settings")),null,Timeout.Infinite,false);
            _historyWait=ThreadPool.RegisterWaitForSingleObject(_historySignal,(_,__)=>Dispatcher.BeginInvoke(()=>_controller?.OpenWorkspace()),null,Timeout.Infinite,false);
            if(settings || history) _controller.OpenWorkspace(settings?"settings":"history");
            _shutdownSignal=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Nava.Desktop.Quit.v1");
            _shutdownWait=ThreadPool.RegisterWaitForSingleObject(_shutdownSignal,(_,__)=>Dispatcher.BeginInvoke(()=>_controller?.Quit()),null,Timeout.Infinite,false);
        }
        catch(Exception ex) { ShowError("نوا اجرا نشد.\n"+SafeMessage(ex),MessageBoxImage.Error); Shutdown(1); }
    }
    private void ShowError(string text,MessageBoxImage image)
    {
        var owner=Windows.OfType<Window>().FirstOrDefault(window=>window.IsActive)??MainWindow;
        if(owner!=null) MessageBox.Show(owner,text,"نوا",MessageBoxButton.OK,image);
        else MessageBox.Show(text,"نوا",MessageBoxButton.OK,image);
    }
    private static string SafeMessage(Exception e) => e is IOException or UnauthorizedAccessException ? "پوشهٔ برنامه قابل نوشتن نیست. آن را به پوشه‌ای قابل نوشتن منتقل کنید." : "برنامه را دوباره اجرا کنید؛ اگر مشکل باقی ماند، تنظیمات را بررسی کنید.";
    protected override void OnExit(ExitEventArgs e) { _wait?.Unregister(null); _shutdownWait?.Unregister(null); _settingsWait?.Unregister(null); _historyWait?.Unregister(null); _controller?.Dispose(); _activation?.Dispose(); _shutdownSignal?.Dispose(); _settingsSignal?.Dispose(); _historySignal?.Dispose(); if(_mutex!=null) { try { _mutex.ReleaseMutex(); } catch(ApplicationException) { } _mutex.Dispose(); } base.OnExit(e); }
}
