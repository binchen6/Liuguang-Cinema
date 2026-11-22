using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;
using TV.Services.Persistence;

namespace TV.Services.AI;

/// <summary>
/// Fast headless audio extractor using the bundled LibVLC runtime.
/// Extracts mono 16kHz Opus (OGG) — ~14 MB/hour — with automatic WAV fallback for maximum compatibility.
/// Works on local files, direct HTTP/HTTPS streams, HLS playlists, and local CloudMediaProxy URLs.
/// </summary>
public sealed class AudioExtractionService
{
    private const long MaxUploadAudioBytes = 95L * 1024 * 1024; // DashScope temp OSS limit is 100 MB

    public static string CacheDirectory => Path.Combine(AppPaths.Root, "cache", "ai-subtitles");

    public async Task<string> ExtractAudioAsync(
        string mediaUrl,
        IReadOnlyDictionary<string, string>? headers,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(CacheDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mediaUrl)))[..16].ToLowerInvariant();
        var stamp = Guid.NewGuid().ToString("N")[..8];

        // Primary: compact 16kHz mono Opus in OGG (~32 kbps -> ~14 MB/hour)
        var oggPath = Path.Combine(CacheDirectory, $"audio-{hash}-{stamp}.ogg");
        try
        {
            await ExtractWithSoutAsync(mediaUrl, headers, oggPath,
                "acodec=opus,ab=32,channels=1,samplerate=16000", "ogg", progress, token).ConfigureAwait(false);
            if (IsValidAudioFile(oggPath)) return oggPath;
            TryDelete(oggPath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TryDelete(oggPath);
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Opus audio extraction fallback: {ex.GetType().Name} {ex.Message}");
            TryDelete(oggPath);
        }

        // Fallback: 16kHz mono PCM WAV (universally supported by LibVLC & DashScope)
        var wavPath = Path.Combine(CacheDirectory, $"audio-{hash}-{stamp}.wav");
        try
        {
            await ExtractWithSoutAsync(mediaUrl, headers, wavPath,
                "acodec=s16l,channels=1,samplerate=16000", "wav", progress, token).ConfigureAwait(false);
            if (!IsValidAudioFile(wavPath))
                throw new InvalidDataException("音频提取结果为空，请确认当前视频包含可解码音轨。");
            return wavPath;
        }
        catch
        {
            TryDelete(wavPath);
            throw;
        }
    }

    private static async Task ExtractWithSoutAsync(
        string mediaUrl,
        IReadOnlyDictionary<string, string>? headers,
        string outputPath,
        string transcodeAudio,
        string mux,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken token)
    {
        progress?.Report(new AiSubtitleProgress("正在从视频提取音轨…", 0));

        await Task.Run(async () =>
        {
            TV.Services.Playback.PlayerService.EnsureVlcRuntimeInitialized();
            using var vlc = new LibVLC(
                "--no-video",
                "--vout=none",
                "--aout=none",
                "--no-osd",
                "--no-stats",
                "--no-lua",
                "--no-metadata-network-access",
                "--no-sout-video",
                "--sout-audio");
            using var player = new MediaPlayer(vlc);
            using var media = Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri) && uri.IsFile
                ? new Media(vlc, uri.LocalPath, FromType.FromPath)
                : new Media(vlc, mediaUrl, FromType.FromLocation);

            // Escape backslashes, single quotes, and braces for VLC sout syntax (handles Unicode/spaces/special usernames)
            var escapedDst = outputPath.Replace("\\", "\\\\").Replace("'", "\\'");
            media.AddOption($":sout=#transcode{{vcodec=none,{transcodeAudio}}}:std{{access=file,mux={mux},dst='{escapedDst}'}}");
            media.AddOption(":no-sout-all");
            media.AddOption(":sout-keep");
            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                {
                    if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        media.AddOption($":http-user-agent={value}");
                    else if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key.Equals("Referrer", StringComparison.OrdinalIgnoreCase))
                        media.AddOption($":http-referrer={value}");
                }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.EndReached += (_, _) => tcs.TrySetResult(true);
            player.EncounteredError += (_, _) => tcs.TrySetException(new InvalidOperationException("LibVLC 音轨提取失败。"));

            using var reg = token.Register(() => tcs.TrySetCanceled(token));

            if (!player.Play(media))
                throw new InvalidOperationException("无法启动音轨提取任务。");

            try
            {
                while (!tcs.Task.IsCompleted)
                {
                    await Task.WhenAny(tcs.Task, Task.Delay(350, token)).ConfigureAwait(false);
                    var pos = player.Position;
                    if (pos is > 0f and <= 1f)
                        progress?.Report(new AiSubtitleProgress($"正在提取音轨 ({pos * 100:0}%)…", pos * 100));
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > MaxUploadAudioBytes)
                        throw new InvalidOperationException("提取的音频文件超过 95 MB 临时上传上限，建议使用实时字幕模式。");
                }
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                try { player.Stop(); } catch { }
            }
        }, token).ConfigureAwait(false);

        progress?.Report(new AiSubtitleProgress("音轨提取完成", 100));
    }

    private static bool IsValidAudioFile(string path)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        return length > 1024 && length <= MaxUploadAudioBytes;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
