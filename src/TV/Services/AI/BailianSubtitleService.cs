using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.AI;

/// <summary>
/// Alibaba Cloud Bailian (DashScope) AI subtitle generation (paraformer-v2 / qwen3-asr-flash-filetrans)
/// and subtitle translation (qwen-mt-flash / qwen-mt-plus).
/// </summary>
public sealed partial class BailianSubtitleService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly AudioExtractionService _extractor;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public BailianSubtitleService(DatabaseService database, DnsResolverService? dns = null)
    {
        _database = database;
        _extractor = new AudioExtractionService();
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8
        };
        if (dns is not null) handler.ConnectCallback = dns.ConnectAsync;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = true;
    }

    internal BailianSubtitleService(DatabaseService database, HttpMessageHandler handler)
    {
        _database = database;
        _extractor = new AudioExtractionService();
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        _ownsHttp = true;
    }

    public async Task<AiSubtitleConfig> GetConfigAsync(CancellationToken token = default)
    {
        var apiKey = SecretStore.Unprotect(await _database.GetSettingAsync(AiSubtitleModels.SettingApiKey, token).ConfigureAwait(false));
        var region = await _database.GetSettingAsync(AiSubtitleModels.SettingRegion, token).ConfigureAwait(false) ?? "cn";
        var asrModel = await _database.GetSettingAsync(AiSubtitleModels.SettingAsrModel, token).ConfigureAwait(false);
        if (asrModel is not (AiSubtitleModels.ParaformerV2 or AiSubtitleModels.Qwen3AsrFlashFiletrans))
            asrModel = AiSubtitleModels.Qwen3AsrFlashFiletrans;
        var mtModel = await _database.GetSettingAsync(AiSubtitleModels.SettingMtModel, token).ConfigureAwait(false);
        if (mtModel is not (AiSubtitleModels.QwenMtFlash or AiSubtitleModels.QwenMtPlus))
            mtModel = AiSubtitleModels.QwenMtFlash;
        var targetLang = await _database.GetSettingAsync(AiSubtitleModels.SettingTargetLanguage, token).ConfigureAwait(false) ?? "Chinese";
        var bilingual = !string.Equals(await _database.GetSettingAsync(AiSubtitleModels.SettingBilingual, token).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);
        var realtimeTranslate = string.Equals(await _database.GetSettingAsync(AiSubtitleModels.SettingRealtimeTranslate, token).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);
        var sourceLang = await _database.GetSettingAsync(AiSubtitleModels.SettingSourceLanguage, token).ConfigureAwait(false) ?? "auto";
        return new AiSubtitleConfig(apiKey, region, asrModel, mtModel, targetLang, bilingual, realtimeTranslate, sourceLang);
    }

    public Task SaveApiKeyAsync(string apiKey, CancellationToken token = default) =>
        _database.SetSettingAsync(AiSubtitleModels.SettingApiKey, SecretStore.Protect(apiKey), token);

    public Task ClearApiKeyAsync(CancellationToken token = default) =>
        _database.SetSettingAsync(AiSubtitleModels.SettingApiKey, string.Empty, token);

    /// <summary>
    /// Zero-cost API key verification using the DashScope temporary upload policy endpoint.
    /// </summary>
    public async Task<(bool Ok, string Message)> VerifyApiKeyAsync(string? apiKeyOverride = null, string? regionOverride = null, CancellationToken token = default)
    {
        var config = await GetConfigAsync(token).ConfigureAwait(false);
        var key = string.IsNullOrWhiteSpace(apiKeyOverride) ? config.ApiKey : apiKeyOverride.Trim();
        var region = string.IsNullOrWhiteSpace(regionOverride) ? config.Region : regionOverride;
        if (string.IsNullOrWhiteSpace(key))
            return (false, "请先输入阿里云百炼 API Key。");

        var probe = config with { ApiKey = key, Region = region };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{probe.HttpBaseUrl}/api/v1/uploads?action=getPolicy&model={AiSubtitleModels.ParaformerV2}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, "API Key 无效或无权限（HTTP " + (int)resp.StatusCode + "），请核对百炼平台密钥。");
            if (!resp.IsSuccessStatusCode)
            {
                var err = await ReadErrorMessageAsync(resp, token).ConfigureAwait(false);
                return (false, $"百炼接口返回 HTTP {(int)resp.StatusCode}：{err}");
            }
            return (true, "阿里云百炼 API Key 验证通过，可正常使用 AI 字幕生成与翻译。");
        }
        catch (Exception ex)
        {
            return (false, "连接阿里云百炼失败：" + ex.Message);
        }
    }

    public async Task<AiSubtitleResult> GenerateSubtitleAsync(
        string mediaUrl,
        IReadOnlyDictionary<string, string>? headers,
        string titleHint,
        bool translateAfter,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken token,
        TimeSpan? videoDuration = null)
    {
        var config = await GetConfigAsync(token).ConfigureAwait(false);
        var cues = await TranscribeAudioAsync(mediaUrl, headers, config, videoDuration, progress, token).ConfigureAwait(false);
        if (translateAfter)
            cues = await TranslateCuesAsync(cues, config, progress, token).ConfigureAwait(false);

        Directory.CreateDirectory(AudioExtractionService.CacheDirectory);
        var safeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{mediaUrl}|{config.AsrModel}|{(translateAfter ? config.TargetLanguage : "orig")}")))[..16].ToLowerInvariant();
        var suffix = translateAfter ? $".{SanitizeToken(config.TargetLanguage)}" : string.Empty;
        var outPath = Path.Combine(AudioExtractionService.CacheDirectory, $"ai-{safeHash}{suffix}.srt");
        var srtContent = SubtitleFileCodec.WriteSrt(cues, bilingual: translateAfter && config.Bilingual);
        await WriteAtomicTextAsync(outPath, srtContent, token).ConfigureAwait(false);

        progress?.Report(new AiSubtitleProgress($"AI 字幕已生成（共 {cues.Count} 条）", 100));
        return new AiSubtitleResult(outPath, cues.Count, config.AsrModel, translateAfter);
    }

    internal async Task<List<SubtitleCue>> TranscribeAudioAsync(
        string mediaUrl, IReadOnlyDictionary<string, string>? headers, AiSubtitleConfig config,
        TimeSpan? duration, IProgress<AiSubtitleProgress>? progress, CancellationToken token)
    {
        if (!config.HasApiKey)
            throw new InvalidOperationException("请在「设置 → 弹幕与 AI」配置阿里云百炼 API Key。");

        string? extractedAudio = null;
        try
        {
            extractedAudio = await _extractor.ExtractAudioAsync(mediaUrl, headers, progress, token).ConfigureAwait(false);

            progress?.Report(new AiSubtitleProgress($"正在上传音轨至百炼临时存储（{config.AsrModel}）…", 10));
            var ossUri = await UploadTempFileAsync(extractedAudio, config.AsrModel, config, token).ConfigureAwait(false);

            progress?.Report(new AiSubtitleProgress($"正在提交 {config.AsrModel} 语音转写任务（开启字级时间戳对齐）…", 20));
            var taskId = await SubmitTranscriptionTaskAsync(ossUri, config, token).ConfigureAwait(false);

            var transcriptionUrl = await PollTranscriptionTaskAsync(taskId, config, progress, token).ConfigureAwait(false);

            progress?.Report(new AiSubtitleProgress("正在对齐识别结果的时间轴…", 92));
            var rawCues = await DownloadAndParseTranscriptionAsync(transcriptionUrl, token).ConfigureAwait(false);
            var cues = SubtitleFileCodec.SplitAndAlignCues(rawCues, duration);
            if (cues.Count == 0)
                throw new InvalidDataException("未能从该音轨中识别出有效内容。");
            return cues;
        }
        finally
        {
            AudioExtractionService.TryDelete(extractedAudio);
        }
    }

    public async Task<AiSubtitleResult> TranslateSubtitleFileAsync(
        string sourcePath,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken token)
    {
        var config = await GetConfigAsync(token).ConfigureAwait(false);
        if (!config.HasApiKey)
            throw new InvalidOperationException("尚未配置阿里云百炼 API Key，请前往“设置 → 弹幕与 AI”获取并填入密钥。");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到待翻译的字幕文件。", sourcePath);

        progress?.Report(new AiSubtitleProgress("正在解析原字幕文件…", 5));
        var cues = SubtitleFileCodec.ParseFile(sourcePath);
        if (cues.Count == 0)
            throw new InvalidDataException("字幕文件中没有可翻译的字幕条目。");

        var translated = await TranslateCuesAsync(cues, config, progress, token).ConfigureAwait(false);
        Directory.CreateDirectory(AudioExtractionService.CacheDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourcePath}|{config.MtModel}|{config.TargetLanguage}|{config.Bilingual}")))[..16].ToLowerInvariant();
        var outPath = Path.Combine(AudioExtractionService.CacheDirectory, $"tr-{hash}.{SanitizeToken(config.TargetLanguage)}.srt");
        var srt = SubtitleFileCodec.WriteSrt(translated, bilingual: config.Bilingual);
        await WriteAtomicTextAsync(outPath, srt, token).ConfigureAwait(false);

        progress?.Report(new AiSubtitleProgress($"字幕翻译完成（共 {translated.Count} 条）", 100));
        return new AiSubtitleResult(outPath, translated.Count, config.MtModel, IsTranslated: true);
    }

    public async Task<List<SubtitleCue>> TranslateCuesAsync(
        IReadOnlyList<SubtitleCue> cues,
        AiSubtitleConfig config,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken token)
    {
        if (cues.Count == 0) return [];
        const int batchSize = 16;
        var results = new string?[cues.Count];
        var batches = new List<(int Start, int Count)>();
        for (var i = 0; i < cues.Count; i += batchSize)
            batches.Add((i, Math.Min(batchSize, cues.Count - i)));

        var completedCount = 0;
        var langLabel = AiSubtitleModels.TargetLanguages.FirstOrDefault(l => l.ApiName == config.TargetLanguage)?.DisplayName ?? config.TargetLanguage;
        progress?.Report(new AiSubtitleProgress($"正在使用 {config.MtModel} 翻译为{langLabel} (0/{cues.Count})…", 0));

        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token }, async (batch, ct) =>
        {
            var slice = new string[batch.Count];
            for (var j = 0; j < batch.Count; j++)
                slice[j] = cues[batch.Start + j].Text.Replace('\r', ' ').Replace('\n', ' ').Trim();

            var translatedSlice = await TranslateBatchAsync(slice, config, ct).ConfigureAwait(false);
            for (var j = 0; j < batch.Count; j++)
                results[batch.Start + j] = translatedSlice[j];

            var done = Interlocked.Add(ref completedCount, batch.Count);
            progress?.Report(new AiSubtitleProgress(
                $"正在使用 {config.MtModel} 翻译为{langLabel} ({done}/{cues.Count})…",
                done * 100.0 / cues.Count));
        }).ConfigureAwait(false);

        var output = new List<SubtitleCue>(cues.Count);
        for (var i = 0; i < cues.Count; i++)
            output.Add(cues[i] with { TranslatedText = string.IsNullOrWhiteSpace(results[i]) ? cues[i].Text : results[i] });
        return output;
    }

    public async Task<string> TranslateSingleLineAsync(string text, AiSubtitleConfig config, CancellationToken token)
    {
        var clean = text.Trim();
        if (clean.Length == 0 || !config.HasApiKey) return clean;
        return await CallQwenMtAsync(clean, config, token).ConfigureAwait(false);
    }

    private async Task<string[]> TranslateBatchAsync(string[] lines, AiSubtitleConfig config, CancellationToken token)
    {
        if (lines.Length == 1)
            return [await CallQwenMtAsync(lines[0], config, token).ConfigureAwait(false)];

        // Format with stable numeric markers so Qwen-MT preserves 1-to-1 cue alignment
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(lines[i]);

        var raw = await CallQwenMtAsync(sb.ToString().TrimEnd(), config, token).ConfigureAwait(false);
        var parsed = ParseNumberedLines(raw, lines.Length);
        if (parsed is not null) return parsed;

        // Fallback: translate each line individually if batch markers were altered
        var individual = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            individual[i] = await CallQwenMtAsync(lines[i], config, token).ConfigureAwait(false);
        return individual;
    }

    private async Task<string> CallQwenMtAsync(string content, AiSubtitleConfig config, CancellationToken token)
    {
        var url = $"{config.HttpBaseUrl}/compatible-mode/v1/chat/completions";
        // Qwen-MT requires exactly one user message, no system message, and translation_options at root body
        var payload = new
        {
            model = config.MtModel,
            messages = new[]
            {
                new { role = "user", content }
            },
            translation_options = new
            {
                source_lang = "auto",
                target_lang = config.TargetLanguage
            }
        };

        var json = JsonSerializer.Serialize(payload);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), token).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var err = await ReadErrorMessageAsync(resp, token).ConfigureAwait(false);
                throw new HttpRequestException($"百炼翻译失败（HTTP {(int)resp.StatusCode}）：{err}", null, resp.StatusCode);
            }
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var msgContent))
                    return (msgContent.GetString() ?? string.Empty).Trim();
            }
            return string.Empty;
        }
        return content;
    }

    internal static string[]? ParseNumberedLines(string raw, int expectedCount)
    {
        var map = new Dictionary<int, string>();
        var currentIdx = -1;
        var currentText = new StringBuilder();

        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var match = NumberedLinePattern().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num) && num >= 1 && num <= expectedCount)
            {
                if (currentIdx > 0) map[currentIdx] = currentText.ToString().Trim();
                currentIdx = num;
                currentText.Clear();
                currentText.Append(match.Groups[2].Value.Trim());
            }
            else if (currentIdx > 0)
            {
                if (currentText.Length > 0) currentText.Append(' ');
                currentText.Append(line);
            }
        }
        if (currentIdx > 0) map[currentIdx] = currentText.ToString().Trim();
        if (map.Count != expectedCount) return null;

        var result = new string[expectedCount];
        for (var i = 0; i < expectedCount; i++)
            result[i] = map[i + 1];
        return result;
    }

    private async Task<string> UploadTempFileAsync(string localFilePath, string model, AiSubtitleConfig config, CancellationToken token)
    {
        // Step 1: Request temporary OSS upload policy
        var policyUrl = $"{config.HttpBaseUrl}/api/v1/uploads?action=getPolicy&model={Uri.EscapeDataString(model)}";
        using var policyReq = new HttpRequestMessage(HttpMethod.Get, policyUrl);
        policyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        using var policyResp = await _http.SendAsync(policyReq, token).ConfigureAwait(false);
        if (!policyResp.IsSuccessStatusCode)
        {
            var err = await ReadErrorMessageAsync(policyResp, token).ConfigureAwait(false);
            throw new HttpRequestException($"获取百炼临时上传凭证失败（HTTP {(int)policyResp.StatusCode}）：{err}", null, policyResp.StatusCode);
        }

        var policyBody = await policyResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var policyDoc = JsonDocument.Parse(policyBody);
        if (!policyDoc.RootElement.TryGetProperty("data", out var data))
            throw new InvalidDataException("百炼上传凭证响应格式异常。");

        var uploadHost = data.GetProperty("upload_host").GetString() ?? throw new InvalidDataException("缺少 upload_host。");
        var uploadDir = data.GetProperty("upload_dir").GetString() ?? throw new InvalidDataException("缺少 upload_dir。");
        var ossAccessKeyId = data.GetProperty("oss_access_key_id").GetString() ?? string.Empty;
        var policy = data.GetProperty("policy").GetString() ?? string.Empty;
        var signature = data.GetProperty("signature").GetString() ?? string.Empty;
        var acl = data.TryGetProperty("x_oss_object_acl", out var aclProp) ? aclProp.GetString() ?? "private" : "private";
        var forbidOverwrite = data.TryGetProperty("x_oss_forbid_overwrite", out var foProp) ? foProp.GetString() ?? "true" : "true";

        var fileName = Path.GetFileName(localFilePath);
        var objectKey = $"{uploadDir.TrimEnd('/')}/{fileName}";

        // Step 2: Upload audio file via multipart/form-data to OSS (file field MUST be last)
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(ossAccessKeyId), "OSSAccessKeyId");
        form.Add(new StringContent(policy), "policy");
        form.Add(new StringContent(signature), "Signature");
        form.Add(new StringContent(objectKey), "key");
        form.Add(new StringContent(acl), "x-oss-object-acl");
        form.Add(new StringContent(forbidOverwrite), "x-oss-forbid-overwrite");
        form.Add(new StringContent("200"), "success_action_status");

        await using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(streamContent, "file", fileName);

        using var uploadResp = await _http.PostAsync(uploadHost, form, token).ConfigureAwait(false);
        if (!uploadResp.IsSuccessStatusCode)
        {
            var err = await ReadErrorMessageAsync(uploadResp, token).ConfigureAwait(false);
            throw new HttpRequestException($"上传音轨到百炼临时存储失败（HTTP {(int)uploadResp.StatusCode}）：{err}", null, uploadResp.StatusCode);
        }

        return $"oss://{objectKey}";
    }

    private async Task<string> SubmitTranscriptionTaskAsync(string audioUri, AiSubtitleConfig config, CancellationToken token)
    {
        var url = $"{config.HttpBaseUrl}/api/v1/services/audio/asr/transcription";
        string bodyJson;
        if (string.Equals(config.AsrModel, AiSubtitleModels.Qwen3AsrFlashFiletrans, StringComparison.OrdinalIgnoreCase))
        {
            var parameters = new Dictionary<string, object>
            {
                ["channel_id"] = new[] { 0 },
                ["enable_itn"] = true,
                ["enable_words"] = true
            };
            if (!string.Equals(config.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(config.SourceLanguage))
                parameters["language"] = config.SourceLanguage;

            bodyJson = JsonSerializer.Serialize(new
            {
                model = AiSubtitleModels.Qwen3AsrFlashFiletrans,
                input = new { file_url = audioUri },
                parameters
            });
        }
        else
        {
            var hints = string.Equals(config.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(config.SourceLanguage)
                ? new[] { "zh", "en", "ja", "ko" }
                : new[] { config.SourceLanguage };

            bodyJson = JsonSerializer.Serialize(new
            {
                model = AiSubtitleModels.ParaformerV2,
                input = new { file_urls = new[] { audioUri } },
                parameters = new
                {
                    channel_id = new[] { 0 },
                    language_hints = hints,
                    timestamp_alignment_enabled = true
                }
            });
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        req.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        if (audioUri.StartsWith("oss://", StringComparison.OrdinalIgnoreCase))
            req.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");

        using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await ReadErrorMessageAsync(resp, token).ConfigureAwait(false);
            throw new HttpRequestException($"提交语音转写任务失败（HTTP {(int)resp.StatusCode}）：{err}", null, resp.StatusCode);
        }

        var responseText = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("output", out var output) &&
            output.TryGetProperty("task_id", out var taskIdProp) &&
            taskIdProp.GetString() is { Length: > 0 } taskId)
        {
            return taskId;
        }
        throw new InvalidDataException("百炼语音转写响应缺少 task_id。");
    }

    private async Task<string> PollTranscriptionTaskAsync(string taskId, AiSubtitleConfig config, IProgress<AiSubtitleProgress>? progress, CancellationToken token)
    {
        var url = $"{config.HttpBaseUrl}/api/v1/tasks/{Uri.EscapeDataString(taskId)}";
        var pollCount = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(pollCount == 0 ? 1500 : 2800, token).ConfigureAwait(false);
            pollCount++;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await ReadErrorMessageAsync(resp, token).ConfigureAwait(false);
                throw new HttpRequestException($"查询转写进度失败（HTTP {(int)resp.StatusCode}）：{err}", null, resp.StatusCode);
            }

            var text = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("output", out var output)) continue;

            var status = output.TryGetProperty("task_status", out var stProp) ? stProp.GetString() ?? "RUNNING" : "RUNNING";
            if (status.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                // Paraformer returns output.results[0].transcription_url; qwen3-asr-flash-filetrans may return output.result.transcription_url or output.results[0].transcription_url
                if (TryExtractTranscriptionUrl(output, out var resultUrl))
                    return resultUrl;
                throw new InvalidDataException("语音转写已完成，但未返回结果下载地址。");
            }
            if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase))
            {
                var msg = output.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(msg) && output.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0)
                    msg = res[0].TryGetProperty("message", out var subMsg) ? subMsg.GetString() : null;
                throw new InvalidOperationException("语音转写任务失败：" + (string.IsNullOrWhiteSpace(msg) ? status : msg));
            }

            var syntheticPct = Math.Min(88, 25 + pollCount * 4);
            var label = status.Equals("PENDING", StringComparison.OrdinalIgnoreCase)
                ? $"云端排队中（{config.AsrModel}）…"
                : $"云端语音识别中（{config.AsrModel}，已用时 {pollCount * 3}s）…";
            progress?.Report(new AiSubtitleProgress(label, syntheticPct));
        }
    }

    internal static bool TryExtractTranscriptionUrl(JsonElement output, out string url)
    {
        url = string.Empty;
        if (output.TryGetProperty("result", out var single) &&
            single.ValueKind == JsonValueKind.Object &&
            single.TryGetProperty("transcription_url", out var singleUrl) &&
            singleUrl.GetString() is { Length: > 0 } u1)
        {
            url = u1;
            return true;
        }
        if (output.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("transcription_url", out var itemUrl) && itemUrl.GetString() is { Length: > 0 } u2)
                {
                    url = u2;
                    return true;
                }
            }
        }
        return false;
    }

    private async Task<List<SubtitleCue>> DownloadAndParseTranscriptionAsync(string transcriptionUrl, CancellationToken token)
    {
        using var resp = await _http.GetAsync(transcriptionUrl, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return ParseTranscriptionJson(json);
    }

    public static List<SubtitleCue> ParseTranscriptionJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cues = new List<SubtitleCue>();
        if (!doc.RootElement.TryGetProperty("transcripts", out var transcripts) || transcripts.ValueKind != JsonValueKind.Array)
            return cues;

        foreach (var channel in transcripts.EnumerateArray())
        {
            if (channel.TryGetProperty("sentences", out var sentences) && sentences.ValueKind == JsonValueKind.Array)
            {
                foreach (var sent in sentences.EnumerateArray())
                {
                    var text = sent.TryGetProperty("text", out var tProp) ? tProp.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var beginMs = ReadDouble(sent, "begin_time");
                    var endMs = ReadDouble(sent, "end_time");
                    if (endMs <= beginMs) endMs = beginMs + 1500;
                    List<SubtitleWord>? words = null;
                    if (sent.TryGetProperty("words", out var wordsProp) && wordsProp.ValueKind == JsonValueKind.Array && wordsProp.GetArrayLength() > 0)
                    {
                        words = new List<SubtitleWord>(wordsProp.GetArrayLength());
                        foreach (var w in wordsProp.EnumerateArray())
                        {
                            var wText = w.TryGetProperty("text", out var wt) ? wt.GetString() ?? string.Empty : string.Empty;
                            var wPunct = w.TryGetProperty("punctuation", out var wp) ? wp.GetString() ?? string.Empty : string.Empty;
                            var combined = (wText + wPunct).Trim();
                            if (combined.Length == 0) continue;
                            var wb = ReadDouble(w, "begin_time");
                            var we = ReadDouble(w, "end_time");
                            if (we < wb) we = wb;
                            words.Add(new SubtitleWord(TimeSpan.FromMilliseconds(Math.Max(0, wb)), TimeSpan.FromMilliseconds(Math.Max(0, we)), combined));
                        }
                        if (words.Count == 0) words = null;
                    }
                    cues.Add(new SubtitleCue(TimeSpan.FromMilliseconds(Math.Max(0, beginMs)), TimeSpan.FromMilliseconds(endMs), text, Words: words));
                }
            }
        }
        cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static double ReadDouble(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var v)) return v;
        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return response.ReasonPhrase ?? "未知错误";
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } m) return m;
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var errMsg) && errMsg.GetString() is { Length: > 0 } em) return em;
            return body[..Math.Min(180, body.Length)];
        }
        catch { return response.ReasonPhrase ?? "未知错误"; }
    }

    private static async Task WriteAtomicTextAsync(string path, string content, CancellationToken token)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), token).ConfigureAwait(false);
            File.Move(tmp, path, true);
        }
        finally
        {
            AudioExtractionService.TryDelete(tmp);
        }
    }

    private static string SanitizeToken(string value) =>
        string.Concat(value.Where(char.IsAsciiLetterOrDigit)).ToLowerInvariant();

    [GeneratedRegex(@"^\[(\d+)\]\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLinePattern();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
