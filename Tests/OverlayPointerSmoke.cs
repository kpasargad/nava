using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Nava.Views;

namespace Nava.Tests;

/// <summary>An opt-in, visible fixture for pointer verification of Nava's own overlay.</summary>
public static class OverlayPointerSmoke
{
    private sealed class PointerApplication : Nava.App
    {
        // Do not call Nava.App.OnStartup: no production instance, signals,
        // network checks, hotkeys, tray icon, microphone, or clipboard.
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private sealed record PointerState(int ProcessId,bool IsExpanded,double AnchorLeft,double AnchorTop,double Left,double Top,double Width,double Height,bool Closed);

    public static int Run(string projectDirectory)
    {
        string root=Path.GetFullPath(projectDirectory);
        if(!File.Exists(Path.Combine(root,"App.xaml")) || !File.Exists(Path.Combine(root,"Nava.csproj")))
            throw new ArgumentException("Pointer fixture requires the Nava source project directory.");
        string local=Path.Combine(root,".local"); Directory.CreateDirectory(local);
        string statePath=Path.Combine(local,"pointer-smoke-state.json");
        int result=1;
        var thread=new Thread(()=> {
            string? previousRoot=Environment.GetEnvironmentVariable("NAVA_DATA_DIR");
            PointerApplication? application=null; AppController? controller=null; OverlayWindow? overlay=null;
            DispatcherTimer? timer=null;
            string? previousState=null;
            bool fixtureClosed=false;
            var json=new JsonSerializerOptions { WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
            void WriteState(bool closed=false)
            {
                if(overlay==null) return;
                var snapshot=new PointerState(Environment.ProcessId,overlay.IsExpanded,overlay.MiniLeft,overlay.MiniTop,overlay.Left,overlay.Top,overlay.Width,overlay.Height,closed);
                string serialized=JsonSerializer.Serialize(snapshot,json);
                if(serialized==previousState) return;
                string temporary=statePath+"."+Guid.NewGuid().ToString("N")+".tmp";
                try { File.WriteAllText(temporary,serialized,new UTF8Encoding(false)); File.Move(temporary,statePath,true); }
                finally { if(File.Exists(temporary)) File.Delete(temporary); }
                previousState=serialized;
                Console.WriteLine(FormattableString.Invariant($"POINTER expanded={snapshot.IsExpanded} anchor=({snapshot.AnchorLeft:0.##},{snapshot.AnchorTop:0.##}) size=({snapshot.Width:0.##},{snapshot.Height:0.##}) closed={snapshot.Closed}"));
            }
            try
            {
                Environment.SetEnvironmentVariable("NAVA_DATA_DIR",Path.Combine(local,"pointer-smoke-data",Guid.NewGuid().ToString("N")));
                application=new PointerApplication { ShutdownMode=ShutdownMode.OnExplicitShutdown };
                InitializeResources(application,root);
                controller=new AppController(); // Enumerates device names; never calls Initialize or recording.
                overlay=new OverlayWindow(controller) { ShowInTaskbar=true,Title="Nava pointer verification" };
                overlay.Closed+=(_,_)=> { fixtureClosed=true; timer?.Stop(); WriteState(true); application.Shutdown(); };
                application.MainWindow=overlay;
                overlay.Show(); overlay.RestorePosition(300,300);
                DateTime exitAt=DateTime.UtcNow.AddSeconds(120);
                timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(200) };
                timer.Tick+=(_,_)=> { WriteState(); if(DateTime.UtcNow>=exitAt) overlay.Close(); };
                timer.Start(); WriteState();
                Console.WriteLine($"READY Nava pointer verification pid={Environment.ProcessId}; auto-exit=120s; no microphone/network/hotkeys");
                application.Run(); result=0;
            }
            catch(Exception ex) { Console.WriteLine("FAIL pointer fixture: "+ex.GetType().Name+" "+ex.Message); }
            finally
            {
                timer?.Stop();
                try { if(overlay!=null && !fixtureClosed) overlay.Close(); } catch { }
                try { controller?.Dispose(); } catch { }
                Environment.SetEnvironmentVariable("NAVA_DATA_DIR",previousRoot);
            }
        }) { IsBackground=true,Name="Nava native pointer fixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(125))) { Console.WriteLine("FAIL pointer fixture timed out"); return 1; }
        return result;
    }
    private static void InitializeResources(PointerApplication application,string projectRoot)
    {
        // A derived App in a different assembly cannot load App.xaml's compiled
        // root via inherited InitializeComponent. Load the identical resource
        // dictionary from the checked-out source, without production startup.
        XNamespace presentation="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source=XDocument.Load(Path.Combine(projectRoot,"App.xaml"));
        var root=source.Root??throw new InvalidDataException("Application XAML has no root.");
        var resources=root.Element(presentation+"Application.Resources")??throw new InvalidDataException("Application XAML has no resources.");
        var dictionary=new XElement(presentation+"ResourceDictionary",root.Attributes().Where(attribute=>attribute.IsNamespaceDeclaration),resources.Nodes());
        application.Resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString(SaveOptions.DisableFormatting));
    }
}
