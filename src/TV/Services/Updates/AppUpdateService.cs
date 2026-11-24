using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Updates;

public sealed record AppUpdateAsset(string Name, long Size, string DownloadUrl, string? Digest, bool SizeApproximate = false);
public sealed record AppUpdateRelease(Version Version, string Tag, string Title, string Notes, string PageUrl,
    DateTimeOffset PublishedAt, IReadOnlyList<AppUpdateAsset> Assets);
public sealed record AppUpdatePackage(AppUpdateAsset Asset, string? ChecksumUrl, string? ExpectedHash, bool Incremental);
public sealed record DownloadedUpdate(string Path, AppUpdatePackage Package);
public readonly record struct UpdateDownloadProgress(
    double Percent,
    long Downloaded,
    long Total,
    double BytesPerSecond = 0,
    string? RouteLabel = null);

public sealed partial class AppUpdateService : IDisposable
{
    private const string Repository = "binchen6/Liuguang-Cinema";
    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const string LatestWeb = $"https://github.com/{Repository}/releases/latest";
    private const long MaxPackageBytes = 512 * 1024 * 1024;
    private const long SegmentedThresholdBytes = 2 * 1024 * 1024;
    private const int ChunkSizeBytes = 2 * 1024 * 1024;
    private const int ProbeBytes = 64 * 1024;
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(15);

    private sealed record DownloadRoute(string Label, string Url, bool IsMirror, bool SupportsRange, double ScoreBytesPerSec, long ExactSize = 0);

    // Curated HTTPS GitHub Release accelerators for Mainland China users.
    // Every downloaded byte is still strictly verified against the official GitHub Release SHA-256 digest.
    private static readonly (string Label, string Prefix)[] ChinaMirrors =
    [
        ("国内加速 A", "https://gh-proxy.com/"),
        ("国内加速 B", "https://cdn.gh-proxy.org/"),
        ("国内加速 C", "https://edgeone.gh-proxy.org/"),
        ("国内加速 D", "https://gh-proxy.org/"),
        ("国内加速 E", "https://ghfast.top/")
    ];

    private readonly SourceHttpClient _http;
    private readonly DatabaseService _database;
    private readonly HttpClient _downloads;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly string _endpoint;
    private readonly bool _allowLoopback;

