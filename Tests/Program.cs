using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nava.Core;
using Nava.Services;

if (args.Length > 0 && args[0] == "--inspect-shell")
{
    if(args.Length!=2 || !int.TryParse(args[1],out int inspectedPid)) { Console.WriteLine("FAIL use --inspect-shell with a Nava process ID"); return 2; }
    return Nava.Tests.ShellWindowInspector.Run(inspectedPid);
}
if (args.Length > 0 && args[0] == "--shell-window-smoke")
    return Nava.Tests.ShellWindowSmoke.Run(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
if (args.Length > 0 && args[0] == "--overlay-pointer")
    return Nava.Tests.OverlayPointerSmoke.Run(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
if (args.Length > 0 && args[0] == "--visual-smoke")
{
    Nava.Tests.WpfPreview.Run(args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(),".local"));
    Console.WriteLine("PASS own-control visual smoke renders");
    return 0;
}
if (args.Length > 0 && args[0] == "--controller-live")
    return await Nava.Tests.ControllerLiveSmoke.RunAsync(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
if (args.Length > 0 && args[0] == "--native-capture")
    return await Nava.Tests.NativeCaptureSmoke.RunAsync(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
if (args.Length > 1 && args[0] == "--check-package")
{
    Nava.Tests.ReleaseChecks.VerifyCleanPayload(args[1]);
    Console.WriteLine("PASS release payload contains no private configuration or audio");
    return 0;
}
if (args.Length > 0 && args[0] is "--live" or "--live-rt" or "--live-batch" or "--live-strict")
    return await Nava.Tests.LiveIntegration.RunAsync(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory(),args[0]);

var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);
int passed = 0, failed = 0;
void Test(string name, Action run)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
string Fresh(string name) { var path=Path.Combine(testRoot,name); Directory.CreateDirectory(path); return path; }

try
{
    Test("character catalog provides ten stable distinct choices", () => Nava.Tests.ReleaseChecks.CharacterCatalogContract());
    Test("character selection persists and legacy settings recover", () => Nava.Tests.ReleaseChecks.CharacterSettingsRoundTrip(Fresh("character-settings")));
    Test("fresh release has no private endpoint or credentials", () => Nava.Tests.ReleaseChecks.FreshInstallDefaults());
    Test("installed and portable builds keep data in the correct location", () => Nava.Tests.ReleaseChecks.InstalledVersusPortableDataLocation());
    Test("first launch creates safe defaults and directories", () => {
        var store = new LocalStore(Fresh("first-launch"));
        var settings = store.LoadSettings();
        Assert(settings.Hotkey == "NumLock"); Assert(settings.Diarization); Assert(settings.LiveTranscription);
        Assert(!settings.AutoPaste, "Auto paste should require explicit opt-in");
        Assert(!settings.StartWithWindows, "Startup should require explicit opt-in");
        Assert(Directory.Exists(store.AudioDirectory)); Assert(store.LoadRecords().Count == 0);
    });
    Test("DPAPI credentials round trip without plaintext files", () => {
        var store = new LocalStore(Fresh("credential-roundtrip"));
        var settings = new AppSettings { ApiKey="qa-secret-"+Guid.NewGuid().ToString("N"), ProxyUrl="http://test-user:test-password@127.0.0.1:19000", Hotkey="Ctrl+Alt+Space", KeepAudio=true };
        store.SaveSettings(settings);
        var loaded=store.LoadSettings();
        Assert(loaded.ApiKey==settings.ApiKey); Assert(loaded.ProxyUrl==settings.ProxyUrl); Assert(loaded.Hotkey==settings.Hotkey); Assert(loaded.KeepAudio);
        foreach (var file in Directory.EnumerateFiles(store.Root))
        {
            var content=File.ReadAllText(file);
            Assert(!content.Contains(settings.ApiKey), "Plaintext API key in saved file");
            Assert(!content.Contains("test-password"), "Plaintext proxy password in saved file");
        }
        Assert(settings.ApiKey==loaded.ApiKey, "Save must not erase caller credentials");
    });
    Test("corrupted public settings recover to defaults", () => {
        var store=new LocalStore(Fresh("corrupt-settings"));
        File.WriteAllText(Path.Combine(store.Root,"settings.json"),"{invalid");
        Assert(store.LoadSettings().Hotkey=="NumLock");
    });
    Test("corrupted encrypted credentials do not crash startup", () => {
        var store=new LocalStore(Fresh("corrupt-credentials"));
        File.WriteAllText(Path.Combine(store.Root,"credentials.bin"),"broken-not-base64!");
        Assert(store.LoadSettings().ApiKey=="");
    });
    Test("bilingual transcript metadata persists exactly", () => {
        var store=new LocalStore(Fresh("record-roundtrip"));
        var record=new TranscriptRecord {
            Title="جلسهٔ تیم — weekly sync", Text="سلام. Let's start.", DurationMs=12345,
            RemoteId="remote-id", Status="failed", AudioPath=Path.Combine(store.AudioDirectory,"recovery.wav"),
            Segments=new() { new() { Text="سلام.", StartMs=100,EndMs=1200,Speaker="1",Language="fa" }, new() { Text=" Let's start.", StartMs=1300,EndMs=2400,Speaker="2",Language="en" } },
            SpeakerNames=new() { ["1"]="سارا", ["2"]="Alex" }, Analyses=new() { ["summarize"]="پیگیری کارها" }
        };
        store.Save(record); var loaded=store.LoadRecords().Single();
        Assert(loaded.Id==record.Id); Assert(loaded.Text==record.Text); Assert(loaded.Title==record.Title);
        Assert(loaded.RemoteId==record.RemoteId); Assert(loaded.Status=="failed"); Assert(loaded.AudioPath==record.AudioPath);
        Assert(loaded.Segments.Count==2); Assert(loaded.SpeakerNames["1"]=="سارا"); Assert(loaded.Analyses["summarize"]=="پیگیری کارها");
    });
    Test("updating a transcript does not duplicate history", () => {
        var store=new LocalStore(Fresh("record-update")); var record=new TranscriptRecord { Text="اول" };
        store.Save(record); record.Text="ویرایش"; store.Save(record);
        Assert(store.LoadRecords().Single().Text=="ویرایش");
        Assert(!Directory.EnumerateFiles(Path.Combine(store.Root,"History"),"*.tmp").Any());
    });
    Test("history skips malformed record while loading valid record", () => {
        var store=new LocalStore(Fresh("record-corrupt")); store.Save(new TranscriptRecord { Text="معتبر" });
        File.WriteAllText(Path.Combine(store.Root,"History","broken.json"),"{bad");
        Assert(store.LoadRecords().Single().Text=="معتبر");
    });
    Test("deleting imported transcript preserves original audio", () => {
        var store=new LocalStore(Fresh("import-delete")); var original=Path.Combine(testRoot,"original.wav"); File.WriteAllText(original,"original audio");
        var record=new TranscriptRecord { AudioPath=original }; store.Save(record); store.Delete(record);
        Assert(File.Exists(original),"Imported original was deleted"); Assert(!store.LoadRecords().Any());
    });
    Test("deleting owned recording removes its owned audio", () => {
        var store=new LocalStore(Fresh("owned-delete")); var owned=Path.Combine(store.AudioDirectory,"owned.wav"); File.WriteAllText(owned,"recording");
        var record=new TranscriptRecord { AudioPath=owned }; store.Save(record); store.Delete(record);
        Assert(!File.Exists(owned)); Assert(!store.LoadRecords().Any());
    });
    Test("audio path prefix collision cannot delete sibling directory", () => {
        var store=new LocalStore(Fresh("path-boundary")); var sibling=store.AudioDirectory+"-originals"; Directory.CreateDirectory(sibling);
        var original=Path.Combine(sibling,"keep.wav"); File.WriteAllText(original,"original"); store.DeleteOwnedAudio(original);
        Assert(File.Exists(original));
    });
    Test("SRT groups word tokens without mutating source", () => {
        var record=new TranscriptRecord { Segments=new() { new() { Text="سلام", StartMs=100,EndMs=400,Speaker="1" },new() { Text=" دنیا", StartMs=450,EndMs=900,Speaker="1" } },SpeakerNames=new() { ["1"]="سارا" } };
        var srt=LocalStore.ToSrt(record);
        Assert(srt.Contains("00:00:00,100 --> 00:00:00,900")); Assert(srt.Contains("سارا: سلام دنیا"));
        Assert(!srt.Contains(Environment.NewLine+"2"+Environment.NewLine));
        Assert(record.Segments[0].Text=="سلام" && record.Segments[0].EndMs==400,"Export mutated source segments");
    });
    Test("SRT splits speakers and long pauses", () => {
        var record=new TranscriptRecord { Segments=new() { new() { Text="A",StartMs=0,EndMs=200,Speaker="1" },new() { Text="B",StartMs=250,EndMs=500,Speaker="2" },new() { Text="C",StartMs=3000,EndMs=3200,Speaker="2" } } };
        var srt=LocalStore.ToSrt(record); Assert(Regex.Matches(srt," --> ").Count==3);
    });
    Test("SRT exports text-only result with nonzero duration", () => {
        var srt=LocalStore.ToSrt(new TranscriptRecord { Text="متن انگلیسی English" });
        Assert(srt.Contains("00:00:00,000 --> 00:00:01,000")); Assert(srt.Contains("متن انگلیسی English"));
    });
    Test("speaker text export preserves bilingual words and named speakers", () => {
        var record=new TranscriptRecord { Segments=new() { new() { Text="سلام",StartMs=100,EndMs=300,Speaker="1" },new() { Text=" Hello",StartMs=400,EndMs=700,Speaker="2" } },SpeakerNames=new() { ["1"]="سارا",["2"]="Alex" } };
        var text=LocalStore.TextWithSpeakers(record); Assert(text.Contains("سارا")); Assert(text.Contains("Alex")); Assert(text.Contains("سلام")); Assert(text.Contains("Hello"));
    });
    Test("text-only speaker export remains exact", () => {
        const string value="فارسی\nEnglish\nکلمهٔ ترکیبی";
        Assert(LocalStore.TextWithSpeakers(new TranscriptRecord { Text=value })==value);
    });
    Test("global shortcut parsing preserves NumLock and disables repeats", () => {
        var numlock=GlobalHotkey.Parse("NumLock"); Assert(numlock.Key==0x90); Assert((numlock.Modifiers&0x4000)!=0);
        var chord=GlobalHotkey.Parse("Ctrl+Alt+Space"); Assert(chord.Key==0x20); Assert((chord.Modifiers&3)==3); Assert((chord.Modifiers&0x4000)!=0);
    });
    Test("push-to-talk physical hold transitions ignore repeats and injected input", () => Nava.Tests.HotkeyHoldContract.Run());
    Test("unsafe shortcut inputs are rejected", () => {
        foreach(var input in new[] { "A", "F12", "Ctrl+LeftCtrl", "Ctrl+Banana", "", "Hyper+Space" })
        {
            bool rejected=false; try { GlobalHotkey.Parse(input); } catch(ArgumentException) { rejected=true; }
            Assert(rejected,"Accepted unsupported shortcut: "+input);
        }
    });
    Test("interrupted PCM recording header repairs without losing samples", () => {
        var path=Path.Combine(Fresh("wave-recovery"),"interrupted.wav");
        var samples=Enumerable.Range(0,3200).Select(i=>(byte)(i%251)).ToArray();
        using(var stream=File.Create(path)) { stream.Write(new byte[44]); stream.Write(samples); }
        AudioRecorder.RepairHeader(path);
        var restored=File.ReadAllBytes(path);
        Assert(Encoding.ASCII.GetString(restored,0,4)=="RIFF");
        Assert(Encoding.ASCII.GetString(restored,8,8)=="WAVEfmt ");
        Assert(BitConverter.ToInt32(restored,4)==restored.Length-8);
        Assert(BitConverter.ToInt32(restored,24)==16000);
        Assert(BitConverter.ToInt16(restored,22)==1);
        Assert(BitConverter.ToInt16(restored,34)==16);
        Assert(BitConverter.ToInt32(restored,40)==samples.Length);
        Assert(restored.AsSpan(44).SequenceEqual(samples));
    });
    Test("normalizer preserves final bilingual token text and millisecond timing", () => {
        using var json=JsonDocument.Parse("""{"id":"job-1","tokens":[{"text":"س","start_ms":100,"end_ms":250,"speaker":"1","language":"fa","is_final":true},{"text":"لام","start_ms":250,"end_ms":500,"speaker":"1","language":"fa","is_final":true},{"text":" Hello","start_ms":600,"end_ms":1000,"speaker":"2","language":"en","is_final":true},{"text":" ignored","is_final":false},{"text":"<end>","is_final":true}]}""");
        var record=TranscriptionClient.NormalizeTranscript(json.RootElement);
        Assert(record.RemoteId=="job-1"); Assert(record.Text=="سلام Hello"); Assert(record.Segments.Count==3);
        Assert(record.Segments[0].StartMs==100 && record.Segments[2].EndMs==1000); Assert(record.DurationMs==1000); Assert(record.Segments[2].Speaker=="2"); Assert(record.Segments[2].Language=="en");
    });
    Test("normalizer unwraps data and converts segment seconds once", () => {
        using var json=JsonDocument.Parse("""{"data":{"transcription_id":"job-2","name":"جلسه","duration_ms":5000,"speaker_names":{"1":"سارا"},"result":{"segments":[{"text":"سلام دنیا","start":1.25,"end":2.5,"speaker_id":1,"language_code":"fa"},{"text":"Hello","start":3,"end":4,"speaker_id":2,"language_code":"en"}]}}}""");
        var record=TranscriptionClient.NormalizeTranscript(json.RootElement);
        Assert(record.RemoteId=="job-2"); Assert(record.Title=="جلسه"); Assert(record.DurationMs==5000);
        Assert(record.Segments[0].StartMs==1250 && record.Segments[0].EndMs==2500); Assert(record.SpeakerNames["1"]=="سارا");
        Assert(record.Text.Contains("سلام دنیا") && record.Text.Contains("Hello"));
    });
    Test("normalizer supports text-only results and guards negative timestamps", () => {
        using var textJson=JsonDocument.Parse("""{"transcription":{"transcription_text":"English فارسی","audio_duration_ms":12000}}""");
        var textRecord=TranscriptionClient.NormalizeTranscript(textJson.RootElement);
        Assert(textRecord.Text=="English فارسی" && textRecord.DurationMs==12000);
        using var negativeJson=JsonDocument.Parse("""{"tokens":[{"text":"x","start_ms":-200,"end_ms":100}]}""");
        Assert(TranscriptionClient.NormalizeTranscript(negativeJson.RootElement).Segments[0].StartMs==0);
    });
    Test("audio MIME values match upload contract", () => {
        Assert(TranscriptionClient.Mime("clip.M4A")=="audio/mp4"); Assert(TranscriptionClient.Mime("clip.wav")=="audio/wav");
        Assert(TranscriptionClient.Mime("clip.mp3")=="audio/mpeg"); Assert(TranscriptionClient.Mime("clip.webm")=="audio/webm");
    });
    Test("realtime null and zero error codes are normal messages", () => {
        foreach(var payload in new[] { "{}", "{\"error_code\":null}","{\"error_code\":0}","{\"error_code\":\"0\",\"error\":null}","{\"error\":false}","{\"error\":\"\"}" })
        {
            using var json=JsonDocument.Parse(payload);
            Assert(!RealtimeSession.TryReadError(json.RootElement,out var message),"False-positive realtime error");
            Assert(message.Length==0);
        }
    });
    Test("realtime error details redact secrets and remain bounded", () => {
        const string key="temporary-secret-value";
        var payload=JsonSerializer.Serialize(new { error_code=400,error_type="bad_input",error_message="bad config "+key+" Bearer abcdefghijklmnopqrstuvwxyz0123456789 "+new string('x',500) });
        using var json=JsonDocument.Parse(payload);
        Assert(RealtimeSession.TryReadError(json.RootElement,out var message,key));
        Assert(message.Contains("400") && message.Contains("bad_input"));
        Assert(!message.Contains(key) && !message.Contains("abcdefghijklmnopqrstuvwxyz0123456789"));
        Assert(message.Length<450,"Realtime diagnostics are unbounded");
    });
    Test("custom API complete workflow preserves upload credential boundary", () => Nava.Tests.ApiContract.CustomWorkflowAsync(Fresh("custom-contract")).GetAwaiter().GetResult());
    Test("native API fallback selects model and resumes without duplicate upload", () => Nava.Tests.ApiContract.NativeFallbackResumeAsync(Fresh("native-contract")).GetAwaiter().GetResult());
    Test("HTTP authentication errors do not retry through proxy", () => Nava.Tests.ApiContract.HttpErrorsDoNotUseProxyAsync().GetAwaiter().GetResult());
    Test("failed settings writes preserve previous credentials and endpoint", () => Nava.Tests.ControllerFailureRegression.SettingsWriteFailurePreservesPreviousCredentials(Fresh("settings-rollback")));
    Test("valid incomplete history JSON recovers safe text and collections", () => Nava.Tests.ControllerFailureRegression.NullHistoryFieldsRecover(Fresh("history-null-fields")));
    Test("audio recovery preserves extended differently sampled and unrelated files", () => Nava.Tests.NativeSafetyRegression.WaveRecoveryPreservesUnrelatedAudio(Fresh("safe-wave-recovery")));
    Test("automatic paste requires unchanged focus and released modifiers", () => Nava.Tests.NativeSafetyRegression.PasteRequiresOriginalFocusAndReleasedModifiers());
    Test("controller persistence failures never retain capture or lose recoverable audio", () => Nava.Tests.ControllerFailureRegression.RunAsync(Fresh("controller-failures")).GetAwaiter().GetResult());
    Test("realtime disposal cancels active send and waiters without semaphore races", () => Nava.Tests.RealtimeRaceRegression.DisposeCancelsActiveSendAndWaitersAsync().GetAwaiter().GetResult());
    Test("realtime disposal cancels speech pacing before sending queued audio", () => Nava.Tests.RealtimeRaceRegression.DisposeCancelsPacingDelayAsync().GetAwaiter().GetResult());
    Test("Alt+Tab exclusion preserves layered rendering activation and unrelated native flags", () => Nava.Tests.ShellWindowSmoke.VerifyStyleTransform());
}
finally
{
    if (testRoot.StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"scratch"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) Directory.Delete(testRoot,true);
}
Console.WriteLine($"RESULT {passed} passed; {failed} failed");
return failed==0 ? 0 : 1;
