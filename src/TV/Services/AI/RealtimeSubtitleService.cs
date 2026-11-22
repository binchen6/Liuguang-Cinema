using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TV.Services.AI;

/// <summary>
/// Streams playback audio via WASAPI Process Loopback to DashScope qwen3-asr-flash-realtime WebSocket API
/// for low-latency live subtitles (with optional live translation via qwen-mt-flash / qwen-mt-plus).
/// </summary>
public sealed class RealtimeSubtitleService : IDisposable
{
    private readonly BailianSubtitleService _bailian;
    private readonly List<SubtitleCue> _recorded = [];
    private readonly object _recordSync = new();
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private ProcessAudioLoopbackCapture? _capture;
    private Task? _runner;
    private Func<long>? _getMediaTimeMs;
    private long _currentTurnStartMs;

    public RealtimeSubtitleService(BailianSubtitleService bailian)
    {
        _bailian = bailian;
    }

    public bool IsRunning => _runner is { IsCompleted: false };
    public event EventHandler<LiveCaptionUpdate>? CaptionUpdated;
    public event EventHandler<string>? StatusChanged;

    public IReadOnlyList<SubtitleCue> GetRecordedCues()
    {
        lock (_recordSync) return _recorded.ToArray();
    }

    public async Task StartAsync(Func<long> getMediaTimeMs, CancellationToken externalToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        var config = await _bailian.GetConfigAsync(externalToken).ConfigureAwait(false);
        if (!config.HasApiKey)
            throw new InvalidOperationException("尚未配置阿里云百炼 API Key，请前往“设置 → 弹幕与 AI”填入密钥。");

        lock (_recordSync) _recorded.Clear();
        _getMediaTimeMs = getMediaTimeMs;
        _currentTurnStartMs = Math.Max(0, getMediaTimeMs());

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _cts.Token;

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + config.ApiKey);
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        var endpoint = new Uri($"{config.WsBaseUrl}/api-ws/v1/realtime?model={AiSubtitleModels.Qwen3AsrFlashRealtime}");

        StatusChanged?.Invoke(this, "正在连接百炼实时字幕服务…");
        await ws.ConnectAsync(endpoint, token).ConfigureAwait(false);
        _socket = ws;

        // Configure Server VAD & PCM16 input format
        var sessionUpdate = new Dictionary<string, object>
        {
            ["type"] = "session.update",
            ["session"] = BuildSessionPayload(config)
        };
        await SendJsonAsync(ws, JsonSerializer.Serialize(sessionUpdate), token).ConfigureAwait(false);

        var audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _capture = new ProcessAudioLoopbackCapture(chunk => audioChannel.Writer.TryWrite(chunk));
        _capture.Start();

        StatusChanged?.Invoke(this, config.RealtimeTranslate ? "实时字幕 + 实时翻译已开启" : "实时字幕已开启（qwen3-asr-flash-realtime）");
        _runner = Task.WhenAll(SendAudioLoopAsync(ws, audioChannel.Reader, token), ReceiveLoopAsync(ws, config, token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        var capture = _capture;
        _capture = null;
        capture?.Dispose();

        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None)
                        .WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch { }
            socket.Dispose();
        }

        if (_runner is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false); }
            catch { }
            _runner = null;
        }
        cts?.Dispose();
    }

    private static Dictionary<string, object> BuildSessionPayload(AiSubtitleConfig config)
    {
        var transcription = new Dictionary<string, object>
        {
            ["model"] = AiSubtitleModels.Qwen3AsrFlashRealtime
        };
        if (!string.Equals(config.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(config.SourceLanguage))
            transcription["language"] = config.SourceLanguage;

        return new Dictionary<string, object>
        {
            ["modalities"] = new[] { "text" },
            ["input_audio_format"] = "pcm16",
            ["sample_rate"] = 16000,
            ["input_audio_transcription"] = transcription,
            ["turn_detection"] = new Dictionary<string, object>
            {
                ["type"] = "server_vad",
                ["threshold"] = 0.4,
                ["silence_duration_ms"] = 450
            }
        };
    }

    private static async Task SendAudioLoopAsync(ClientWebSocket ws, ChannelReader<byte[]> reader, CancellationToken token)
    {
        try
        {
            await foreach (var pcm in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;
                var json = JsonSerializer.Serialize(new
                {
                    type = "input_audio_buffer.append",
                    audio = Convert.ToBase64String(pcm)
                });
                await SendJsonAsync(ws, json, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Log($"Realtime ASR send loop ended: {ex.GetType().Name}"); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, AiSubtitleConfig config, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || message.Length == 0) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                await HandleServerEventAsync(text, config, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Diagnostics.Log($"Realtime ASR receive loop ended: {ex.GetType().Name} {ex.Message}");
            StatusChanged?.Invoke(this, "实时字幕连接已断开：" + ex.Message);
        }
    }

    private async Task HandleServerEventAsync(string json, AiSubtitleConfig config, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;

            if (type == "input_audio_buffer.speech_started")
            {
                _currentTurnStartMs = Math.Max(0, _getMediaTimeMs?.Invoke() ?? 0);
                return;
            }

            if (type is "conversation.item.input_audio_transcription.text" or "conversation.item.input_audio_transcription.delta")
            {
                var partial = ReadTextField(root, "text", "stash", "delta");
                if (!string.IsNullOrWhiteSpace(partial))
                    CaptionUpdated?.Invoke(this, new LiveCaptionUpdate(partial, null, IsFinal: false));
                return;
            }

            if (type == "conversation.item.input_audio_transcription.completed")
            {
                var final = ReadTextField(root, "transcript", "text");
                if (string.IsNullOrWhiteSpace(final)) return;

                var endMs = Math.Max(_currentTurnStartMs + 600, _getMediaTimeMs?.Invoke() ?? (_currentTurnStartMs + 2000));
                string? translated = null;
                if (config.RealtimeTranslate)
                {
                    // Emit original immediately, then update with translated line
                    CaptionUpdated?.Invoke(this, new LiveCaptionUpdate(final, null, IsFinal: true));
                    try { translated = await _bailian.TranslateSingleLineAsync(final, config, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Diagnostics.Log($"Realtime caption translation skipped: {ex.GetType().Name}");
                    }
                }

                CaptionUpdated?.Invoke(this, new LiveCaptionUpdate(final, translated, IsFinal: true));
                lock (_recordSync)
                {
                    _recorded.Add(new SubtitleCue(
                        TimeSpan.FromMilliseconds(_currentTurnStartMs),
                        TimeSpan.FromMilliseconds(endMs),
                        final,
                        translated));
                }
                _currentTurnStartMs = endMs;
                return;
            }

            if (type == "error")
            {
                var errMsg = root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                    ? m.GetString() ?? "服务端返回错误"
                    : "实时字幕服务端错误";
                StatusChanged?.Invoke(this, "实时字幕错误：" + errMsg);
            }
        }
        catch (JsonException) { }
    }

    private static string ReadTextField(JsonElement root, params string[] names)
    {
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    if (name == "text") return val.Trim();
                    sb.Append(val.Trim());
                }
            }
        }
        return sb.ToString().Trim();
    }

    private static Task SendJsonAsync(ClientWebSocket ws, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, token);
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }
}
