using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nava.Core;

namespace Nava.Services;

public sealed class RealtimeSession : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Action<IReadOnlyList<TranscriptSegment>, string> _onTokens;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _audioLock = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly object _finishGate = new();
    private readonly CancellationToken _lifetimeToken;
    private Task? _disposeTask;
    private Task<TranscriptRecord>? _finishTask;
    private readonly TaskCompletionSource<TranscriptRecord> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TranscriptSegment> _finalTokens = new();
    private readonly string _apiKind;
    private readonly string? _temporaryKey;
    private readonly Task _receiver;
    private readonly Task _keepAlive;
    private string? _remoteId;
    private volatile bool _finishing;
    private volatile bool _disposed;
    private readonly Stopwatch _audioClock = new();
    private long _sentAudioBytes;
    public long MessagesReceived { get; private set; }
    public double FinalAudioProcessedMs { get; private set; }
    public double TotalAudioProcessedMs { get; private set; }
    public IReadOnlyList<string> LastMessageFields { get; private set; } = Array.Empty<string>();

    internal RealtimeSession(WebSocket socket, Action<IReadOnlyList<TranscriptSegment>, string> onTokens, string? remoteId, string apiKind = "custom", string? temporaryKey = null)
    {
        _socket = socket; _onTokens = onTokens; _remoteId = string.IsNullOrWhiteSpace(remoteId) ? null : remoteId; _apiKind = apiKind; _temporaryKey = temporaryKey;
        _lifetimeToken = _lifetime.Token;
        _receiver = ReceiveAsync(); _keepAlive = KeepAliveAsync();
    }

    public async Task SendAudioAsync(byte[] pcm, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        await _audioLock.WaitAsync(linked.Token);
        try
        {
            if (_finishing || _disposed) return;
            if (_finished.Task.IsFaulted) await _finished.Task;
            // Connection setup may have buffered capture frames. Drain at speech speed.
            if (!_audioClock.IsRunning) _audioClock.Start();
            var delay = _sentAudioBytes * 1000.0 / 32000 - _audioClock.Elapsed.TotalMilliseconds;
            if (delay > 1) await Task.Delay(TimeSpan.FromMilliseconds(delay), linked.Token);
            await SendAsync(pcm, WebSocketMessageType.Binary, linked.Token);
            _sentAudioBytes += pcm.Length;
        }
        finally { _audioLock.Release(); }
    }

    public Task<TranscriptRecord> FinishAsync(CancellationToken ct)
    {
        lock (_finishGate) { _finishTask ??= FinishCoreAsync(ct); return _finishTask; }
    }
    private async Task<TranscriptRecord> FinishCoreAsync(CancellationToken ct)
    {
        _finishing = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        await _audioLock.WaitAsync(linked.Token);
        try
        {
            if (_finished.Task.IsCompleted) return await _finished.Task;
            await SendAsync(Array.Empty<byte>(), WebSocketMessageType.Binary, linked.Token);
        }
        finally { _audioLock.Release(); }
        // The documented custom gateway accepts empty binary. Native Soniox uses an
        // empty text message; some gateway proxies discard zero-length binary frames.
        if (_apiKind == "soniox")
        {
            await Task.WhenAny(_finished.Task, Task.Delay(1000, linked.Token));
            linked.Token.ThrowIfCancellationRequested();
            if (!_finished.Task.IsCompleted) await SendAsync(Array.Empty<byte>(), WebSocketMessageType.Text, linked.Token);
        }
        try { return await _finished.Task.WaitAsync(TimeSpan.FromSeconds(50), linked.Token); }
        catch (TimeoutException) { throw new ServiceException("تکمیل متن طول کشید. اتصال را بررسی کن و دوباره امتحان کن."); }
    }

    private async Task SendAsync(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        linked.CancelAfter(TimeSpan.FromSeconds(8));
        bool entered=false;
        try
        {
            await _sendLock.WaitAsync(linked.Token); entered=true;
            if (_socket.State != WebSocketState.Open) throw new ServiceException("اتصال متن زنده قطع شد. اتصال اینترنت را بررسی کن.");
            await _socket.SendAsync(new ArraySegment<byte>(bytes), type, true, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetimeToken.IsCancellationRequested)
        {
            _socket.Abort();
            var error = new ServiceException("فرستادن صدا طول کشید. اتصال را بررسی کن و دوباره امتحان کن.");
            _finished.TrySetException(error); throw error;
        }
        finally { if (entered) _sendLock.Release(); }
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            while (!_lifetimeToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(7), _lifetimeToken);
                if (!_finishing) await SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"keepalive\"}"), WebSocketMessageType.Text, _lifetimeToken);
            }
        }
        catch (OperationCanceledException) { }
        catch { _finished.TrySetException(new ServiceException("اتصال متن زنده قطع شد. اتصال اینترنت را بررسی کن.")); }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_lifetimeToken.IsCancellationRequested)
            {
                using var message = new System.IO.MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetimeToken);
                    if (received.MessageType == WebSocketMessageType.Close) throw new ServiceException("اتصال پیش از تکمیل متن قطع شد. دوباره امتحان کن.");
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 8 * 1024 * 1024) throw new ServiceException("متن دریافتی بیش از حد طولانی بود. ضبط کوتاه‌تری را امتحان کن.");
                } while (!received.EndOfMessage);
                if (received.MessageType != WebSocketMessageType.Text) continue;
                using var json = JsonDocument.Parse(message.ToArray());
                var root = TranscriptionClient.Unwrap(json.RootElement);
                MessagesReceived++;
                LastMessageFields = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(x => x.Name).ToArray() : Array.Empty<string>();
                FinalAudioProcessedMs = Math.Max(FinalAudioProcessedMs, TranscriptionClient.Number(root, "final_audio_proc_ms"));
                TotalAudioProcessedMs = Math.Max(TotalAudioProcessedMs, TranscriptionClient.Number(root, "total_audio_proc_ms"));
                if (TryReadError(root, out var errorMessage, _temporaryKey)) throw new ServiceException(errorMessage);
                var id = TranscriptionClient.Value(root, "transcription_id");
                if (!string.IsNullOrWhiteSpace(id)) _remoteId = id;
                var interim = new StringBuilder();
                if (TranscriptionClient.TryProperty(root, "tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
                    foreach (var item in tokens.EnumerateArray())
                    {
                        var text = TranscriptionClient.Value(item, "text", "word");
                        if (text.Length == 0 || text is "<end>" or "<fin>") continue;
                        var isFinal = TranscriptionClient.TryProperty(item, "is_final", out var final) && final.ValueKind == JsonValueKind.True;
                        if (isFinal)
                        {
                            var segment = new TranscriptSegment { Text = text, StartMs = TranscriptionClient.TimeMs(item, true), EndMs = TranscriptionClient.TimeMs(item, false), Speaker = TranscriptionClient.Value(item, "speaker", "speaker_id"), Language = TranscriptionClient.Value(item, "language", "language_code") };
                            // Soniox sends each final token once. Identical tokens may be legitimate
                            // repeated words or spaces without individual alignment; preserve them.
                            _finalTokens.Add(segment);
                        }
                        else interim.Append(text);
                    }
                var snapshot = _finalTokens.Select(Clone).ToArray();
                try { _onTokens(snapshot, interim.ToString()); } catch { /* UI callbacks cannot terminate transport. */ }
                if (TranscriptionClient.TryProperty(root, "finished", out var finished) && finished.ValueKind == JsonValueKind.True)
                {
                    _finished.TrySetResult(new TranscriptRecord { RemoteId = _remoteId, ApiKind = _apiKind, Segments = snapshot.ToList(), Text = string.Concat(snapshot.Select(x => x.Text)).Trim(), DurationMs = Math.Max(TotalAudioProcessedMs,snapshot.Length > 0 ? snapshot.Max(x => x.EndMs) : 0) });
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { _finished.TrySetCanceled(_lifetimeToken); }
        catch (ServiceException ex) { _finished.TrySetException(ex); }
        catch { _finished.TrySetException(new ServiceException("دریافت متن زنده کامل نشد. دوباره امتحان کن.")); }
    }
    private static TranscriptSegment Clone(TranscriptSegment x) => new() { Text = x.Text, StartMs = x.StartMs, EndMs = x.EndMs, Speaker = x.Speaker, Language = x.Language };

    public static bool TryReadError(JsonElement root, out string message, string? temporaryKey = null)
    {
        var code = TranscriptionClient.Value(root, "error_code");
        var kind = TranscriptionClient.Value(root, "error_type");
        bool hasCode = code.Length > 0 && code != "0";
        bool hasError = TranscriptionClient.TryProperty(root, "error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined) && error.ToString().Length > 0 && error.ToString() != "0";
        if (!hasCode && !hasError) { message = ""; return false; }
        var details = TranscriptionClient.Value(root, "error_message", "message");
        if (details.Length == 0 && hasError && error.ValueKind == JsonValueKind.String) details = error.GetString() ?? "";
        var raw = string.Join(" • ", new[] { code, kind, details }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (!string.IsNullOrEmpty(temporaryKey)) raw = raw.Replace(temporaryKey, "[کلید حذف شد]", StringComparison.Ordinal);
        raw = Regex.Replace(raw, @"(?i)Bearer\s+\S+", "Bearer [redacted]");
        raw = Regex.Replace(raw, @"[A-Za-z0-9_+./:=~\-]{24,}", "[redacted]");
        raw = Regex.Replace(raw, @"[\r\n\t]+", " ");
        if (raw.Length > 320) raw = raw[..320] + "…";
        message = "دریافت متن زنده کامل نشد. تنظیمات اتصال را بررسی کن.\nجزئیات: " + raw;
        return true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { _disposeTask ??= DisposeCoreAsync(); return new ValueTask(_disposeTask); }
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        _socket.Abort();
        try { await Task.WhenAll(_receiver, _keepAlive).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _socket.Dispose(); _lifetime.Dispose();
        // A canceled sender may still be unwinding its finally/Release. Managed
        // semaphores have no OS handle here and must not be disposed under it.
    }
}