    public AppUpdateService(SourceHttpClient http, DnsResolverService dns, DatabaseService database)
    {
        _http = http;
        _database = database;
        _endpoint = Environment.GetEnvironmentVariable("TV_UPDATE_API_URL") is { Length: > 0 } endpoint ? endpoint : LatestApi;
        _allowLoopback = Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpointUri) && endpointUri.IsLoopback;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            MaxConnectionsPerServer = 12,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            ConnectCallback = dns.ConnectAsync
        };
        _downloads = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var assembly = typeof(AppUpdateService).Assembly.GetName().Version;
        CurrentVersion = new Version(assembly?.Major ?? 0, assembly?.Minor ?? 0, assembly?.Build >= 0 ? assembly.Build : 0);
    }

    public Version CurrentVersion { get; }

    public async Task<bool> ShouldCheckAutomaticallyAsync(CancellationToken token = default)
    {
        var value = await _database.GetSettingAsync("updates.lastCheck", token).ConfigureAwait(false);
        return !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt)
            || DateTimeOffset.UtcNow - checkedAt >= AutomaticInterval;
    }

    public async Task<AppUpdateRelease?> CheckAsync(CancellationToken token = default)
    {
        await _checkGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(CleanupStalePackages, token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = "Liuguang-Cinema/" + CurrentVersion,
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2022-11-28"
            };
            string json = string.Empty;
            Exception? lastError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    json = await _http.GetStringAsync(_endpoint, headers, deadline.Token).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && attempt == 0 && ex is HttpRequestException or TaskCanceledException)
                {
                    lastError = ex;
                    Diagnostics.Log($"GitHub Release 检查首次超时/波动，重试中: {ex.GetType().Name}");
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
                {
                    lastError = ex;
                }
            }
            AppUpdateRelease release;
            if (lastError is null)
            {
                release = ParseRelease(json);
            }
            else if (_endpoint.Equals(LatestApi, StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.Log($"GitHub API 检查失败 ({lastError.Message})，自动切换 GitHub Release 网页源检查");
                release = await CheckViaGitHubWebAsync(token).ConfigureAwait(false);
            }
            else
            {
                throw lastError;
            }
            await _database.SetSettingAsync("updates.lastCheck",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            return release.Version > CurrentVersion ? release : null;
        }
        finally { _checkGate.Release(); }
    }

    private async Task<AppUpdateRelease> CheckViaGitHubWebAsync(CancellationToken token)
    {
        var jsonHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "Liuguang-Cinema/" + CurrentVersion,
            ["Accept"] = "application/json"
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var latestJson = await _http.GetStringAsync(LatestWeb, jsonHeaders, deadline.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(latestJson, new JsonDocumentOptions { MaxDepth = 16 });
        var tag = ReadString(doc.RootElement, "tag_name");
        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Major < 0 || version.Minor < 0)
            throw new InvalidDataException("GitHub Release 网页源返回的版本号无效。");
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));

        var expandedUrl = $"https://github.com/{Repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
        var htmlHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "Liuguang-Cinema/" + CurrentVersion,
            ["Accept"] = "text/html"
        };
        var html = await _http.GetStringAsync(expandedUrl, htmlHeaders, deadline.Token).ConfigureAwait(false);
        var assets = ParseExpandedAssetsHtml(html, tag);
        if (assets.Count == 0)
            throw new InvalidDataException("GitHub Release 网页源未找到可用安装包。");
        return new AppUpdateRelease(
            version,
            tag,
            tag,
            string.Empty,
            $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}",
            DateTimeOffset.MinValue,
            assets);
    }

    internal static List<AppUpdateAsset> ParseExpandedAssetsHtml(string html, string tag)
    {
        var assets = new List<AppUpdateAsset>();
        var matches = ExpandedAssetLinkPattern().Matches(html);
        for (var i = 0; i < matches.Count; i++)
        {
            var relPath = matches[i].Groups[1].Value;
            if (!relPath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Uri.UnescapeDataString(relPath[(relPath.LastIndexOf('/') + 1)..]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var sliceStart = matches[i].Index;
            var sliceEnd = i + 1 < matches.Count ? matches[i + 1].Index : Math.Min(html.Length, sliceStart + 2500);
            var slice = html[sliceStart..sliceEnd];

            string? digest = null;
            var digestMatch = ExpandedAssetDigestPattern().Match(slice);
            if (digestMatch.Success && IsHash(digestMatch.Groups[1].Value))
                digest = digestMatch.Groups[1].Value.ToLowerInvariant();

            long approxSize = 1;
            var sizeMatch = ExpandedAssetSizePattern().Match(slice);
            if (sizeMatch.Success && double.TryParse(sizeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            {
                var unit = sizeMatch.Groups[2].Value.ToUpperInvariant();
                approxSize = unit switch
                {
                    "GB" => (long)(num * 1024 * 1024 * 1024),
                    "MB" => (long)(num * 1024 * 1024),
                    "KB" => (long)(num * 1024),
                    _ => Math.Max(1, (long)num)
                };
            }
            if (approxSize <= MaxPackageBytes)
                assets.Add(new AppUpdateAsset(name, approxSize, "https://github.com" + relPath, digest, SizeApproximate: true));
        }
        return assets;
    }

    internal static AppUpdateRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || ReadBool(root, "draft") || ReadBool(root, "prerelease"))
            throw new InvalidDataException("GitHub 发行版响应无效。");
        var tag = ReadString(root, "tag_name");
        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Major < 0 || version.Minor < 0)
            throw new InvalidDataException("发行版版本号无效。");
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (!root.TryGetProperty("assets", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("发行版没有下载资产。");
        var assets = new List<AppUpdateAsset>();
        foreach (var item in items.EnumerateArray())
        {
            var name = ReadString(item, "name");
            var url = ReadString(item, "browser_download_url");
            var size = item.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : 0;
            if (name.Length == 0 || url.Length == 0 || size is <= 0 or > MaxPackageBytes) continue;
            string? digest = null;
            var rawDigest = ReadString(item, "digest");
            if (rawDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsHash(rawDigest[7..])) digest = rawDigest[7..];
            assets.Add(new AppUpdateAsset(name, size, url, digest));
        }
        return new AppUpdateRelease(version, tag, ReadString(root, "name"), ReadString(root, "body"),
            ReadString(root, "html_url"), ReadDate(root, "published_at"), assets);
    }

    public AppUpdatePackage SelectPackage(AppUpdateRelease release, bool allowIncremental = true)
    {
        var current = CurrentVersion.ToString(3);
        var target = release.Version.ToString(3);
        if (allowIncremental)
        {
            var delta = FindAsset(release, $"TV-Update-{target}-from-{current}-x64.exe");
            if (delta is not null && TryPackage(release, delta, true, out var package)) return package;
        }
        var full = FindAsset(release, $"TV-Setup-{target}-x64.exe")
            ?? throw new InvalidDataException("当前发行版缺少 Windows x64 完整安装包。");
        if (TryPackage(release, full, false, out var fullPackage)) return fullPackage;
        throw new InvalidDataException("安装包缺少 SHA-256 校验信息，已停止更新。");
    }

    private static bool TryPackage(AppUpdateRelease release, AppUpdateAsset asset, bool incremental, out AppUpdatePackage package)
    {
        var checksum = FindAsset(release, asset.Name + ".sha256");
        if (asset.Digest is null && checksum is null)
        {
            package = null!;
            return false;
        }
        package = new AppUpdatePackage(asset, checksum?.DownloadUrl, asset.Digest, incremental);
        return true;
    }

    private static AppUpdateAsset? FindAsset(AppUpdateRelease release, string name) =>
        release.Assets.FirstOrDefault(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public async Task<DownloadedUpdate> DownloadAsync(AppUpdateRelease release, IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        var package = SelectPackage(release);
        try { return await DownloadPackageAsync(package, progress, token).ConfigureAwait(false); }
        catch (Exception ex) when (package.Incremental && ex is not OperationCanceledException)
        {
            Diagnostics.Log($"增量更新下载失败，回退完整安装包: {ex.GetType().Name} {ex.Message}");
            return await DownloadPackageAsync(SelectPackage(release, allowIncremental: false), progress, token).ConfigureAwait(false);
        }
    }

    private async Task<DownloadedUpdate> DownloadPackageAsync(AppUpdatePackage package, IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        ValidateAssetUrl(package.Asset.DownloadUrl);
        var expected = package.ExpectedHash;
        if (expected is null && package.ChecksumUrl is not null)
        {
            ValidateAssetUrl(package.ChecksumUrl);
            expected = await FetchChecksumAsync(package.ChecksumUrl, token).ConfigureAwait(false);
        }
        if (!IsHash(expected)) throw new InvalidDataException("安装包缺少有效的 SHA-256。");

        var folder = Path.Combine(AppPaths.UpdateCache, CurrentVersion.ToString(3) + "-to-" + package.Asset.Name);
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, package.Asset.Name);
        if (File.Exists(destination) &&
            (package.Asset.SizeApproximate || new FileInfo(destination).Length == package.Asset.Size) &&
            await HashMatchesAsync(destination, expected!, token).ConfigureAwait(false))
        {
            var actualLen = new FileInfo(destination).Length;
            progress?.Report(new UpdateDownloadProgress(100, actualLen, actualLen, 0, "本地缓存"));
            return new DownloadedUpdate(destination, package);
        }

        var partial = destination + ".partial";
        var planPath = partial + ".plan";
        var (usedMirror, resolvedSize) = await DownloadAssetSmartAsync(package.Asset, partial, planPath, progress, token).ConfigureAwait(false);
        var expectedSize = resolvedSize > 0 ? resolvedSize : package.Asset.Size;
        if (!File.Exists(partial) || (!package.Asset.SizeApproximate && new FileInfo(partial).Length != expectedSize) ||
            (resolvedSize > 0 && new FileInfo(partial).Length != resolvedSize))
        {
            CleanupPartialFiles(partial, planPath);
            throw new InvalidDataException("下载大小与 GitHub 发行版记录不一致。");
        }
        if (!await HashMatchesAsync(partial, expected!, token).ConfigureAwait(false))
        {
            CleanupPartialFiles(partial, planPath);
            if (usedMirror)
            {
                Diagnostics.Log("镜像加速下载 SHA-256 校验不匹配，已删除并自动切换 GitHub 官方直连重试");
                var directAsset = resolvedSize > 0 ? package.Asset with { Size = resolvedSize, SizeApproximate = false } : package.Asset;
                await DownloadWithResumeAsync(directAsset, package.Asset.DownloadUrl, "GitHub 官方直连", partial, progress, token).ConfigureAwait(false);
                if (!File.Exists(partial) ||
                    (!directAsset.SizeApproximate && new FileInfo(partial).Length != directAsset.Size) ||
                    !await HashMatchesAsync(partial, expected!, token).ConfigureAwait(false))
                {
                    CleanupPartialFiles(partial, planPath);
                    throw new InvalidDataException("SHA-256 校验失败，已删除损坏的下载文件。");
                }
            }
            else
            {
                throw new InvalidDataException("SHA-256 校验失败，已删除损坏的下载文件。");
            }
        }
        TryDeleteFile(planPath);
        File.Move(partial, destination, true);
        return new DownloadedUpdate(destination, package);
    }

    private async Task<string> FetchChecksumAsync(string checksumUrl, CancellationToken token)
    {
        var urls = new List<string> { checksumUrl };
        if (!_allowLoopback && IsOfficialGitHubAssetUrl(checksumUrl))
        {
            foreach (var (_, prefix) in ChinaMirrors.Take(3))
                urls.Insert(0, prefix + checksumUrl);
        }
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var checksumDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                checksumDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                var text = await _http.GetStringAsync(url, cancellationToken: checksumDeadline.Token).ConfigureAwait(false);
                var match = HashPattern().Match(text);
                if (match.Success) return match.Value;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidDataException("SHA-256 校验文件格式无效。");
    }

    /// <summary>
    /// Races China GitHub Release accelerators alongside direct GitHub, then runs multi-stream Range chunks
    /// for files >= 2 MB while preserving exact sequential Range resume for small files and loopback tests.
    /// </summary>
    private async Task<(bool UsedMirror, long ExactSize)> DownloadAssetSmartAsync(
        AppUpdateAsset asset,
        string partial,
        string planPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        // Preserve exact sequential resume path for loopback test fixtures and small files (< 2 MB)
        if (_allowLoopback || (!asset.SizeApproximate && asset.Size < SegmentedThresholdBytes) || !IsOfficialGitHubAssetUrl(asset.DownloadUrl))
        {
            await DownloadWithResumeAsync(asset, asset.DownloadUrl, "GitHub 直连", partial, progress, token).ConfigureAwait(false);
            return (false, asset.SizeApproximate ? 0 : asset.Size);
        }

        progress?.Report(new UpdateDownloadProgress(0, 0, asset.Size, 0, "正在优选最快下载线路…"));
        var ranked = await ProbeAndRankRoutesAsync(asset, token).ConfigureAwait(false);
        var exactSize = ranked.Select(r => r.ExactSize).FirstOrDefault(s => s > 0);
        if (exactSize > 0)
            asset = asset with { Size = exactSize, SizeApproximate = false };
        Diagnostics.Log("更新下载测速排序: " + string.Join(", ", ranked.Select(r => $"{r.Label}({r.ScoreBytesPerSec / 1024:0}KB/s,range={r.SupportsRange})")));

        var rangeRoutes = ranked.Where(r => r.SupportsRange).ToList();
        if (rangeRoutes.Count > 0 && !asset.SizeApproximate && asset.Size >= SegmentedThresholdBytes)
        {
            try
            {
                await DownloadSegmentedAsync(asset, rangeRoutes, partial, planPath, progress, token).ConfigureAwait(false);
                return (rangeRoutes.Any(r => r.IsMirror), asset.Size);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Diagnostics.Log($"多路分片下载异常，切换单路断点下载: {ex.GetType().Name} {ex.Message}");
                TryDeleteFile(planPath);
                TryDeleteFile(partial);
            }
        }

        Exception? lastError = null;
        foreach (var route in ranked)
        {
            try
            {
                await DownloadWithResumeAsync(asset, route.Url, route.Label, partial, progress, token).ConfigureAwait(false);
                return (route.IsMirror, asset.SizeApproximate ? 0 : asset.Size);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                Diagnostics.Log($"更新线路 {route.Label} 下载失败，切换备用线路: {ex.GetType().Name} {ex.Message}");
            }
        }
        throw lastError ?? new IOException("所有更新下载线路均不可用，请检查网络连接后重试。");
    }

    private async Task<List<DownloadRoute>> ProbeAndRankRoutesAsync(AppUpdateAsset asset, CancellationToken token)
    {
        var candidates = new List<(string Label, string Url, bool IsMirror)>(ChinaMirrors.Length + 1);
        foreach (var (label, prefix) in ChinaMirrors)
            candidates.Add((label, prefix + asset.DownloadUrl, true));
        candidates.Add(("GitHub 直连", asset.DownloadUrl, false));

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        probeCts.CancelAfter(TimeSpan.FromSeconds(5));
        var results = new ConcurrentBag<DownloadRoute>();
        var firstSuccessUtc = DateTimeOffset.MinValue;
        var successCount = 0;

        var tasks = candidates.Select(async c =>
        {
            var route = await ProbeSingleRouteAsync(c.Label, c.Url, c.IsMirror, asset.Size, asset.SizeApproximate, probeCts.Token).ConfigureAwait(false);
            if (route is null) return;
            results.Add(route);
            var count = Interlocked.Increment(ref successCount);
            if (count == 1) firstSuccessUtc = DateTimeOffset.UtcNow;
            // Once we have 3 working routes (or 2 fast routes after 0.9s), stop waiting on slow mirrors
            if (count >= 3 || (count >= 2 && DateTimeOffset.UtcNow - firstSuccessUtc > TimeSpan.FromMilliseconds(900)))
            {
                try { probeCts.Cancel(); } catch { }
            }
        }).ToArray();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

        var ranked = results
            .OrderByDescending(r => r.SupportsRange)
            .ThenByDescending(r => r.ScoreBytesPerSec)
            .ToList();

        // Always keep direct GitHub as ultimate fallback if not already probed
        if (!ranked.Any(r => !r.IsMirror))
            ranked.Add(new DownloadRoute("GitHub 直连", asset.DownloadUrl, false, true, 0));

        return ranked;
    }

    private async Task<DownloadRoute?> ProbeSingleRouteAsync(string label, string url, bool isMirror, long expectedSize, bool sizeApproximate, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Liuguang-Cinema/" + CurrentVersion);
            var probeEnd = sizeApproximate ? ProbeBytes - 1 : Math.Min(ProbeBytes - 1, expectedSize - 1);
            req.Headers.Range = new RangeHeaderValue(0, probeEnd);
            using var resp = await _downloads.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                var total = resp.Content.Headers.ContentRange?.Length ?? 0;
                if (!sizeApproximate && total > 0 && total != expectedSize)
                    return null;
                if (total > MaxPackageBytes)
                    return null;
                var buffer = new byte[16 * 1024];
                long readTotal = 0;
                await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                while (readTotal < ProbeBytes)
                {
                    var n = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (n == 0) break;
                    readTotal += n;
                }
                sw.Stop();
                if (readTotal <= 0) return null;
                var speed = readTotal / Math.Max(0.05, sw.Elapsed.TotalSeconds);
                return new DownloadRoute(label, url, isMirror, SupportsRange: true, ScoreBytesPerSec: speed, ExactSize: total);
            }
            if (resp.StatusCode == HttpStatusCode.OK &&
                resp.Content.Headers.ContentLength is { } fullLen &&
                fullLen > 0 && fullLen <= MaxPackageBytes &&
                (sizeApproximate || fullLen == expectedSize))
            {
                sw.Stop();
                return new DownloadRoute(label, url, isMirror, SupportsRange: false, ScoreBytesPerSec: 1024 / Math.Max(0.1, sw.Elapsed.TotalSeconds), ExactSize: fullLen);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task DownloadSegmentedAsync(
        AppUpdateAsset asset,
        List<DownloadRoute> routes,
        string partial,
        string planPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token)
    {
        var chunkCount = (int)((asset.Size + ChunkSizeBytes - 1) / ChunkSizeBytes);
        var completed = LoadOrResetChunkPlan(partial, planPath, asset.Size, chunkCount);
        var pending = new ConcurrentQueue<int>();
        long completedBytes = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var chunkLen = Math.Min(ChunkSizeBytes, asset.Size - (long)i * ChunkSizeBytes);
            if (completed[i]) completedBytes += chunkLen;
            else pending.Enqueue(i);
        }

        if (pending.IsEmpty)
        {
            progress?.Report(new UpdateDownloadProgress(100, asset.Size, asset.Size, 0, routes[0].Label));
            return;
        }

        var concurrency = Math.Min(pending.Count, asset.Size >= 32 * 1024 * 1024 ? 6 : 4);
        var primaryLabel = $"{routes[0].Label} · {concurrency} 路并发";
        var planLock = new object();
        long totalDownloaded = completedBytes;
        long bytesAtLastTick = completedBytes;
        var speedSw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        double smoothedBps = 0;

        void ReportProgress(long delta)
        {
            var nowTotal = Interlocked.Add(ref totalDownloaded, delta);
            var elapsedMs = speedSw.ElapsedMilliseconds;
            var prev = Interlocked.Read(ref lastReportMs);
            if (elapsedMs - prev < 120 && nowTotal < asset.Size) return;
            if (Interlocked.CompareExchange(ref lastReportMs, elapsedMs, prev) != prev && nowTotal < asset.Size) return;
            var dt = Math.Max(0.05, (elapsedMs - prev) / 1000.0);
            var prevBytes = Interlocked.Exchange(ref bytesAtLastTick, nowTotal);
            var instant = Math.Max(0, (nowTotal - prevBytes) / dt);
            smoothedBps = smoothedBps <= 0 ? instant : smoothedBps * 0.65 + instant * 0.35;
            var pct = asset.Size > 0 ? Math.Min(100.0, nowTotal * 100.0 / asset.Size) : 0;
            progress?.Report(new UpdateDownloadProgress(pct, Math.Min(nowTotal, asset.Size), asset.Size, smoothedBps, primaryLabel));
        }

        // Pre-allocate or open the partial file for lock-free RandomAccess writes
        using var handle = File.OpenHandle(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
        if (RandomAccess.GetLength(handle) != asset.Size)
            RandomAccess.SetLength(handle, asset.Size);

        var activeRoutes = routes.Take(3).ToList();
        if (!activeRoutes.Any(r => !r.IsMirror) && routes.FirstOrDefault(r => !r.IsMirror) is { } directFallback)
            activeRoutes.Add(directFallback);

        using var failCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workerTasks = Enumerable.Range(0, concurrency).Select(workerIdx => Task.Run(async () =>
        {
            var buffer = new byte[128 * 1024];
            var preferredRoute = workerIdx % Math.Min(2, activeRoutes.Count);
            while (!failCts.IsCancellationRequested && pending.TryDequeue(out var chunkIndex))
            {
                var start = (long)chunkIndex * ChunkSizeBytes;
                var end = Math.Min(asset.Size - 1, start + ChunkSizeBytes - 1);
                var expectedChunkLen = end - start + 1;
                var chunkDone = false;
                Exception? chunkError = null;

                for (var attempt = 0; attempt < activeRoutes.Count * 2 && !chunkDone; attempt++)
                {
                    var route = activeRoutes[(preferredRoute + attempt) % activeRoutes.Count];
                    long writtenThisAttempt = 0;
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, route.Url);
                        req.Headers.TryAddWithoutValidation("User-Agent", "Liuguang-Cinema/" + CurrentVersion);
                        req.Headers.Range = new RangeHeaderValue(start, end);
                        using var reqTimeout = CancellationTokenSource.CreateLinkedTokenSource(failCts.Token);
                        reqTimeout.CancelAfter(ReadStallTimeout);
                        using var resp = await _downloads.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqTimeout.Token).ConfigureAwait(false);
                        if (resp.StatusCode != HttpStatusCode.PartialContent ||
                            resp.Content.Headers.ContentRange?.From != start ||
                            resp.Content.Headers.ContentRange?.To != end)
                        {
                            throw new IOException($"线路 {route.Label} 分片响应无效 (HTTP {(int)resp.StatusCode})");
                        }
                        await using var stream = await resp.Content.ReadAsStreamAsync(reqTimeout.Token).ConfigureAwait(false);
                        var filePos = start;
                        while (writtenThisAttempt < expectedChunkLen)
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(failCts.Token);
                            readCts.CancelAfter(ReadStallTimeout);
                            var toRead = (int)Math.Min(buffer.Length, expectedChunkLen - writtenThisAttempt);
                            var n = await stream.ReadAsync(buffer.AsMemory(0, toRead), readCts.Token).ConfigureAwait(false);
                            if (n == 0) break;
                            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, n), filePos, failCts.Token).ConfigureAwait(false);
                            filePos += n;
                            writtenThisAttempt += n;
                            ReportProgress(n);
                        }
                        if (writtenThisAttempt != expectedChunkLen)
                            throw new IOException($"分片 #{chunkIndex} 传输不完整 ({writtenThisAttempt}/{expectedChunkLen})");
                        chunkDone = true;
                        preferredRoute = (preferredRoute + attempt) % activeRoutes.Count;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (writtenThisAttempt > 0) Interlocked.Add(ref totalDownloaded, -writtenThisAttempt);
                        chunkError = ex;
                        Diagnostics.Log($"分片 #{chunkIndex} 在 {route.Label} 失败/超时，切换线路重试: {ex.GetType().Name}");
                    }
                }

                if (!chunkDone)
                {
                    failCts.Cancel();
                    throw chunkError ?? new IOException($"分片 #{chunkIndex} 下载失败。");
                }

                lock (planLock)
                {
                    completed[chunkIndex] = true;
                    SaveChunkPlan(planPath, asset.Size, completed);
                }
            }
        }, token)).ToArray();

        await Task.WhenAll(workerTasks).ConfigureAwait(false);
        progress?.Report(new UpdateDownloadProgress(100, asset.Size, asset.Size, smoothedBps, primaryLabel));
    }

    private static bool[] LoadOrResetChunkPlan(string partial, string planPath, long expectedSize, int chunkCount)
    {
        var completed = new bool[chunkCount];
        try
        {
            if (File.Exists(partial) && new FileInfo(partial).Length == expectedSize && File.Exists(planPath))
            {
                var parts = File.ReadAllText(planPath).Trim().Split('|');
                if (parts.Length == 3 &&
                    long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size == expectedSize &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count == chunkCount &&
                    parts[2].Length == chunkCount)
                {
                    for (var i = 0; i < chunkCount; i++) completed[i] = parts[2][i] == '1';
                    return completed;
                }
            }
        }
        catch { }
        TryDeleteFile(planPath);
        return completed;
    }

    private static void SaveChunkPlan(string planPath, long expectedSize, bool[] completed)
    {
        try
        {
            var bits = new char[completed.Length];
            for (var i = 0; i < completed.Length; i++) bits[i] = completed[i] ? '1' : '0';
            File.WriteAllText(planPath, $"{expectedSize.ToString(CultureInfo.InvariantCulture)}|{completed.Length}|{new string(bits)}");
        }
        catch { }
    }

    private async Task DownloadWithResumeAsync(AppUpdateAsset asset, string url, string routeLabel, string partial,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        var offset = File.Exists(partial) ? Math.Min(new FileInfo(partial).Length, asset.Size) : 0;
        if (offset == asset.Size && offset > 0) offset = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Liuguang-Cinema/" + CurrentVersion);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        headTimeout.CancelAfter(ReadStallTimeout);
        using var response = await _downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headTimeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
        if (!append) offset = 0;
        var total = response.Content.Headers.ContentLength is { } length && length > 0 ? offset + length : asset.Size;
        if (total != asset.Size || response.Content.Headers.ContentLength > asset.Size)
            throw new InvalidDataException("更新服务器返回了不匹配的安装包大小。");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var downloaded = offset;
        var speedSw = Stopwatch.StartNew();
        var lastReportMs = 0L;
        var lastReportBytes = offset;
        double smoothedBps = 0;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(ReadStallTimeout);
            var read = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            downloaded += read;
            if (downloaded > asset.Size) throw new InvalidDataException("下载内容超过发行版记录大小。");
            var nowMs = speedSw.ElapsedMilliseconds;
            if (nowMs - lastReportMs >= 120 || downloaded == total)
            {
                var dt = Math.Max(0.05, (nowMs - lastReportMs) / 1000.0);
                var instant = Math.Max(0, (downloaded - lastReportBytes) / dt);
                smoothedBps = smoothedBps <= 0 ? instant : smoothedBps * 0.65 + instant * 0.35;
                lastReportMs = nowMs;
                lastReportBytes = downloaded;
                progress?.Report(new UpdateDownloadProgress(total > 0 ? downloaded * 100d / total : 0, downloaded, total, smoothedBps, routeLabel));
            }
        }
        await target.FlushAsync(token).ConfigureAwait(false);
    }

    private static bool IsOfficialGitHubAssetUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0;

    private void ValidateAssetUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidDataException("下载地址无效。");
        var production = IsOfficialGitHubAssetUrl(value);
        if (!production && !(_allowLoopback && uri.IsLoopback && uri.Scheme is "http" or "https"))
            throw new InvalidDataException("更新下载地址不属于官方 GitHub Release。");
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupPartialFiles(string partial, string planPath)
    {
        TryDeleteFile(partial);
        TryDeleteFile(planPath);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static DateTimeOffset ReadDate(JsonElement element, string name) =>
        DateTimeOffset.TryParse(ReadString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value : DateTimeOffset.MinValue;

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private void CleanupStalePackages()
    {
        try
        {
            if (!Directory.Exists(AppPaths.UpdateCache)) return;
            var currentPrefix = CurrentVersion.ToString(3) + "-to-";
            foreach (var directory in Directory.EnumerateDirectories(AppPaths.UpdateCache))
            {
                var info = new DirectoryInfo(directory);
                if (info.Name.StartsWith(currentPrefix, StringComparison.Ordinal) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try { info.Delete(recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _downloads.Dispose();

    [GeneratedRegex(@"(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"href=""(/[^""]+/releases/download/[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpandedAssetLinkPattern();

    [GeneratedRegex(@"sha256:([0-9a-f]{64})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpandedAssetDigestPattern();

    [GeneratedRegex(@">\s*(\d+(?:\.\d+)?)\s*(Bytes|KB|MB|GB)\s*<", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpandedAssetSizePattern();
}
