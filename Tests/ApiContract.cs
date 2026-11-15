using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nava.Core;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Local-only HTTP contract fixtures. The placeholder key never leaves loopback.</summary>
public static class ApiContract
{
    public static async Task CustomWorkflowAsync(string scratch)
    {
        bool uploadChecked=false,startChecked=false,formatChecked=false;
        await using var storage=new FixtureServer(request=> {
            Require(!request.Headers.ContainsKey("Authorization"),"Service bearer leaked to presigned storage");
            Require(request.BodyText.Contains("name=key") || request.BodyText.Contains("name=\"key\""),"Missing upload key field");
            Require(request.BodyText.Contains("fixture-object") && request.BodyText.Contains("fixture-policy"),"Missing presigned multipart fields");
            Require(request.BodyText.Contains("audio/wav") && request.BodyText.Contains("synthetic-bytes"),"Missing multipart file or MIME");
            uploadChecked=true; return new(204,"");
        });
        await using var api=new FixtureServer(request=> {
            Require(request.Headers.GetValueOrDefault("Authorization")=="Bearer fixture-api-key","Missing service bearer");
            if(request.Target=="/healthz") return Json(new { status="ok" });
            if(request.Target=="/v1/transcriptions" && request.Method=="POST") {
                using var body=JsonDocument.Parse(request.BodyText);
                Require(body.RootElement.GetProperty("parameters").GetProperty("enable_speaker_diarization").GetBoolean(),"Missing diarization config");
                return Json(new { id="custom-job" });
            }
            if(request.Target.StartsWith("/v1/transcriptions/custom-job/upload-url")) return Json(new { url=storage.BaseUri+"upload",object_key="fixture-object",fields=new Dictionary<string,string> { ["key"]="fixture-object",["policy"]="fixture-policy" } });
            if(request.Target=="/v1/transcriptions/custom-job/async-transcription-bucket") { using var body=JsonDocument.Parse(request.BodyText); Require(body.RootElement.GetProperty("object_key").GetString()=="fixture-object","Wrong async object key"); startChecked=true; return Json(new { status="processing" }); }
            if(request.Target=="/v1/transcriptions/custom-job/async-status") return Json(new { status="completed" });
            if(request.Target=="/v1/transcriptions/custom-job") return new(200,"""{"id":"custom-job","tokens":[{"text":"Hello","start_ms":0,"end_ms":400,"speaker":"1"},{"text":" فارسی","start_ms":450,"end_ms":900,"speaker":"2"}]}""");
            if(request.Target=="/v1/text/smart-format") { using var body=JsonDocument.Parse(request.BodyText); Require(body.RootElement.GetProperty("duration_ms").GetInt64()>0,"duration_ms missing"); formatChecked=true; return Json(new { formatted_text="Hello, فارسی." }); }
            if(request.Target=="/v1/analysis") return Json(new { result="خلاصهٔ آزمایشی" });
            if(request.Target=="/v1/transcriptions/custom-job/name-speakers") return new(204,"");
            return new(404,"{}");
        });
        string file=Path.Combine(scratch,"custom-fixture.wav"); File.WriteAllText(file,"synthetic-bytes");
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client=new TranscriptionClient(new AppSettings { BaseUrl=api.BaseUri,ApiKey="fixture-api-key" });
        Require(await client.HealthAsync(ct.Token),"Health failed");
        string? createdId=null;
        var result=await client.TranscribeFileAsync(file,"fixture",null,ct.Token,onRemoteCreated:id=>createdId=id);
        Require(createdId=="custom-job" && result.Text=="Hello فارسی" && result.Segments.Count==2,"Wrong custom result");
        Require(uploadChecked && startChecked,"Incomplete custom workflow");
        Require((await client.SmartFormatAsync(result.Text,900,ct.Token)).Length>0 && formatChecked,"Formatting failed");
        Require((await client.AnalyzeAsync(result.RemoteId!,"summarize",null,ct.Token)).Length>0,"Analysis failed");
        await client.NameSpeakersAsync(result.RemoteId!,new() { ["1"]="آزمایشی" },ct.Token);
        api.ThrowIfFaulted(); storage.ThrowIfFaulted();
    }

