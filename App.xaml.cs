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
        if(!first) { _activation.Set(); Shutdown(); return; }
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException+=(_,args)=> { args.Handled=true; MessageBox.Show("نوا به مشکلی برخورد کرد. ضبط یا فایل‌های ذخیره‌شده را از تاریخچه بررسی کنید.\n"+SafeMessage(args.Exception),"نوا",MessageBoxButton.OK,MessageBoxImage.Warning); };
        try
        {
            _controller=new AppController(); _controller.Initialize();
            _wait=ThreadPool.RegisterWaitForSingleObject(_activation,(_,__)=>Dispatcher.BeginInvoke(()=>_controller?.ShowOverlay()),null,Timeout.Infinite,false);
            _shutdownSignal=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\Nava.Desktop.Quit.v1");
            _shutdownWait=ThreadPool.RegisterWaitForSingleObject(_shutdownSignal,(_,__)=>Dispatcher.BeginInvoke(()=>_controller?.Quit()),null,Timeout.Infinite,false);
        }
        catch(Exception ex) { MessageBox.Show("نوا اجرا نشد.\n"+SafeMessage(ex),"نوا",MessageBoxButton.OK,MessageBoxImage.Error); Shutdown(1); }
    }
    private static string SafeMessage(Exception e) => e is IOException or UnauthorizedAccessException ? "پوشهٔ برنامه قابل نوشتن نیست. آن را به پوشه‌ای قابل نوشتن منتقل کنید." : "برنامه را دوباره اجرا کنید؛ اگر مشکل باقی ماند، تنظیمات را بررسی کنید.";
    protected override void OnExit(ExitEventArgs e) { _wait?.Unregister(null); _shutdownWait?.Unregister(null); _controller?.Dispose(); _activation?.Dispose(); _shutdownSignal?.Dispose(); if(_mutex!=null) { try { _mutex.ReleaseMutex(); } catch(ApplicationException) { } _mutex.Dispose(); } base.OnExit(e); }
}
