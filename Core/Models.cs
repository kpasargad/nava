using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Nava.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Raise(name); }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AppSettings
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public string Hotkey { get; set; } = "NumLock";
    public int MicrophoneId { get; set; } = -1;
    public bool Sounds { get; set; } = true;
    public bool AutoCopy { get; set; } = true;
    public bool AutoPaste { get; set; } = false;
    public bool KeepAudio { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool Diarization { get; set; } = true;
    public bool LiveTranscription { get; set; } = true;
    public string CharacterId { get; set; } = "ghost";
    public double OverlayLeft { get; set; } = -1;
    public double OverlayTop { get; set; } = -1;
}

public sealed class TranscriptSegment
{
    public string Text { get; set; } = "";
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public string Speaker { get; set; } = "";
    public string Language { get; set; } = "";
}

public sealed class TranscriptRecord : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? RemoteId { get; set; }
    public string ApiKind { get; set; } = "custom";
    private string _title = "یادداشت صوتی";
    public string Title { get => _title; set => Set(ref _title, value); }
    private string _text = "";
    public string Text { get => _text; set { Set(ref _text, value); Raise(nameof(Preview)); } }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public double DurationMs { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = new();
    public Dictionary<string, string> SpeakerNames { get; set; } = new();
    public Dictionary<string, string> Analyses { get; set; } = new();
    public string? AudioPath { get; set; }
    public string Status { get; set; } = "completed";
    [JsonIgnore] public string DateLabel => CreatedAt.ToString("yyyy/MM/dd • HH:mm");
    [JsonIgnore] public string Preview => Text.Length > 95 ? Text[..95] + "…" : Text;
    [JsonIgnore] public string DurationLabel => TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs>=3600000?@"hh\:mm\:ss":@"mm\:ss");
}

public sealed record MicrophoneDevice(int Id, string Name);
