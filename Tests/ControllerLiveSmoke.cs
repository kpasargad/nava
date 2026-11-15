using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Runs the real PTT controller with generated audio, no microphone, windows, hotkeys, or clipboard.</summary>
public static class ControllerLiveSmoke
{
    public static Task<int> RunAsync(string projectDirectory)
    {
        var finished=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        string root=Path.GetFullPath(projectDirectory);
        var thread=new Thread(()=> {
            // Deliberately use the base WPF app. Nava.App would initialize desktop UI.
            var app=new System.Windows.Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async ()=> {
                try { await ScenarioAsync(root); Console.WriteLine("PASS controller live push-to-talk synthetic fixture"); finished.TrySetResult(0); }
                catch(Exception ex) { Console.WriteLine("FAIL controller live push-to-talk "+Safe(ex.Message)); finished.TrySetResult(1); }
                finally { app.Shutdown(); }
            }));
            try { app.Run(); } catch(Exception ex) { Console.WriteLine("FAIL controller dispatcher "+Safe(ex.Message)); finished.TrySetResult(1); }
        }) { IsBackground=true,Name="Nava synthetic controller QA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return finished.Task;
    }

    private static async Task ScenarioAsync(string root)
    {
        string privateRoot=Path.Combine(root,".local");
        string fixture=Path.Combine(privateRoot,"test-english.wav");
        Require(File.Exists(fixture),"Generated audio fixture missing");
        var settings=new LocalStore(privateRoot).LoadSettings();
        Require(!string.IsNullOrWhiteSpace(settings.ApiKey),"Windows-user credentials unavailable");
        settings.AutoCopy=false; settings.AutoPaste=false; settings.Sounds=false; settings.KeepAudio=false; settings.StartWithWindows=false; settings.LiveTranscription=true;
        var store=new LocalStore(Path.Combine(privateRoot,"controller-ptt-smoke",Guid.NewGuid().ToString("N")));
        store.SaveSettings(settings);
        byte[] pcm=LiveIntegration.ReadPcm16Mono16Khz(fixture);
        using var capture=new FixtureCapture(pcm);
        int factoryCalls=0;
        using var controller=new AppController(store,()=> { factoryCalls++; return capture; });
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        int finalUpdates=0,partialUpdates=0; bool streamedBeforeRelease=false,connected=false,released=false; long firstTextMs=-1;
        var clock=Stopwatch.StartNew();
        controller.PropertyChanged+=(_,eventArgs)=> {
            if(eventArgs.PropertyName==nameof(AppController.IsLiveConnected) && controller.IsLiveConnected) connected=true;
            if(eventArgs.PropertyName==nameof(AppController.FinalText) && !string.IsNullOrWhiteSpace(controller.FinalText)) finalUpdates++;
            if(eventArgs.PropertyName==nameof(AppController.PartialText) && !string.IsNullOrWhiteSpace(controller.PartialText)) partialUpdates++;
            if(eventArgs.PropertyName is nameof(AppController.FinalText) or nameof(AppController.PartialText))
                if(!released && controller.IsRecording && (!string.IsNullOrWhiteSpace(controller.FinalText) || !string.IsNullOrWhiteSpace(controller.PartialText))) {
                    streamedBeforeRelease=true; if(firstTextMs<0) firstTextMs=clock.ElapsedMilliseconds;
                }
        };
        try
        {
            await controller.StartPushToTalkAsync().WaitAsync(TimeSpan.FromSeconds(8),deadline.Token);
            Require(controller.IsRecording,"Press did not begin capture");
            await controller.StartPushToTalkAsync();
            Require(controller.IsRecording && controller.Records.Count==1 && factoryCalls==1 && capture.StartCount==1,"Held/repeated start created an extra recording");
            await capture.PlaybackFinished.WaitAsync(deadline.Token);
            var textDeadline=DateTime.UtcNow.AddSeconds(8);
            while(!streamedBeforeRelease && DateTime.UtcNow<textDeadline) await Task.Delay(100,deadline.Token);
            Require(controller.IsRecording,"Capture ended while the PTT key was still held");
            Require(connected && streamedBeforeRelease,"No live text update appeared before release");
            Require(finalUpdates+partialUpdates>0,"No interim/final property updates before release");
            Console.WriteLine("INFO controller held_recording=true streamed_before_release=true first_text_ms="+firstTextMs+" final_updates="+finalUpdates+" partial_updates="+partialUpdates+" captured_bytes="+capture.BytesWritten);
            released=true;
            await controller.EndPushToTalkAsync().WaitAsync(TimeSpan.FromSeconds(35),deadline.Token);
            Require(!controller.IsRecording && !controller.IsBusy,"Release did not finish capture/processing");
            Require(controller.Records.Count==1,"PTT produced duplicate history");
            var result=controller.Records.Single();
            Require(result.Status=="completed","PTT result was not completed: "+Safe(controller.ErrorMessage));
            Require(!string.IsNullOrWhiteSpace(result.Text) && result.Text.Contains("Monday",StringComparison.OrdinalIgnoreCase) && (result.Text.Contains("ten",StringComparison.OrdinalIgnoreCase)||result.Text.Contains("10",StringComparison.Ordinal)) && result.Text.Contains("Thank you",StringComparison.OrdinalIgnoreCase),"Full fixture ending is missing from final controller text");
            Require(controller.FinalText==result.Text && string.IsNullOrWhiteSpace(controller.PartialText),"Final/interim state did not settle on release");
            Require(result.AudioPath==null && !Directory.EnumerateFiles(store.AudioDirectory,"*.wav").Any(),"Successful temporary audio was not cleaned up");
            await controller.EndPushToTalkAsync(); await controller.EndPushToTalkAsync();
            Require(controller.Records.Count==1 && capture.StartCount==1 && factoryCalls==1,"Repeated release created another operation");
            Require(store.LoadRecords().Single().Status=="completed","Completed PTT result did not persist");
            File.WriteAllText(Path.Combine(store.Root,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("INFO controller completed=true records=1 final_text_length="+result.Text.Length+" segments="+result.Segments.Count+" repeated_release_noop=true");
        }
        finally { if(controller.IsRecording || controller.IsBusy) await controller.CancelAsync(); }
    }

    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static string Safe(string message)
    {
        message=System.Text.RegularExpressions.Regex.Replace(message,@"[A-Za-z0-9_+./:=~\-]{24,}","[redacted]");
        message=message.Replace('\r',' ').Replace('\n',' ');
        return message.Length>500?message[..500]:message;
    }

    private sealed class FixtureCapture : IAudioCapture
    {
        private readonly byte[] pcm;
        private readonly object gate=new();
        private readonly CancellationTokenSource stop=new();
        private readonly TaskCompletionSource playbackFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileStream? stream;
        private string? path;
        private long bytes;
        public event Action<byte[]>? DataAvailable;
        public event Action<double>? LevelChanged;
        public event Action? Failed;
        public Task PlaybackFinished=>playbackFinished.Task;
        public long BytesWritten=>Interlocked.Read(ref bytes);
        public double DurationMs=>BytesWritten/32.0;
        public int StartCount { get; private set; }
        public FixtureCapture(byte[] pcm)=>this.pcm=pcm;
        public void Start(string targetPath,int deviceId)
        {
            StartCount++; Require(StartCount==1,"Synthetic capture started more than once"); path=targetPath;
            stream=new FileStream(targetPath,FileMode.Create,FileAccess.Write,FileShare.Read); stream.Write(new byte[44]);
            _=Task.Run(async ()=> {
                try
                {
                    for(int offset=0;offset<pcm.Length;offset+=3200)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        byte[] frame=pcm.AsSpan(offset,Math.Min(3200,pcm.Length-offset)).ToArray();
                        lock(gate)
                        {
                            if(stream==null) break;
                            stream.Write(frame); Interlocked.Add(ref bytes,frame.Length);
                            DataAvailable?.Invoke(frame); LevelChanged?.Invoke(.35);
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(frame.Length/32.0),stop.Token);
                    }
                    playbackFinished.TrySetResult();
                }
                catch(OperationCanceledException) { playbackFinished.TrySetCanceled(stop.Token); }
                catch(Exception ex) { playbackFinished.TrySetException(ex); Failed?.Invoke(); }
            });
        }
        public void Stop()
        {
            stop.Cancel();
            lock(gate)
            {
                if(stream==null) return;
                stream.Flush(); stream.Dispose(); stream=null;
                if(path!=null) AudioRecorder.RepairHeader(path);
            }
        }
        public void Dispose()=>Stop();
    }
}
