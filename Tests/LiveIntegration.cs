using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nava.Core;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Opt-in network verification. Only uploads the generated test fixture; never opens the microphone.</summary>
public static class LiveIntegration
{
    public static async Task<int> RunAsync(string projectDirectory,string mode="--live")
    {
        string root=Path.GetFullPath(projectDirectory);
        string privateDirectory=Path.Combine(root,".local");
        string fixture=Path.Combine(privateDirectory,"test-english.wav");
        if(!File.Exists(fixture)) { Console.WriteLine("FAIL synthetic fixture missing"); return 1; }
        var settings=new LocalStore(privateDirectory).LoadSettings();
        if(string.IsNullOrWhiteSpace(settings.ApiKey)) { Console.WriteLine("FAIL Windows-user credentials unavailable"); return 1; }
        var client=new TranscriptionClient(settings);
        int passed=0,failed=0;
        bool runRealtime=mode!="--live-batch",runBatch=mode!="--live-rt",strict=mode=="--live-strict";
        string pendingIdPath=Path.Combine(privateDirectory,"live-test-pending-id.txt");
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(mode=="--live-rt"?2:8));
        var ct=deadline.Token;
        async Task Check(string label,Func<Task> action)
        {
            try { await action(); passed++; Console.WriteLine("PASS "+label); }
            catch(Exception ex) { failed++; Console.WriteLine("FAIL "+label+" status="+(ex is ServiceException service?service.StatusCode?.ToString()??"transport":"local")+" "+Redact(ex.Message,settings)); }
        }
        await Check("live health",async()=> { if(!await client.HealthAsync(ct)) throw new Exception("Health was not ok"); });
        if(runRealtime) await Check("live realtime synthetic audio",async()=> {
            int finalCount=0,callbackCount=0,partialUpdates=0; long firstCallbackMs=-1; bool streamedBeforeFinish=false,audioFinished=false;
            var clock=Stopwatch.StartNew();
            await using var session=await client.StartRealtimeAsync((segments,interim)=> {
                finalCount=segments.Count; callbackCount++;
                if(!string.IsNullOrWhiteSpace(interim)) partialUpdates++;
                if(segments.Count>0 || !string.IsNullOrWhiteSpace(interim)) {
                    if(firstCallbackMs<0) firstCallbackMs=clock.ElapsedMilliseconds;
                    if(!audioFinished) streamedBeforeFinish=true;
                }
            },ct);
            var pcm=ReadPcm16Mono16Khz(fixture);
            for(int offset=0;offset<pcm.Length;offset+=3200)
            {
                var frame=pcm.AsSpan(offset,Math.Min(3200,pcm.Length-offset)).ToArray();
                await session.SendAudioAsync(frame,ct);
            }
            audioFinished=true;
            var result=await session.FinishAsync(ct);
            if(string.IsNullOrWhiteSpace(result.Text) || result.Segments.Count==0) throw new Exception("No final speech tokens returned");
            File.WriteAllText(Path.Combine(privateDirectory,"live-rt-result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("INFO realtime final_tokens="+finalCount+" text_length="+result.Text.Length+" callbacks="+callbackCount+" partial_updates="+partialUpdates+" first_callback_ms="+firstCallbackMs+" streamed_before_finish="+streamedBeforeFinish.ToString().ToLowerInvariant());
            if(!streamedBeforeFinish) throw new Exception("Speech appeared only after finish; live streaming requirement not met");
            if(!result.Text.Contains("Monday",StringComparison.OrdinalIgnoreCase) || (!result.Text.Contains("ten",StringComparison.OrdinalIgnoreCase) && !result.Text.Contains("10",StringComparison.Ordinal))) throw new Exception("Final transcript lost expected ending words from the full synthetic fixture");
        });
        if(runRealtime) Console.WriteLine("INFO realtime_configuration="+JsonSerializer.Serialize(client.LastRealtimeDiagnostics));
        TranscriptRecord? transcription=null;
        if(runBatch) await Check("live batch create/upload/start/status/result",async()=> {
            string? pending=File.Exists(pendingIdPath)?File.ReadAllText(pendingIdPath).Trim():null;
            if(pending!=null && !Regex.IsMatch(pending,"^[A-Za-z0-9_-]{1,200}$")) pending=null;
            Console.WriteLine("STAGE batch resume="+(pending!=null?"true":"false"));
            var progress=new Progress<string>(stage=>Console.WriteLine("STAGE batch "+Redact(stage,settings)));
            transcription=await client.TranscribeFileAsync(fixture,"Nava synthetic QA",progress,ct,pending,id=> {
                File.WriteAllText(pendingIdPath,id);
                Console.WriteLine("STAGE batch remote ID saved for resume");
            });
            if(string.IsNullOrWhiteSpace(transcription.RemoteId) || string.IsNullOrWhiteSpace(transcription.Text)) throw new Exception("Empty result or missing remote ID");
            if(!transcription.Text.Contains("meeting",StringComparison.OrdinalIgnoreCase) && !transcription.Text.Contains("Monday",StringComparison.OrdinalIgnoreCase)) throw new Exception("Result did not match generated speech fixture");
            Console.WriteLine("INFO batch text_length="+transcription.Text.Length+" segments="+transcription.Segments.Count);
            File.WriteAllText(Path.Combine(privateDirectory,"live-test-result.json"),JsonSerializer.Serialize(transcription,new JsonSerializerOptions { WriteIndented=true }));
        });
        if(mode is "--live-rt" or "--live-batch")
        {
            Console.WriteLine("INFO service_api="+client.ApiKind);
            Console.WriteLine($"LIVE RESULT {passed} passed; {failed} failed");
            return failed==0?0:1;
        }
        if(transcription==null && client.ApiKind!="soniox") await DiagnoseCreateAsync(settings,ct);
        bool native=transcription?.ApiKind=="soniox" || client.ApiKind=="soniox";
        if(native && !strict)
        {
            Console.WriteLine("UNAVAILABLE smart-format: native service has no documented formatting endpoint");
            Console.WriteLine("UNAVAILABLE analysis: native service has no documented analysis endpoint");
            Console.WriteLine("UNAVAILABLE remote speaker naming: native service; names remain local in app");
        }
        else
        {
        await Check("live smart-format mandatory duration",async()=> {
            var formatted=await client.SmartFormatAsync("hello team the meeting is on monday",12000,ct);
            if(string.IsNullOrWhiteSpace(formatted)) throw new Exception("Empty formatted text");
            Console.WriteLine("INFO formatted_length="+formatted.Length);
        });
        if(transcription?.RemoteId is string remoteId)
        {
            foreach(var action in new[] { "summarize","to_do_list" })
                await Check("live analysis "+action,async()=> { var result=await client.AnalyzeAsync(remoteId,action,null,ct); if(string.IsNullOrWhiteSpace(result)) throw new Exception("Empty analysis"); Console.WriteLine("INFO analysis_length="+result.Length); });
            var firstSpeaker=transcription.Segments.Select(x=>x.Speaker).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x))??"1";
            await Check("live speaker naming",()=>client.NameSpeakersAsync(remoteId,new Dictionary<string,string> { [firstSpeaker]="گویندهٔ آزمایشی" },ct));
        }
        else Console.WriteLine("SKIP analysis and speaker naming: no persisted transcript");
        }
        Console.WriteLine($"LIVE RESULT {passed} passed; {failed} failed");
        return failed==0?0:1;
    }

    private static async Task DiagnoseCreateAsync(AppSettings settings,CancellationToken ct)
    {
        // Emit only a redacted validation body, never request headers, URLs, or keys.
        try
        {
            using var handler=new SocketsHttpHandler { UseProxy=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(8) };
            using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(20) };
            using var request=new HttpRequestMessage(HttpMethod.Post,settings.BaseUrl.TrimEnd('/')+"/v1/transcriptions");
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.ApiKey);
            request.Content=new StringContent(JsonSerializer.Serialize(new { name="Nava synthetic QA diagnostic",transcription_type="transcription",parameters=new { language_hints=new[] { "fa","en" },enable_speaker_diarization=true,enable_language_identification=true } }),Encoding.UTF8,"application/json");
            using var response=await client.SendAsync(request,ct);
            var body=Redact(await response.Content.ReadAsStringAsync(ct),settings);
            Console.WriteLine("DIAGNOSTIC create status="+(int)response.StatusCode+" body="+body[..Math.Min(body.Length,1800)].Replace('\r',' ').Replace('\n',' '));
        }
        catch(Exception ex) { Console.WriteLine("DIAGNOSTIC unavailable "+Redact(ex.Message,settings)); }
    }

    private static string Redact(string text,AppSettings settings)
    {
        if(settings.ApiKey.Length>0) text=text.Replace(settings.ApiKey,"[redacted]",StringComparison.Ordinal);
        if(settings.ProxyUrl.Length>0) text=text.Replace(settings.ProxyUrl,"[redacted-proxy]",StringComparison.Ordinal);
        if(Uri.TryCreate(settings.ProxyUrl,UriKind.Absolute,out var uri) && uri.UserInfo.Length>0)
            foreach(var value in uri.UserInfo.Split(':')) if(value.Length>0) text=text.Replace(Uri.UnescapeDataString(value),"[redacted]",StringComparison.Ordinal);
        return Regex.Replace(text,"(?i)(api_key|authorization|token|password)(\\\"?\\s*[:=]\\s*\\\"?)[^\\\"\\s,}]+","$1$2[redacted]");
    }

    public static byte[] ReadPcm16Mono16Khz(string path)
    {
        using var stream=File.OpenRead(path); using var reader=new BinaryReader(stream);
        if(new string(reader.ReadChars(4))!="RIFF") throw new Exception("Fixture must be RIFF WAV"); reader.ReadInt32();
        if(new string(reader.ReadChars(4))!="WAVE") throw new Exception("Fixture must be WAV");
        ushort format=0,channels=0,bits=0; int rate=0; byte[]? data=null;
        while(stream.Position+8<=stream.Length)
        {
            string name=new(reader.ReadChars(4)); uint length=reader.ReadUInt32(); long next=stream.Position+length+(length&1);
            if(next>stream.Length) throw new Exception("Malformed WAV chunk");
            if(name=="fmt ") { format=reader.ReadUInt16(); channels=reader.ReadUInt16(); rate=reader.ReadInt32(); reader.ReadInt32(); reader.ReadUInt16(); bits=reader.ReadUInt16(); }
            else if(name=="data") data=reader.ReadBytes(checked((int)length));
            stream.Position=next;
        }
        if(data==null || format!=1 || channels<1 || channels>2 || rate<8000 || (bits!=8 && bits!=16)) throw new Exception("Fixture must be mono/stereo PCM8/16 WAV");
        int bytesPerSample=bits/8; int inputFrames=data.Length/(bytesPerSample*channels);
        if(inputFrames==0) throw new Exception("Empty WAV fixture");
        var output=new byte[checked((int)Math.Ceiling(inputFrames*16000.0/rate))*2];
        double Sample(int frame)
        {
            frame=Math.Clamp(frame,0,inputFrames-1); double sum=0;
            for(int channel=0;channel<channels;channel++) { int offset=(frame*channels+channel)*bytesPerSample; sum+=bits==16?BitConverter.ToInt16(data,offset):(data[offset]-128)*256; }
            return sum/channels;
        }
        for(int i=0;i<output.Length/2;i++)
        {
            double position=i*(rate/16000.0); int left=(int)position; double fraction=position-left;
            short sample=(short)Math.Clamp(Math.Round(Sample(left)*(1-fraction)+Sample(left+1)*fraction),short.MinValue,short.MaxValue);
            output[i*2]=(byte)(sample&255); output[i*2+1]=(byte)((sample>>8)&255);
        }
        return output;
    }
}
