using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nava.Core;

namespace Nava.Services;

public sealed class ServiceException : Exception
{
    public string? RemoteId { get; set; }
    public int? StatusCode { get; }
    internal bool NativeValidation { get; set; }
    public ServiceException(string message, int? statusCode = null, string? remoteId = null) : base(message) { StatusCode = statusCode; RemoteId = remoteId; }
}

public sealed record RealtimeDiagnostics(string KeyFormat, int KeyLength, string KeyFingerprint, string ExpiresAtUtc, double? SecondsUntilExpiry, string WebsocketHost);

/// <summary>Transport is explicitly scoped to this service. No machine proxy settings are read or changed.</summary>
public sealed class TranscriptionClient
{
    private readonly AppSettings _settings;
    private readonly Uri _baseUri;
    private static readonly ConcurrentDictionary<string, bool> NativeServices = new();
    private static readonly ConcurrentDictionary<string, string> Models = new();
    public string ApiKind => NativeServices.ContainsKey(_baseUri.AbsoluteUri) ? "soniox" : "custom";
    public RealtimeDiagnostics? LastRealtimeDiagnostics { get; private set; }
    public TranscriptionClient(AppSettings settings)
    {
        _settings = settings;
        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ServiceException("آدرس سرویس معتبر نیست. آن را در تنظیمات اتصال بررسی کن.");
        _baseUri = uri;
    }

    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        var result = await JsonAsync(HttpMethod.Get, "healthz", null, ct);
        return Value(result, "status").Equals("ok", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TranscriptRecord> TranscribeFileAsync(string path, string name, IProgress<string>? progress, CancellationToken ct, string? existingRemoteId = null, Action<string>? onRemoteCreated = null)
    {
        if (ApiKind == "soniox") return await NativeTranscribeFileAsync(path, name, progress, ct, existingRemoteId, onRemoteCreated);
        string? id = existingRemoteId;
        try
        {
            if (!File.Exists(path)) throw new ServiceException("فایل صدا پیدا نشد. فایل را دوباره انتخاب کن.");
            progress?.Report("در حال آماده‌سازی فایل…");
            if (!string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    var existingStatus = Unwrap(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}/async-status", null, ct));
                    var state = Value(existingStatus, "status", "state").ToLowerInvariant();
                    if (IsCompleted(state)) return await FetchResultAsync(id, name, path, ct);
                    if (state is "processing" or "transcribing" or "queued" or "running") return await WaitForResultAsync(id, name, path, progress, ct);
                }
                catch (ServiceException ex) when (ex.StatusCode == 404) { id = null; }
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                var created = await JsonAsync(HttpMethod.Post, "v1/transcriptions", new
                {
                    name,
                    transcription_type = "transcription",
                    parameters = new { language_hints = new[] { "fa", "en" }, enable_speaker_diarization = _settings.Diarization, enable_language_identification = true }
                }, ct);
                id = Value(Unwrap(created), "id", "transcription_id");
            }
            if (string.IsNullOrWhiteSpace(id)) throw new ServiceException("پاسخ سرویس برای ادامهٔ تبدیل کافی نیست. دوباره امتحان کن.");
            onRemoteCreated?.Invoke(id);
            var mime = Mime(path);
            var upload = Unwrap(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}/upload-url?content_type={Uri.EscapeDataString(mime)}", null, ct));
            var objectKey = Value(upload, "object_key", "objectKey", "key");
            var urlText = Value(upload, "url", "upload_url", "uploadUrl");
            if (TryProperty(upload, "upload", out var inner)) { if (urlText.Length == 0) urlText = Value(inner, "url", "upload_url"); if (objectKey.Length == 0) objectKey = Value(inner, "object_key", "key"); upload = inner; }
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uploadUri) || (uploadUri.Scheme != "http" && uploadUri.Scheme != "https") || !string.IsNullOrEmpty(uploadUri.UserInfo))
                throw new ServiceException("سرویس برای دریافت فایل آماده نیست. کمی بعد دوباره امتحان کن.");
            var fields = TryProperty(upload, "fields", out var f) ? f : default;
            if (objectKey.Length == 0 && fields.ValueKind == JsonValueKind.Object) objectKey = Value(fields, "key", "object_key");
            if (objectKey.Length == 0) throw new ServiceException("آماده‌سازی فایل در سرویس کامل نشد. دوباره امتحان کن.");
            progress?.Report("در حال فرستادن فایل…");
            await UploadAsync(uploadUri, fields, path, mime, ct);
            await JsonAsync(HttpMethod.Post, $"v1/transcriptions/{Uri.EscapeDataString(id)}/async-transcription-bucket", new { object_key = objectKey }, ct);
            return await WaitForResultAsync(id, name, path, progress, ct);
        }
        catch (ServiceException ex) when (ex.NativeValidation)
        {
            NativeServices[_baseUri.AbsoluteUri] = true;
            return await NativeTranscribeFileAsync(path, name, progress, ct, existingRemoteId, onRemoteCreated);
        }
        catch (ServiceException ex) { ex.RemoteId ??= id; throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new ServiceException("تبدیل فایل کامل نشد. دوباره امتحان کن.", remoteId: id); }
    }

    private async Task<TranscriptRecord> NativeTranscribeFileAsync(string path, string name, IProgress<string>? progress, CancellationToken ct, string? existingId, Action<string>? onRemoteCreated)
    {
        string? id = existingId;
        try
        {
            if (!File.Exists(path)) throw new ServiceException("فایل صدا پیدا نشد. فایل را دوباره انتخاب کن.");
            if (!string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    var metadata = Unwrap(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}", null, ct));
                    var status = Value(metadata, "status").ToLowerInvariant();
                    if (IsCompleted(status)) return await NativeFetchResultAsync(id, name, path, metadata, ct);
                    if (status is "queued" or "processing" or "running") return await NativeWaitAsync(id, name, path, progress, ct);
                    id = null;
                }
                catch (ServiceException ex) when (ex.StatusCode == 404) { id = null; }
            }
            progress?.Report("در حال آماده‌سازی فایل…");
            var model = await SelectModelAsync(false, ct);
            progress?.Report("در حال فرستادن فایل…");
            var fileUri = new Uri(_baseUri, "v1/files");
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, fileUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
                var multipart = new MultipartFormDataContent();
                var stream = new StreamContent(File.OpenRead(path));
                stream.Headers.ContentType = new MediaTypeHeaderValue(Mime(path));
                multipart.Add(stream, "file", Path.GetFileName(path));
                request.Content = multipart;
                return request;
            }, fileUri, TimeSpan.FromMinutes(20), ct);
            if (!response.IsSuccessStatusCode) throw StatusError((int)response.StatusCode);
            using var fileDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var fileId = Value(Unwrap(fileDoc.RootElement), "id", "file_id");
            if (string.IsNullOrWhiteSpace(fileId)) throw new ServiceException("پاسخ سرویس برای ادامهٔ تبدیل کافی نیست. دوباره امتحان کن.");
            var created = Unwrap(await JsonAsync(HttpMethod.Post, "v1/transcriptions", new
            {
                model,
                file_id = fileId,
                language_hints = new[] { "fa", "en" },
                enable_speaker_diarization = _settings.Diarization,
                enable_language_identification = true
            }, ct));
            id = Value(created, "id", "transcription_id");
            if (string.IsNullOrWhiteSpace(id)) throw new ServiceException("پاسخ سرویس برای ادامهٔ تبدیل کافی نیست. دوباره امتحان کن.");
            onRemoteCreated?.Invoke(id);
            return await NativeWaitAsync(id, name, path, progress, ct);
        }
        catch (ServiceException ex) { ex.RemoteId ??= id; throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new ServiceException("تبدیل فایل کامل نشد. دوباره امتحان کن.", remoteId: id); }
    }

    private async Task<TranscriptRecord> NativeWaitAsync(string id, string name, string path, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("در حال تبدیل صدا به متن…");
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            var metadata = Unwrap(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}", null, ct));
            var status = Value(metadata, "status").ToLowerInvariant();
            if (IsCompleted(status)) return await NativeFetchResultAsync(id, name, path, metadata, ct);
            if (status is "error" or "failed" or "canceled" or "cancelled") throw new ServiceException("سرویس تبدیل فایل را کامل نکرد. دوباره امتحان کن.", remoteId: id);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new ServiceException("تبدیل فایل هنوز کامل نشده است. کمی بعد دوباره امتحان کن.", remoteId: id);
    }

    private async Task<TranscriptRecord> NativeFetchResultAsync(string id, string name, string path, JsonElement metadata, CancellationToken ct)
    {
        var result = NormalizeTranscript(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}/transcript", null, ct));
        result.RemoteId = id; result.Title = name; result.AudioPath = path; result.ApiKind = "soniox";
        var duration = Number(metadata, "audio_duration_ms", "duration_ms");
        if (duration > 0) result.DurationMs = duration;
        if (string.IsNullOrWhiteSpace(result.Text)) throw new ServiceException("در این فایل گفتاری تشخیص داده نشد. صدای فایل را بررسی کن.", remoteId: id);
        return result;
    }

    private async Task<string> SelectModelAsync(bool realtime, CancellationToken ct)
    {
        var prefix = realtime ? "stt-rt-v" : "stt-async-v";
        var cacheKey = _baseUri.AbsoluteUri + "|" + prefix;
        if (Models.TryGetValue(cacheKey, out var known)) return known;
        var models = Unwrap(await JsonAsync(HttpMethod.Get, "v1/models", null, ct));
        var array = TryProperty(models, "models", out var list) ? list : models;
        var eligible = array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(x => Value(x, "id", "model")).Where(x => x.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(x[prefix.Length..], out _)).ToArray() : Array.Empty<string>();
        var selected = eligible.Contains(prefix + "5") ? prefix + "5" : eligible.OrderByDescending(x => int.Parse(x[prefix.Length..], CultureInfo.InvariantCulture)).FirstOrDefault();
        if (selected == null) throw new ServiceException("سرویس برای تبدیل صدا آماده نیست. تنظیمات اتصال را بررسی کن.");
        Models[cacheKey] = selected;
        return selected;
    }

    private static bool IsCompleted(string state) => state is "completed" or "complete" or "done" or "succeeded" or "success";
    private async Task<TranscriptRecord> WaitForResultAsync(string id, string name, string path, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("در حال تبدیل صدا به متن…");
        for (int attempt = 0; attempt < 2400; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var statusDoc = Unwrap(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}/async-status", null, ct));
            var status = Value(statusDoc, "status", "state").ToLowerInvariant();
            if (IsCompleted(status)) return await FetchResultAsync(id, name, path, ct);
            if (status is "failed" or "error" or "cancelled" or "canceled") throw new ServiceException("سرویس تبدیل فایل را کامل نکرد. دوباره امتحان کن.", remoteId: id);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new ServiceException("تبدیل فایل هنوز کامل نشده است. کمی بعد دوباره امتحان کن.", remoteId: id);
    }
    private async Task<TranscriptRecord> FetchResultAsync(string id, string name, string path, CancellationToken ct)
    {
        var record = NormalizeTranscript(await JsonAsync(HttpMethod.Get, $"v1/transcriptions/{Uri.EscapeDataString(id)}", null, ct));
        record.RemoteId = id; record.Title = name; record.AudioPath = path;
        if (string.IsNullOrWhiteSpace(record.Text)) throw new ServiceException("در این فایل گفتاری تشخیص داده نشد. صدای فایل را بررسی کن.", remoteId: id);
        return record;
    }

    public async Task<string> AnalyzeAsync(string remoteId, string action, string? customPrompt, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?> { ["transcription_id"] = remoteId, ["action"] = action, ["target_language"] = "fa" };
        if (action == "custom") payload["custom_prompt"] = customPrompt ?? "";
        var result = Unwrap(await JsonAsync(HttpMethod.Post, "v1/analysis", payload, ct));
        return ExtractText(result, "result", "analysis", "text", "content", "summary", "output");
    }

    public async Task NameSpeakersAsync(string remoteId, Dictionary<string, string> names, CancellationToken ct)
        => _ = await JsonAsync(HttpMethod.Post, $"v1/transcriptions/{Uri.EscapeDataString(remoteId)}/name-speakers", new { speaker_names = names }, ct);

    public async Task<string> SmartFormatAsync(string text, double durationMs, CancellationToken ct)
    {
        var result = Unwrap(await JsonAsync(HttpMethod.Post, "v1/text/smart-format", new { text, duration_ms = Math.Max(1, (long)durationMs), languages = new[] { "fa", "en" } }, ct));
        return ExtractText(result, "formatted_text", "text", "result", "content");
    }

    public async Task<RealtimeSession> StartRealtimeAsync(Action<IReadOnlyList<TranscriptSegment>, string> onTokens, CancellationToken ct)
    {
        LastRealtimeDiagnostics = null;
        JsonElement session;
        bool native = ApiKind == "soniox";
        if (!native)
        {
            try { session = Unwrap(await JsonAsync(HttpMethod.Post, "v1/stt/session", new { }, ct)); }
            catch (ServiceException ex) when (ex.StatusCode == 404) { native = true; session = default; }
        }
        else session = default;
        var model = native ? await SelectModelAsync(true, ct) : "stt-rt-v5";
        if (native) NativeServices[_baseUri.AbsoluteUri] = true;
        // This gateway authenticates native streams with the connection header.
        // Stay on its origin; its credentials are never sent to an external provider.
        var nativeWebsocket = new UriBuilder(_baseUri) { Scheme = _baseUri.Scheme == "https" ? "wss" : "ws", Path = _baseUri.AbsolutePath.TrimEnd('/') + "/transcribe-websocket", Query = "", Fragment = "" }.Uri;
        var url = native ? nativeWebsocket.AbsoluteUri : Value(session, "websocket_url", "websocketUrl", "url");
        var apiKey = native ? "" : Value(session, "api_key", "temporary_api_key", "token", "key");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var wsUri) || (wsUri.Scheme != "ws" && wsUri.Scheme != "wss") || !string.IsNullOrEmpty(wsUri.UserInfo) || (!native && apiKey.Length == 0))
            throw new ServiceException("آماده‌سازی متن زنده انجام نشد. تنظیمات اتصال را بررسی کن.");
        var hasExpiry = DateTimeOffset.TryParse(Value(session, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry);
        LastRealtimeDiagnostics = native
            ? new RealtimeDiagnostics("gateway_header", 0, "", "not_applicable", null, wsUri.Host)
            : new RealtimeDiagnostics(apiKey.StartsWith("snx_temp_", StringComparison.Ordinal) ? "snx_temp_" : apiKey.StartsWith("temp:", StringComparison.Ordinal) ? "temp:" : "other", apiKey.Length, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..12].ToLowerInvariant(), hasExpiry ? expiry.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : "unavailable", hasExpiry ? (expiry - DateTimeOffset.UtcNow).TotalSeconds : null, wsUri.Host);
        var websocketAuthorization = SameService(wsUri) ? _settings.ApiKey : null;
        ClientWebSocket socket = NewSocket(false, websocketAuthorization);
        try
        {
            try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12)); await socket.ConnectAsync(wsUri, timeout.Token); }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsConnectionFailure(ex) && !string.IsNullOrWhiteSpace(_settings.ProxyUrl))
            {
                socket.Dispose(); socket = NewSocket(true, websocketAuthorization);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15)); await socket.ConnectAsync(wsUri, timeout.Token);
            }
            var config = new Dictionary<string, object> { ["model"] = model, ["audio_format"] = "pcm_s16le", ["sample_rate"] = 16000, ["num_channels"] = 1, ["language_hints"] = new[] { "fa", "en" }, ["enable_speaker_diarization"] = _settings.Diarization, ["enable_language_identification"] = true };
            // The native gateway authenticates the connection and translates its bearer.
            // Its temporary token must not replace the already-authenticated connection key.
            if (!native) config["api_key"] = apiKey;
            if (native) config["enable_endpoint_detection"] = true;
            var init = JsonSerializer.SerializeToUtf8Bytes(config);
            await socket.SendAsync(init, WebSocketMessageType.Text, true, ct);
            return new RealtimeSession(socket, onTokens, Value(session, "transcription_id"), native ? "soniox" : "custom", native ? _settings.ApiKey : apiKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { socket.Dispose(); throw; }
        catch { socket.Dispose(); throw new ServiceException("اتصال متن زنده برقرار نشد. تنظیمات اتصال را بررسی کن."); }
    }

    private ClientWebSocket NewSocket(bool useProxy, string? authorizationKey = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = useProxy ? CreateProxy() : null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrEmpty(authorizationKey)) socket.Options.SetRequestHeader("Authorization", "Bearer " + authorizationKey);
        return socket;
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string relative, object? body, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, relative);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }, uri, TimeSpan.FromSeconds(70), ct);
        if (!response.IsSuccessStatusCode)
        {
            var failure = StatusError((int)response.StatusCode);
            if ((int)response.StatusCode is 400 or 422 && method == HttpMethod.Post && relative == "v1/transcriptions")
            {
                var errorText = await response.Content.ReadAsStringAsync(ct);
                failure.NativeValidation = errorText.Contains("model", StringComparison.OrdinalIgnoreCase) && (errorText.Contains("file_id", StringComparison.OrdinalIgnoreCase) || errorText.Contains("audio_url", StringComparison.OrdinalIgnoreCase));
            }
            throw failure;
        }
        try { var bodyText = await response.Content.ReadAsStringAsync(ct); using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(bodyText) ? "{}" : bodyText); return json.RootElement.Clone(); }
        catch (JsonException) { throw new ServiceException("پاسخ سرویس خوانده نشد. دوباره امتحان کن."); }
    }

    private async Task UploadAsync(Uri uri, JsonElement fields, string path, string mime, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var multipart = new MultipartFormDataContent();
            if (fields.ValueKind == JsonValueKind.Object)
                foreach (var field in fields.EnumerateObject()) multipart.Add(new StringContent(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() ?? "" : field.Value.ToString()), field.Name);
            var stream = new StreamContent(File.OpenRead(path));
            stream.Headers.ContentType = new MediaTypeHeaderValue(mime);
            multipart.Add(stream, "file", Path.GetFileName(path));
            request.Content = multipart;
            // Presigned object storage receives only its own multipart credentials, never the service bearer.
            return request;
        }, uri, TimeSpan.FromMinutes(20), ct, serviceResource: true);
        if (!response.IsSuccessStatusCode) throw new ServiceException("فرستادن فایل کامل نشد. اتصال را بررسی کن و دوباره امتحان کن.", (int)response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, Uri uri, TimeSpan timeout, CancellationToken ct, bool serviceResource = false)
    {
        try { return await AttemptAsync(factory, false, timeout, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsConnectionFailure(ex) && (SameService(uri) || serviceResource) && !string.IsNullOrWhiteSpace(_settings.ProxyUrl))
        {
            try { return await AttemptAsync(factory, true, timeout, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw new ServiceException("اتصال به سرویس برقرار نشد. تنظیمات اتصال را بررسی کن."); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsConnectionFailure(ex)) { throw new ServiceException("اتصال به سرویس برقرار نشد. اینترنت و آدرس سرویس را بررسی کن."); }
    }

    private async Task<HttpResponseMessage> AttemptAsync(Func<HttpRequestMessage> factory, bool useProxy, TimeSpan timeout, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { UseProxy = useProxy, Proxy = useProxy ? CreateProxy() : null, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) };
        using var client = new HttpClient(handler) { Timeout = timeout };
        using var request = factory();
        return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    private IWebProxy CreateProxy()
    {
        if (!Uri.TryCreate(_settings.ProxyUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) throw new ServiceException("آدرس پروکسی معتبر نیست. آن را در تنظیمات اتصال بررسی کن.");
        var proxy = new WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return proxy;
    }
    private bool SameService(Uri uri) => string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == _baseUri.Port;
    private static bool IsConnectionFailure(Exception ex) => ex is HttpRequestException or WebSocketException or OperationCanceledException or IOException;
    private static ServiceException StatusError(int code) => new(code switch
    {
        401 => "کلید دسترسی پذیرفته نشد. آن را در تنظیمات اتصال بررسی کن.",
        403 => "کلید دسترسی مجوز لازم را ندارد. دسترسی‌های آن را بررسی کن.",
        402 => "اعتبار یا سقف مصرف سرویس کافی نیست. حساب سرویس را بررسی کن.",
        400 or 422 => "سرویس درخواست را نپذیرفت. اطلاعات واردشده را بررسی کن.",
        404 => "این مورد در سرویس پیدا نشد. دوباره امتحان کن.",
        429 => "سرویس شلوغ است. کمی بعد دوباره امتحان کن.",
        502 or 503 or 504 => "سرویس موقتاً در دسترس نیست. کمی بعد دوباره امتحان کن.",
        _ => "درخواست کامل نشد. کمی بعد دوباره امتحان کن."
    }, code);

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" or ".mp4" => "audio/mp4", ".ogg" or ".oga" or ".opus" => "audio/ogg", ".webm" => "audio/webm", ".flac" => "audio/flac", ".aac" => "audio/aac", _ => "application/octet-stream" };

    public static TranscriptRecord NormalizeTranscript(JsonElement document)
    {
        var root = Unwrap(document);
        var record = new TranscriptRecord { RemoteId = EmptyToNull(Value(root, "id", "transcription_id")), Title = Value(root, "name", "title") };
        if (record.Title.Length == 0) record.Title = "یادداشت صوتی";
        var source = root;
        foreach (var name in new[] { "transcription", "result", "transcript" })
            if (TryProperty(root, name, out var nested) && nested.ValueKind == JsonValueKind.Object) { source = nested; break; }
        record.Text = Value(source, "text", "transcript", "full_text", "transcription_text", "content");
        JsonElement segments = default;
        string segmentType = "";
        foreach (var name in new[] { "segments", "utterances", "tokens", "words" })
            if (TryProperty(source, name, out var array) && array.ValueKind == JsonValueKind.Array) { segments = array; segmentType = name; break; }
        if (segments.ValueKind == JsonValueKind.Undefined && !source.Equals(root))
            foreach (var name in new[] { "segments", "utterances", "tokens", "words" })
                if (TryProperty(root, name, out var array) && array.ValueKind == JsonValueKind.Array) { segments = array; segmentType = name; break; }
        if (segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segments.EnumerateArray())
            {
                if (TryProperty(item, "is_final", out var finalToken) && finalToken.ValueKind == JsonValueKind.False) continue;
                var text = Value(item, "text", "word", "content");
                if (text.Length == 0 || text is "<end>" or "<fin>") continue;
                record.Segments.Add(new TranscriptSegment { Text = text, StartMs = TimeMs(item, true), EndMs = TimeMs(item, false), Speaker = Value(item, "speaker", "speaker_id", "speaker_label"), Language = Value(item, "language", "language_code") });
            }
            if (record.Text.Length == 0) record.Text = segmentType is "tokens" or "words" ? string.Concat(record.Segments.Select(x => x.Text)) : string.Join(Environment.NewLine, record.Segments.Select(x => x.Text));
        }
        record.DurationMs = Number(source, "duration_ms", "audio_duration_ms");
        if (record.DurationMs == 0) record.DurationMs = Number(root, "duration_ms", "audio_duration_ms");
        if (record.DurationMs == 0) record.DurationMs = Number(source, "duration", "duration_seconds", "audio_duration") * 1000;
        if (record.DurationMs == 0) record.DurationMs = Number(root, "duration", "duration_seconds", "audio_duration") * 1000;
        if (record.DurationMs == 0 && record.Segments.Count > 0) record.DurationMs = record.Segments.Max(x => x.EndMs);
        if (TryProperty(root, "speaker_names", out var speakers) && speakers.ValueKind == JsonValueKind.Object)
            foreach (var p in speakers.EnumerateObject()) record.SpeakerNames[p.Name] = p.Value.ToString();
        record.Text = record.Text.Trim();
        return record;
    }
    internal static double TimeMs(JsonElement item, bool start)
    {
        var ms = start ? "start_ms" : "end_ms";
        if (TryProperty(item, ms, out _)) return Math.Max(0, Number(item, ms));
        return Math.Max(0, Number(item, start ? "start" : "end", start ? "start_time" : "end_time") * 1000);
    }
    internal static JsonElement Unwrap(JsonElement root) => TryProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
    internal static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(name, out value)) return true;
            foreach (var prop in root.EnumerateObject()) if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = prop.Value; return true; }
        }
        value = default; return false;
    }
    internal static string Value(JsonElement root, params string[] names)
    {
        foreach (var name in names) if (TryProperty(root, name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        return root.ValueKind == JsonValueKind.String ? root.GetString() ?? "" : "";
    }
    internal static double Number(JsonElement root, params string[] names) => double.TryParse(Value(root, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : 0;
    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static string ExtractText(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? "";
        foreach (var name in names)
            if (TryProperty(root, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
                if (value.ValueKind == JsonValueKind.Array) return string.Join(Environment.NewLine, value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()));
                if (value.ValueKind == JsonValueKind.Object) { var text = ExtractText(value, "text", "content", "result", "summary", "output"); return text.Length > 0 ? text : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }); }
            }
        return root.ValueKind == JsonValueKind.Object || root.ValueKind == JsonValueKind.Array ? JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }) : "";
    }
}
