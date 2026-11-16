using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Nava.Core;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Failure injection against own temporary files; no microphone, desktop window, clipboard, or network.</summary>
public static class ControllerFailureRegression
{
    public static Task RunAsync(string temporaryRoot)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=> {
            var app=new System.Windows.Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async ()=> {
                var failures=new List<Exception>();
                async Task Check(string name,Func<Task> action)
                {
                    try { await action(); Console.WriteLine("PASS "+name); }
                    catch(Exception ex) { Console.WriteLine("FAIL "+name+": "+ex.GetType().Name); failures.Add(new Exception(name,ex)); }
                }
                try
                {
                    await Check("failed initial persistence closes and disposes capture",()=>StartPersistenceFailureAsync(Path.Combine(temporaryRoot,"start")));
                    await Check("cancel persistence failure always clears recording and busy state",()=>CancelPersistenceFailureAsync(Path.Combine(temporaryRoot,"cancel")));
                    await Check("completion persistence failure preserves recoverable audio",()=>CompletePersistenceFailureAsync(Path.Combine(temporaryRoot,"complete")));
                    await Check("locked pending history cannot prevent startup",()=>PendingHistoryRecoveryAsync(Path.Combine(temporaryRoot,"startup")));
                    if(failures.Count>0) done.TrySetException(new AggregateException(failures)); else done.TrySetResult();
                }
                catch(Exception ex) { done.TrySetException(ex); }
                finally { app.Shutdown(); }
            }));
            try { app.Run(); } catch(Exception ex) { done.TrySetException(ex); }
        }) { IsBackground=true,Name="Nava controller failure regression" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task;
    }

    public static void NullHistoryFieldsRecover(string root)
    {
        var store=new LocalStore(root);
        File.WriteAllText(Path.Combine(root,"History","null-fields.json"),"""{"Id":"nullfields","Title":null,"Text":null,"Status":"completed","Segments":null,"SpeakerNames":null,"Analyses":null}""");
        var record=store.LoadRecords().Single();
        Require(record.Text!=null && record.Title!=null,"Null text/title were not recovered");
        Require(record.Segments!=null && record.SpeakerNames!=null && record.Analyses!=null,"Null collections were not recovered");
        _=record.Preview; _=LocalStore.ToSrt(record); _=LocalStore.TextWithSpeakers(record);
    }

    public static void SettingsWriteFailurePreservesPreviousCredentials(string root)
    {
        var store=new LocalStore(root);
        var before=new AppSettings { BaseUrl="https://fixture-before.example",ApiKey="fixture-before-secret",ProxyUrl="http://before:fixture@127.0.0.1:19001" };
        var proposed=new AppSettings { BaseUrl="https://fixture-after.example",ApiKey="fixture-after-secret",ProxyUrl="http://after:fixture@127.0.0.1:19002" };
        store.SaveSettings(before);
        foreach(string lockedName in new[] { "settings.json","credentials.bin" })
        {
            using var locked=new FileStream(Path.Combine(root,lockedName),FileMode.Open,FileAccess.Read,FileShare.Read);
            bool failed=false;
            try { store.SaveSettings(proposed); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { failed=true; }
            Require(failed,"Locked settings/credentials unexpectedly accepted a write");
            var retained=store.LoadSettings();
            Require(retained.ApiKey==before.ApiKey && retained.ProxyUrl==before.ProxyUrl,"Failed settings write replaced the previous credentials");
            Require(retained.BaseUrl==before.BaseUrl,"Failed settings write replaced the previous endpoint");
        }
    }

    private static LocalStore OwnStore(string root)
    {
        var store=new LocalStore(root);
        // Deliberately invalid URI fails before any socket is opened. The key is a placeholder.
        var settings=new AppSettings { BaseUrl="invalid-no-network",ApiKey="fixture-test-only",Sounds=false,AutoCopy=false,AutoPaste=false,KeepAudio=false,StartWithWindows=false };
        File.WriteAllText(Path.Combine(root,"settings.json"),JsonSerializer.Serialize(settings));
        return store;
    }
    private static string RecordPath(LocalStore store,TranscriptRecord record)=>Path.Combine(store.Root,"History",record.Id+".json");

    private static async Task StartPersistenceFailureAsync(string root)
    {
        var store=OwnStore(root); var capture=new FakeCapture();
        using var controller=new AppController(store,()=>capture);
        var history=Path.Combine(root,"History"); Directory.Delete(history); File.WriteAllText(history,"block history writes");
        try
        {
            await controller.StartPushToTalkAsync();
            Require(!controller.IsRecording && !controller.IsBusy && !controller.IsConnecting && !controller.IsLiveConnected,"Failed start left active UI state");
            Require(!capture.Active && (!capture.Started || capture.Disposed),"Failed start retained undisposed capture");
        }
        finally { File.Delete(history); Directory.CreateDirectory(history); }
    }

    private static async Task CancelPersistenceFailureAsync(string root)
    {
        var store=OwnStore(root); var capture=new FakeCapture();
        using var controller=new AppController(store,()=>capture);
        await controller.StartPushToTalkAsync();
        Require(controller.IsRecording && capture.Active,"Fixture capture did not start");
        var record=controller.Records.Single(); string audio=record.AudioPath!;
        using(var locked=new FileStream(RecordPath(store,record),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            try { await controller.CancelAsync(); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { /* Error reporting may throw; cleanup is still mandatory. */ }
            Require(!controller.IsRecording && !controller.IsBusy && !controller.IsConnecting && !controller.IsLiveConnected,"Cancel persistence failure left the controller busy");
            Require(!capture.Active && capture.Disposed,"Cancel persistence failure retained capture resources");
            Require(File.Exists(audio),"Cancel lost recoverable audio");
        }
    }

    private static Task CompletePersistenceFailureAsync(string root)
    {
        var store=OwnStore(root);
        using var controller=new AppController(store,()=>new FakeCapture());
        string audio=Path.Combine(store.AudioDirectory,"completed-fixture.wav"); File.WriteAllBytes(audio,new byte[3244]); AudioRecorder.RepairHeader(audio);
        var record=new TranscriptRecord { Text="pending synthetic result",Status="processing",AudioPath=audio,DurationMs=100 };
        store.Save(record); controller.Records.Add(record); controller.SelectedRecord=record;
        record.Text="completed synthetic result";
        using(var locked=new FileStream(RecordPath(store,record),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            try
            {
                // Inject a successful service result at the completion boundary without a network request.
                typeof(AppController).GetMethod("Complete",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controller,new object[] { record,false });
            }
            catch(TargetInvocationException ex) when(ex.InnerException is IOException or UnauthorizedAccessException) { }
            Require(File.Exists(audio),"Audio was deleted before the completed transcript was durable");
        }
        return Task.CompletedTask;
    }

    private static Task PendingHistoryRecoveryAsync(string root)
    {
        var store=OwnStore(root); var record=new TranscriptRecord { Status="processing",Text="synthetic pending note" }; store.Save(record);
        AppController? controller=null;
        try
        {
            using(var locked=new FileStream(RecordPath(store,record),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                controller=new AppController(store,()=>new FakeCapture());
                Require(controller.Records.Count==1,"Readable pending history disappeared after a persistence error");
                Require(controller.Records[0].Status=="failed","Pending recovery did not mark the in-memory note recoverable");
            }
        }
        finally { controller?.Dispose(); }
        return Task.CompletedTask;
    }

    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private sealed class FakeCapture : IAudioCapture
    {
        public bool Active { get; private set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public event Action<byte[]>? DataAvailable { add { } remove { } }
        public event Action<double>? LevelChanged { add { } remove { } }
        public event Action? Failed { add { } remove { } }
        public double DurationMs=>1500;
        public void Start(string path,int deviceId) { Started=true; File.WriteAllBytes(path,new byte[48044]); AudioRecorder.RepairHeader(path); Active=true; }
        public void Stop()=>Active=false;
        public void Dispose() { Stop(); Disposed=true; }
    }
}
