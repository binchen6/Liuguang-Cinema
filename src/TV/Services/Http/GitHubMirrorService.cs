using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TV.Services.Http;

public readonly record struct GitHubRoute(string Label, string Url, bool IsMirror, string MirrorPrefix);

public readonly record struct GitHubDownloadProgress(
    double Percent,
    long Downloaded,
    long Total,
    double BytesPerSecond = 0,
    string? RouteLabel = null);

/// <summary>
/// Shared GitHub domestic acceleration infrastructure reused across AppUpdateService,
/// RepoPackService, JsSpiderClient, and PySpiderClient.
/// Races/prioritizes curated HTTPS China GitHub accelerators while enforcing strict SHA-256 integrity.
/// </summary>
public static class GitHubMirrorService
{
    public static readonly (string Label, string Prefix)[] ChinaMirrors =
    [
        ("国内加速 A", "https://gh-proxy.com/"),
        ("国内加速 B", "https://cdn.gh-proxy.org/"),
        ("国内加速 C", "https://edgeone.gh-proxy.org/"),
        ("国内加速 D", "https://gh-proxy.org/"),
        ("国内加速 E", "https://ghfast.top/")
    ];

    private static readonly ConcurrentDictionary<string, long> MirrorLatencyMs = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsGitHubUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            return false;
        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedMirrorHost(string host) =>
        ChinaMirrors.Any(m => Uri.TryCreate(m.Prefix, UriKind.Absolute, out var u) &&
                              u.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<GitHubRoute> BuildCandidateRoutes(string url, bool preferMirrorsFirst = true)
    {
        var routes = new List<GitHubRoute>();
        if (!IsGitHubUrl(url))
        {
            routes.Add(new GitHubRoute("直连", url, IsMirror: false, MirrorPrefix: string.Empty));
            return routes;
        }

        var orderedMirrors = ChinaMirrors
            .OrderBy(m => MirrorLatencyMs.TryGetValue(m.Prefix, out var ms) ? ms : 5000L)
            .ToArray();

        if (preferMirrorsFirst)
        {
            foreach (var (label, prefix) in orderedMirrors)
                routes.Add(new GitHubRoute(label, prefix + url, IsMirror: true, MirrorPrefix: prefix));
            routes.Add(new GitHubRoute("GitHub 直连", url, IsMirror: false, MirrorPrefix: string.Empty));
        }
        else
        {
            routes.Add(new GitHubRoute("GitHub 直连", url, IsMirror: false, MirrorPrefix: string.Empty));
            foreach (var (label, prefix) in orderedMirrors)
                routes.Add(new GitHubRoute(label, prefix + url, IsMirror: true, MirrorPrefix: prefix));
        }
        return routes;
    }

    public static async Task<(string Content, string RouteLabel)> GetTextWithMirrorsAsync(
        SourceHttpClient http,
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 4 * 1024 * 1024,
        TimeSpan? perAttemptTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var routes = BuildCandidateRoutes(url, preferMirrorsFirst: true);
        var attemptTimeout = perAttemptTimeout ?? TimeSpan.FromSeconds(8);
        Exception? lastError = null;

        foreach (var route in routes)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(attemptTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                var exchange = await http.GetExchangeAsync(route.Url, headers, maxBytes: maxBytes,
                    cancellationToken: attemptCts.Token, scopeRedirectHeaders: true).ConfigureAwait(false);
                if ((int)exchange.StatusCode is >= 200 and < 300 && exchange.Payload.Content.Length > 0)
                {
                    if (route.IsMirror)
                        MirrorLatencyMs[route.MirrorPrefix] = sw.ElapsedMilliseconds;
                    return (SourceHttpClient.DecodeText(exchange.Payload.Content), route.Label);
                }
                if (route.IsMirror) MirrorLatencyMs[route.MirrorPrefix] = 30_000;
                lastError = new HttpRequestException($"{route.Label} 返回 HTTP {(int)exchange.StatusCode}", null, exchange.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (route.IsMirror) MirrorLatencyMs[route.MirrorPrefix] = 30_000;
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException($"无法下载资源：{url}");
    }

    public static async Task<string> DownloadVerifiedFileAsync(
        HttpClient client,
        string url,
        string destinationPath,
        string? expectedSha256,
        long maxBytes = 64 * 1024 * 1024,
        IProgress<GitHubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256) && !IsSha256Hex(expectedSha256))
            throw new InvalidDataException("资源包 SHA-256 校验值格式无效。");

        var routes = BuildCandidateRoutes(url, preferMirrorsFirst: true);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partial = destinationPath + "." + Guid.NewGuid().ToString("N")[..8] + ".partial";
        Exception? lastError = null;

        try
        {
            foreach (var route in routes)
            {
                progress?.Report(new GitHubDownloadProgress(0, 0, 0, 0, $"正在连接 {route.Label}…"));
                var sw = Stopwatch.StartNew();
                try
                {
                    await DownloadSingleRouteAsync(client, route, partial, maxBytes, progress, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        var actual = await ComputeFileSha256Async(partial, cancellationToken).ConfigureAwait(false);
                        if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(partial);
                            if (route.IsMirror) MirrorLatencyMs[route.MirrorPrefix] = 60_000;
                            lastError = new InvalidDataException($"{route.Label} 校验失败（SHA-256 不匹配）。");
                            Diagnostics.Log($"[GitHubMirror] {route.Label} SHA-256 mismatch, trying next route.");
                            continue;
                        }
                    }

                    if (route.IsMirror) MirrorLatencyMs[route.MirrorPrefix] = sw.ElapsedMilliseconds;
                    File.Move(partial, destinationPath, overwrite: true);
                    return route.Label;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryDelete(partial);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(partial);
                    if (route.IsMirror) MirrorLatencyMs[route.MirrorPrefix] = 30_000;
                    lastError = ex;
                    Diagnostics.Log($"[GitHubMirror] {route.Label} failed: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }
        finally
        {
            TryDelete(partial);
        }

        throw lastError ?? new HttpRequestException($"所有下载线路均失败：{url}");
    }

    private static async Task DownloadSingleRouteAsync(
        HttpClient client,
        GitHubRoute route,
        string partialPath,
        long maxBytes,
        IProgress<GitHubDownloadProgress>? progress,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route.Url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Liuguang-Cinema/RepoPack");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);

        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > maxBytes)
            throw new InvalidDataException($"文件体积超过允许上限（{maxBytes / (1024 * 1024)} MB）。");

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        var buffer = new byte[65536];
        long downloaded = 0;
        var sw = Stopwatch.StartNew();
        var lastReport = 0L;

        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            readCts.CancelAfter(TimeSpan.FromSeconds(15));
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
            if (read == 0) break;
            downloaded += read;
            if (downloaded > maxBytes)
                throw new InvalidDataException($"下载内容超过允许上限（{maxBytes / (1024 * 1024)} MB）。");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

            var elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs - lastReport >= 120)
            {
                lastReport = elapsedMs;
                var speed = elapsedMs > 0 ? downloaded * 1000.0 / elapsedMs : 0;
                var pct = total > 0 ? Math.Clamp(downloaded * 100.0 / total, 0, 99.9) : 0;
                progress?.Report(new GitHubDownloadProgress(pct, downloaded, total, speed, route.Label));
            }
        }

        if (downloaded == 0)
            throw new InvalidDataException("服务器返回了空文件。");

        var finalSpeed = sw.ElapsedMilliseconds > 0 ? downloaded * 1000.0 / sw.ElapsedMilliseconds : 0;
        progress?.Report(new GitHubDownloadProgress(100, downloaded, Math.Max(total, downloaded), finalSpeed, route.Label));
    }

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken token = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
