using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Explicit local microphone test. Never uploads, plays, or prints captured audio.</summary>
public static class NativeCaptureSmoke
{
    public static async Task<int> RunAsync(string projectDirectory)
    {
        var root=Path.GetFullPath(projectDirectory);
        var scratch=Path.GetFullPath(Path.Combine(root,".local","native-capture-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(scratch);
        var fixtures=new List<string>();
        int passed=0,failed=0;
        Console.WriteLine("NATIVE CAPTURE local microphone; two 450 ms recordings; no upload or playback");
        try
        {
            for(int cycle=1;cycle<=2;cycle++)
            {
                var file=Path.Combine(scratch,"capture-"+cycle+".wav"); fixtures.Add(file);
                try
                {
                    using var capture=new AudioRecorder();
                    int failures=0,frames=0;
                    long frameBytes=0;
                    capture.Failed+=()=>Interlocked.Increment(ref failures);
                    capture.DataAvailable+=data=> { Interlocked.Increment(ref frames); Interlocked.Add(ref frameBytes,data.Length); };
                    capture.Start(file,-1);
                    await Task.Delay(450);
                    var stopping=Stopwatch.StartNew();
                    await Task.Run(capture.Stop);
                    stopping.Stop();
                    if(stopping.Elapsed>TimeSpan.FromMilliseconds(2500)) throw new InvalidOperationException("Stop exceeded bounded drain time.");
                    if(failures!=0) throw new InvalidOperationException("Capture reported a native or disk failure.");
                    using(var stream=File.OpenRead(file))
                    {
                        var header=new byte[44]; stream.ReadExactly(header);
                        if(System.Text.Encoding.ASCII.GetString(header,0,4)!="RIFF" || System.Text.Encoding.ASCII.GetString(header,8,8)!="WAVEfmt " || System.Text.Encoding.ASCII.GetString(header,36,4)!="data")
                            throw new InvalidOperationException("Native capture did not finalize its PCM WAV header.");
                        if(BitConverter.ToInt16(header,20)!=1 || BitConverter.ToInt16(header,22)!=1 || BitConverter.ToInt32(header,24)!=16000 || BitConverter.ToInt16(header,34)!=16)
                            throw new InvalidOperationException("Native capture returned an unexpected PCM format.");
                        int bytes=BitConverter.ToInt32(header,40);
                        if(bytes<=0 || (bytes&1)!=0 || bytes!=stream.Length-44 || BitConverter.ToInt32(header,4)!=stream.Length-8)
                            throw new InvalidOperationException("Native capture WAV sizes do not match stored samples.");
                        if(frames==0 || frameBytes!=bytes || Math.Abs(capture.DurationMs-bytes/32.0)>0.1)
                            throw new InvalidOperationException("Native callback delivery lost or duplicated sample bytes.");
                        Console.WriteLine($"PASS native cycle={cycle} frames={frames} pcm_bytes={bytes} duration_ms={capture.DurationMs:0} stop_ms={stopping.Elapsed.TotalMilliseconds:0}");
                        passed++;
                    }
                }
                catch(Exception ex)
                {
                    failed++;
                    // Only fixed diagnostic labels are emitted; no native exception
                    // text, device name, filesystem path, or audio content is printed.
                    Console.WriteLine($"FAIL native cycle={cycle} error={ex.GetType().Name}");
                }
                finally { TryDeleteOwned(file,scratch,ref failed); }
                await Task.Delay(100);
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            foreach(var file in fixtures) TryDeleteOwned(file,scratch,ref failed);
            try { if(Directory.Exists(scratch)) Directory.Delete(scratch,false); }
            catch { failed++; Console.WriteLine("FAIL native temporary directory cleanup"); }
        }
        Console.WriteLine($"NATIVE CAPTURE RESULT {passed} passed; {failed} failed");
        return failed==0?0:1;
    }

    private static void TryDeleteOwned(string file,string scratch,ref int failures)
    {
        string resolved=Path.GetFullPath(file);
        string prefix=Path.GetFullPath(scratch)+Path.DirectorySeparatorChar;
        if(!resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe capture fixture path.");
        try { if(File.Exists(resolved)) File.Delete(resolved); }
        catch { failures++; Console.WriteLine("FAIL native temporary audio cleanup"); }
    }
}
