using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Nava.Services;
using Nava.Views;

namespace Nava.Tests;

/// <summary>Opt-in native shell-style verification of own visible synthetic windows. No input injection.</summary>
public static class ShellWindowSmoke
{
    public static void VerifyStyleTransform()
    {
        const uint tool=0x80,app=0x40000,protectedMask=~(tool|app);
        foreach(uint before in new uint[] { 0,app,0x80000,0x80008|app,0x08000020,0x12345678,uint.MaxValue })
        {
            uint after=ShellWindowPolicy.ToolWindowStyle(before);
            Require((after&tool)!=0 && (after&app)==0,"Task-switcher style flags were not enforced");
            Require((after&protectedMask)==(before&protectedMask),"Shell policy changed a transparency, activation, topmost, or unrelated style bit");
        }
        uint ordinary=ShellWindowPolicy.ToolWindowStyle(0x80000);
        Require((ordinary&0x20)==0 && (ordinary&0x08000000)==0,"Shell hiding added click-through or no-activate behavior");
    }

    public static int Run(string projectDirectory)
    {
        string root=Path.GetFullPath(projectDirectory);
        if(!File.Exists(Path.Combine(root,"App.xaml"))) throw new ArgumentException("Shell smoke requires the source project directory");
        int result=1;
        var thread=new Thread(()=> {
            string? previous=Environment.GetEnvironmentVariable("NAVA_DATA_DIR");
            System.Windows.Application? app=null; AppController? controller=null; OverlayWindow? overlay=null; WorkspaceWindow? workspace=null; Window? dialog=null;
            var evidence=new List<object>();
            string local=Path.Combine(root,".local"); Directory.CreateDirectory(local);
            try
            {
                Environment.SetEnvironmentVariable("NAVA_DATA_DIR",Path.Combine(local,"shell-window-smoke-data",Guid.NewGuid().ToString("N")));
                app=new System.Windows.Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
                LoadResources(app,root);
                controller=new AppController(); // No Initialize: no production signals, hotkey, tray, or network.
                controller.Settings.Sounds=false; controller.Settings.AutoCopy=false; controller.Settings.AutoPaste=false;
                app.Dispatcher.BeginInvoke(new Action(async ()=> {
                    try
                    {
                        VerifyStyleTransform();
                        overlay=new OverlayWindow(controller) { Title="Nava shell-style verification • mini" };
                        _=new WindowInteropHelper(overlay).EnsureHandle();
                        CheckWindow(overlay,"overlay-created",evidence);
                        overlay.Show(); overlay.RestorePosition(300,300);
                        await FrameAsync(); CheckWindow(overlay,"overlay-visible",evidence);
                        overlay.Expand(); await FrameAsync(); CheckWindow(overlay,"overlay-expanded",evidence);
                        overlay.Collapse(); overlay.Hide(); CheckWindow(overlay,"overlay-hidden",evidence);
                        overlay.Show(); await FrameAsync(); CheckWindow(overlay,"overlay-reopened",evidence);

                        controller.OpenWorkspace("history");
                        workspace=app.Windows.OfType<WorkspaceWindow>().Single();
                        workspace.Title="Nava shell-style verification • notes";
                        await FrameAsync(); CheckWindow(workspace,"workspace-visible",evidence);
                        workspace.Hide(); controller.OpenWorkspace("settings");
                        await FrameAsync(); CheckWindow(workspace,"workspace-settings-reopened",evidence);
                        workspace.WindowState=WindowState.Minimized; await FrameAsync();
                        controller.OpenWorkspace("history"); await FrameAsync();
                        Require(workspace.IsVisible && workspace.WindowState!=WindowState.Minimized,"Tray-style reopening left the workspace minimized");
                        CheckWindow(workspace,"workspace-restored",evidence);

                        // Simulate a later WPF request to add APPWINDOW. The native
                        // style hook must retain TOOLWINDOW without disabling input.
                        workspace.ShowInTaskbar=true;
                        CheckWindow(workspace,"workspace-later-style-write",evidence,checkManaged:false);
                        workspace.ShowInTaskbar=false;
                        CheckWindow(workspace,"workspace-policy-restored",evidence);

                        dialog=new Window { Owner=workspace,Title="Nava shell-style verification • owned dialog",Width=320,Height=150,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBlock { Text="Synthetic shell-policy verification",Margin=new Thickness(18) } };
                        ShellWindowPolicy.Attach(dialog);
                        _=new WindowInteropHelper(dialog).EnsureHandle(); CheckWindow(dialog,"dialog-created",evidence);
                        dialog.Show(); await FrameAsync(); CheckWindow(dialog,"dialog-visible",evidence);
                        dialog.Hide(); dialog.Show(); await FrameAsync(); CheckWindow(dialog,"dialog-reopened",evidence);
                        File.WriteAllText(Path.Combine(local,"shell-window-smoke.json"),JsonSerializer.Serialize(new { scope="Own native windows, show/hide/restore and HWND flags; no Alt+Tab key injection, microphone, network, hotkeys, or clipboard",checks=evidence },new JsonSerializerOptions { WriteIndented=true }));
                        Console.WriteLine("PASS shell native styles across "+evidence.Count+" creation/show/hide/restore phases"); result=0;
                    }
                    catch(Exception ex) { Console.WriteLine("FAIL shell native styles: "+ex.GetType().Name+" "+ex.Message); }
                    finally
                    {
                        try { dialog?.Close(); workspace?.ForceClose(); overlay?.Close(); controller.Dispose(); } catch { }
                        app.Shutdown();
                    }
                }));
                app.Run();
            }
            catch(Exception ex) { Console.WriteLine("FAIL shell fixture: "+ex.GetType().Name+" "+ex.Message); }
            finally { Environment.SetEnvironmentVariable("NAVA_DATA_DIR",previous); }
        }) { IsBackground=true,Name="Nava shell-policy native fixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(25))) { Console.WriteLine("FAIL shell fixture timeout"); return 1; }
        return result;
    }

    private static void CheckWindow(Window window,string phase,List<object> evidence,bool checkManaged=true)
    {
        IntPtr handle=new WindowInteropHelper(window).Handle;
        Require(handle!=IntPtr.Zero,"Fixture window has no native handle");
        uint style=ShellWindowPolicy.ExtendedStyle(handle);
        Require((style&0x80)!=0 && (style&0x40000)==0,"Native window remains eligible for Alt+Tab: "+phase);
        if(checkManaged) Require(!window.ShowInTaskbar,"Managed taskbar policy was not disabled: "+phase);
        Require(window.IsEnabled,"Shell policy disabled window input: "+phase);
        Require((style&0x20)==0,"Shell policy made the window click-through: "+phase);
        if(window is OverlayWindow) Require((style&0x80000)!=0,"Shell policy removed layered-window rendering");
        if(window is WorkspaceWindow) Require((style&0x08000000)==0,"Workspace cannot activate for typing");
        evidence.Add(new { phase,visible=window.IsVisible,showInTaskbar=window.ShowInTaskbar,state=window.WindowState.ToString(),extendedStyle="0x"+style.ToString("X8") });
    }
    private static Task FrameAsync()=>Task.Delay(80);
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static void LoadResources(System.Windows.Application app,string projectRoot)
    {
        XNamespace presentation="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source=XDocument.Load(Path.Combine(projectRoot,"App.xaml")); var root=source.Root??throw new InvalidDataException("Application XAML has no root");
        var resources=root.Element(presentation+"Application.Resources")??throw new InvalidDataException("Application resources missing");
        var dictionary=new XElement(presentation+"ResourceDictionary",root.Attributes().Where(x=>x.IsNamespaceDeclaration),resources.Nodes());
        app.Resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString(SaveOptions.DisableFormatting));
    }
}
