using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nava.Core;

namespace Nava.Services;

public sealed class LocalStore
{
    public string Root { get; }
    public string AudioDirectory => Path.Combine(Root, "Audio");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public LocalStore(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("NAVA_DATA_DIR") ?? DefaultRoot;
        Directory.CreateDirectory(Root); Directory.CreateDirectory(AudioDirectory); Directory.CreateDirectory(Path.Combine(Root, "History"));
    }
    public static string DefaultRoot => File.Exists(Path.Combine(AppContext.BaseDirectory,"installed.flag"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nava")
        : Path.Combine(AppContext.BaseDirectory,"Data");
    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>(Path.Combine(Root, "settings.json")) ?? new();
        var credentialFile = Path.Combine(Root, "credentials.bin");
        if (File.Exists(credentialFile))
        {
            try
            {
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(credentialFile)), null, DataProtectionScope.CurrentUser);
                try { var secrets = JsonSerializer.Deserialize<Dictionary<string,string>>(clear); settings.ApiKey = secrets?.GetValueOrDefault("ApiKey", "") ?? ""; settings.ProxyUrl = secrets?.GetValueOrDefault("ProxyUrl", "") ?? ""; }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            catch (Exception e) when (e is CryptographicException or FormatException or JsonException or IOException)
            {
                // Corrupted/copied portable credentials must not prevent launch.
                settings.ApiKey = ""; settings.ProxyUrl = "";
            }
        }
        settings.CharacterId=CharacterCatalog.Normalize(settings.CharacterId);
        return settings;
    }
    public void SaveSettings(AppSettings settings)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(new { settings.ApiKey, settings.ProxyUrl });
        try { Atomic(Path.Combine(Root, "credentials.bin"), Convert.ToBase64String(ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser))); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var safe = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        safe.ApiKey = ""; safe.ProxyUrl = "";
        Atomic(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(safe, Json));
    }
    public List<TranscriptRecord> LoadRecords() => Directory.EnumerateFiles(Path.Combine(Root, "History"), "*.json").Select(Read<TranscriptRecord>).Where(x => x != null).Cast<TranscriptRecord>().OrderByDescending(x => x.CreatedAt).ToList();
    public void Save(TranscriptRecord record) => Atomic(Path.Combine(Root, "History", SafeId(record.Id) + ".json"), JsonSerializer.Serialize(record, Json));
    public void Delete(TranscriptRecord record)
    {
        File.Delete(Path.Combine(Root, "History", SafeId(record.Id) + ".json"));
        DeleteOwnedAudio(record.AudioPath);
    }
    public void DeleteOwnedAudio(string? path)
    {
        if (path == null) return;
        var full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetFullPath(AudioDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) File.Delete(full);
    }
    public static string TextWithSpeakers(TranscriptRecord record)
    {
        if (record.Segments.Count == 0) return record.Text;
        var sb = new StringBuilder(); string last = "";
        foreach (var segment in record.Segments)
        {
            if (segment.Speaker != last) { if (sb.Length > 0) sb.AppendLine(); var who = record.SpeakerNames.GetValueOrDefault(segment.Speaker, "گوینده " + segment.Speaker); sb.AppendLine($"[{TimeSpan.FromMilliseconds(segment.StartMs):hh\\:mm\\:ss}] {who}"); last = segment.Speaker; }
            sb.Append(segment.Text);
        }
        return sb.ToString();
    }
    public static string ToSrt(TranscriptRecord record)
    {
        var segments = record.Segments.Count > 0 ? record.Segments : new List<TranscriptSegment> { new() { Text = record.Text, EndMs = Math.Max(record.DurationMs,1000) } };
        // Group word tokens into readable subtitle cues rather than flashing a word per cue.
        var cues = new List<TranscriptSegment>();
        foreach (var s in segments)
        {
            var prev = cues.LastOrDefault();
            if (prev != null && prev.Speaker == s.Speaker && s.EndMs - prev.StartMs <= 5000 && prev.Text.Length + s.Text.Length < 100 && s.StartMs - prev.EndMs < 1200) { prev.Text += s.Text; prev.EndMs = Math.Max(prev.EndMs,s.EndMs); }
            else cues.Add(new() { Text=s.Text, StartMs=s.StartMs, EndMs=Math.Max(s.EndMs,s.StartMs+150), Speaker=s.Speaker });
        }
        var output = new StringBuilder(); int index = 1;
        foreach (var cue in cues) { output.AppendLine((index++).ToString()); output.AppendLine($"{Stamp(cue.StartMs)} --> {Stamp(cue.EndMs)}"); output.AppendLine(string.IsNullOrWhiteSpace(cue.Speaker) ? cue.Text.Trim() : record.SpeakerNames.GetValueOrDefault(cue.Speaker, "گوینده " + cue.Speaker) + ": " + cue.Text.Trim()); output.AppendLine(); }
        return output.ToString();
    }
    private static string Stamp(double ms) => TimeSpan.FromMilliseconds(Math.Max(0,ms)).ToString(@"hh\:mm\:ss\,fff");
    private static string SafeId(string id) => new(id.Where(char.IsAsciiLetterOrDigit).ToArray());
    private static T? Read<T>(string path) { try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); } catch (Exception e) when (e is IOException or JsonException) { return default; } }
    private static void Atomic(string path,string content) { var temp = path + ".tmp"; File.WriteAllText(temp,content, new UTF8Encoding(false)); File.Move(temp,path,true); }
}