    public static async Task NativeFallbackResumeAsync(string scratch)
    {
        int uploadCount=0,createCount=0; bool modelChecked=false;
        await using var api=new FixtureServer(request=> {
            Require(request.Headers.GetValueOrDefault("Authorization")=="Bearer fixture-native-key","Missing native bearer");
            if(request.Target=="/v1/transcriptions" && request.Method=="POST") {
                using var body=JsonDocument.Parse(request.BodyText);
                if(!body.RootElement.TryGetProperty("model",out var model)) return new(400,"{\"detail\":\"model and file_id are required\"}");
                Require(model.GetString()=="stt-async-v5","Native model selection failed");
                Require(body.RootElement.GetProperty("file_id").GetString()=="native-file","Missing native uploaded file_id");
                createCount++; return Json(new { id="native-job" });
            }
            if(request.Target=="/v1/models") { modelChecked=true; return Json(new { models=new[] { new { id="stt-async-v4" },new { id="stt-async-v5" },new { id="stt-rt-v5" } } }); }
            if(request.Target=="/v1/files") { uploadCount++; Require(request.BodyText.Contains("synthetic-bytes"),"Native file missing"); return Json(new { id="native-file" }); }
            if(request.Target=="/v1/transcriptions/native-job") return Json(new { id="native-job",status="completed",audio_duration_ms=1200 });
            if(request.Target=="/v1/transcriptions/native-job/transcript") return new(200,"""{"tokens":[{"text":"Hello","start_ms":0,"end_ms":500,"speaker":"1"},{"text":" team.","start_ms":500,"end_ms":1200,"speaker":"1"}]}""");
            return new(404,"{}");
        });
        string file=Path.Combine(scratch,"native-fixture.wav"); File.WriteAllText(file,"synthetic-bytes");
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client=new TranscriptionClient(new AppSettings { BaseUrl=api.BaseUri,ApiKey="fixture-native-key" });
        var result=await client.TranscribeFileAsync(file,"fixture",null,ct.Token);
        Require(client.ApiKind=="soniox" && result.ApiKind=="soniox","Native fallback not identified");
        Require(result.Text=="Hello team." && result.DurationMs==1200 && modelChecked,"Wrong native result");
        var resumed=await client.TranscribeFileAsync(file,"fixture",null,ct.Token,result.RemoteId);
        Require(resumed.Text==result.Text && uploadCount==1 && createCount==1,"Resume duplicated upload or job");
        api.ThrowIfFaulted();
    }

    public static async Task HttpErrorsDoNotUseProxyAsync()
    {
        int proxyRequests=0;
        await using var proxy=new FixtureServer(_=> { Interlocked.Increment(ref proxyRequests); return Json(new { status="ok" }); });
        await using var api=new FixtureServer(_=>new(401,"{}"));
        var client=new TranscriptionClient(new AppSettings { BaseUrl=api.BaseUri,ApiKey="fixture-key",ProxyUrl=proxy.BaseUri });
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool unauthorized=false;
        try { await client.HealthAsync(ct.Token); } catch(ServiceException e) { unauthorized=e.StatusCode==401; }
        Require(unauthorized && proxyRequests==0,"HTTP authentication error improperly retried through proxy");
        api.ThrowIfFaulted(); proxy.ThrowIfFaulted();
    }

    private static FixtureResponse Json(object value)=>new(200,JsonSerializer.Serialize(value));
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private sealed record FixtureRequest(string Method,string Target,Dictionary<string,string> Headers,byte[] Body) { public string BodyText=>Encoding.UTF8.GetString(Body); }
    private sealed record FixtureResponse(int Status,string Body);
    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource lifetime=new();
        private readonly Func<FixtureRequest,FixtureResponse> handler;
        private readonly ConcurrentQueue<Exception> errors=new();
        private readonly Task loop;
        public string BaseUri { get; }
        public FixtureServer(Func<FixtureRequest,FixtureResponse> handler)
        {
            this.handler=handler; listener.Start();
            BaseUri="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/";
            loop=RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while(!lifetime.IsCancellationRequested)
                {
                    using var connection=await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream=connection.GetStream();
                    FixtureResponse response;
                    try
                    {
                        using var header=new MemoryStream(); int state=0;
                        while(state<4) { var one=new byte[1]; int read=await stream.ReadAsync(one,lifetime.Token); if(read==0) throw new IOException("Truncated fixture request"); byte value=one[0]; header.WriteByte(value); state=(state,value) switch { (0,13)=>1,(1,10)=>2,(2,13)=>3,(3,10)=>4,(_,13)=>1,_=>0 }; if(header.Length>65536) throw new IOException("Oversize fixture headers"); }
                        var lines=Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                        var first=lines[0].Split(' '); var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                        foreach(var line in lines.Skip(1)) { int colon=line.IndexOf(':'); if(colon>0) headers[line[..colon]]=line[(colon+1)..].Trim(); }
                        int count=headers.TryGetValue("Content-Length",out var length)?int.Parse(length):0;
                        byte[] body=new byte[count]; await stream.ReadExactlyAsync(body,lifetime.Token);
                        response=handler(new(first[0],first[1],headers,body));
                    }
                    catch(Exception ex) when(ex is not OperationCanceledException) { errors.Enqueue(ex); response=new(500,"{}"); }
                    byte[] payload=Encoding.UTF8.GetBytes(response.Body);
                    byte[] headersBytes=Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headersBytes,lifetime.Token); await stream.WriteAsync(payload,lifetime.Token);
                }
            }
            catch(Exception ex) when(ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        public void ThrowIfFaulted() { if(errors.TryDequeue(out var error)) throw error; }
        public async ValueTask DisposeAsync() { lifetime.Cancel(); listener.Stop(); try { await loop; } catch { } lifetime.Dispose(); }
    }
}
