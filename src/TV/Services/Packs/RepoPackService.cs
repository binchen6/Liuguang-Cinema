using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Packs;

/// <summary>
/// Manages downloadable Repository Resource Packs hosted on an independent GitHub repository
/// (`binchen6/Liuguang-RepoPacks`), reusing China GitHub mirror acceleration and strict SHA-256 verification.
/// Supports both automatic pack download upon importing a matching source repository and manual management in Settings.
/// </summary>
public sealed class RepoPackService : IDisposable
{
    public const string DefaultRepository = "binchen6/Liuguang-RepoPacks";
    public const string DefaultManifestUrl = $"https://raw.githubusercontent.com/{DefaultRepository}/main/manifest.json";
    private const long MaxPackZipBytes = 32 * 1024 * 1024;

    private readonly SourceHttpClient _http;
    private readonly HttpClient _downloads;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly string _packsRoot;
    private readonly string _manifestUrl;
    private readonly string _manifestCachePath;

    private RepoPackManifest? _cachedManifest;
    private DateTimeOffset _manifestFetchedAt;
    private volatile IReadOnlyDictionary<string, InstalledRepoPack> _installed =
        new Dictionary<string, InstalledRepoPack>(StringComparer.OrdinalIgnoreCase);

    public RepoPackService(
        SourceHttpClient http,
        DnsResolverService dns,
        string? packsRoot = null,
        string? manifestUrl = null,
        HttpMessageHandler? downloadHandler = null)
    {
        _http = http;
        _packsRoot = string.IsNullOrWhiteSpace(packsRoot) ? AppPaths.RepoPacks : packsRoot;
        _manifestUrl = !string.IsNullOrWhiteSpace(manifestUrl)
            ? manifestUrl
            : (Environment.GetEnvironmentVariable("TV_REPOPACK_MANIFEST_URL") is { Length: > 0 } envUrl ? envUrl : DefaultManifestUrl);
        _manifestCachePath = Path.Combine(_packsRoot, "manifest-origin-cache.json");

        HttpMessageHandler handler = downloadHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            ConnectCallback = dns.ConnectAsync
        };
        _downloads = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        ReloadInstalledPacks();
    }

    public event EventHandler? Changed;

    public IReadOnlyCollection<InstalledRepoPack> InstalledPacks => _installed.Values.ToArray();

    public bool TryGetInstalled(string packId, out InstalledRepoPack pack) =>
        _installed.TryGetValue(packId, out pack!);

    public bool TryFindRule(SourceSite site, bool overrideUniversalOnly, out InstalledRepoPack pack, out RepoPackSiteRule rule)
    {
        (InstalledRepoPack Pack, RepoPackSiteRule Rule)? apiMatch = null;
        foreach (var installed in _installed.Values)
        {
            foreach (var candidate in installed.Definition.Sites)
            {
                if (overrideUniversalOnly && !candidate.OverrideUniversal) continue;
                if (!string.IsNullOrWhiteSpace(candidate.Key) &&
                    candidate.Key.Equals(site.Key, StringComparison.OrdinalIgnoreCase))
                {
                    pack = installed;
                    rule = candidate;
                    return true;
                }
                if (string.IsNullOrWhiteSpace(candidate.Key) &&
                    !string.IsNullOrWhiteSpace(candidate.Api) &&
                    candidate.Api.Equals(site.Api, StringComparison.OrdinalIgnoreCase))
                {
                    apiMatch ??= (installed, candidate);
                }
            }
        }
        if (apiMatch is { } matched)
        {
            (pack, rule) = matched;
            return true;
        }
        pack = null!;
        rule = null!;
        return false;
    }

    public IReadOnlyList<LiveSource> GetSupplementalLives() =>
        _installed.Values.SelectMany(p => p.Definition.Lives).ToArray();

    public async Task<RepoPackManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!forceRefresh && _cachedManifest is not null && DateTimeOffset.UtcNow - _manifestFetchedAt < TimeSpan.FromMinutes(15))
            return _cachedManifest;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "Liuguang-Cinema/RepoPack",
            ["Accept"] = "application/json"
        };

        try
        {
            if (!Uri.TryCreate(_manifestUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                uri.UserInfo.Length != 0)
                throw new InvalidDataException("资源包清单地址无效。");

            // The origin supplies the digest. Mirrors can serve the ZIP only after we have that digest.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            var payload = await _http.GetTrustedBytesAsync(uri, headers, 2 * 1024 * 1024,
                destination => MediaRequestHeaders.SameOrigin(uri, destination), deadline.Token).ConfigureAwait(false);
            var text = SourceHttpClient.DecodeText(payload.Content);
            var parsed = ParseManifest(text);
            _cachedManifest = parsed;
            _manifestFetchedAt = DateTimeOffset.UtcNow;
            var temporary = _manifestCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(_packsRoot);
                await File.WriteAllTextAsync(temporary, text, Encoding.UTF8, token).ConfigureAwait(false);
                File.Move(temporary, _manifestCachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Diagnostics.Log($"[RepoPack] Loaded origin manifest ({parsed.Packs.Count} packs)");
            return parsed;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            if (File.Exists(_manifestCachePath))
            {
                try
                {
                    var cachedText = await File.ReadAllTextAsync(_manifestCachePath, token).ConfigureAwait(false);
                    _cachedManifest = ParseManifest(cachedText);
                    return _cachedManifest;
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
            throw new InvalidOperationException("仓库资源包清单获取失败：" + ex.Message, ex);
        }
    }

    public async Task<RepoPackManifestEntry?> FindMatchingPackAsync(
        string? repoUrl,
        ConfigDocument? document = null,
        bool forceRefreshManifest = false,
        CancellationToken token = default)
    {
        RepoPackManifest manifest;
        try { manifest = await GetManifestAsync(forceRefreshManifest, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
        return MatchManifestEntry(manifest, repoUrl, document);
    }

    public static RepoPackManifestEntry? MatchManifestEntry(RepoPackManifest manifest, string? repoUrl, ConfigDocument? document)
    {
        string? host = null;
        if (!string.IsNullOrWhiteSpace(repoUrl) && Uri.TryCreate(repoUrl.Trim(), UriKind.Absolute, out var uri))
            host = uri.IdnHost;

        foreach (var entry in manifest.Packs)
        {
            if (!string.IsNullOrEmpty(host) && entry.MatchHosts.Any(h =>
                host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
                return entry;

            if (!string.IsNullOrWhiteSpace(repoUrl) && entry.MatchUrlKeywords.Any(k =>
                !string.IsNullOrWhiteSpace(k) && repoUrl.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return entry;
        }

        if (document is { Sites.Count: > 0 })
        {
            var apis = document.Sites.Select(s => s.Api).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Packs)
            {
                if (entry.MatchSiteApis.Count >= 2 &&
                    entry.MatchSiteApis.Count(a => apis.Contains(a)) >= Math.Min(2, entry.MatchSiteApis.Count))
                    return entry;
            }
        }
        return null;
    }

    public async Task<RepoPackAutoInstallResult> AutoInstallForRepositoryAsync(
        string repoUrl,
        ConfigDocument? document,
        IProgress<GitHubDownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        var entry = await FindMatchingPackAsync(repoUrl, document, forceRefreshManifest: false, token).ConfigureAwait(false);
        if (entry is null)
            return new RepoPackAutoInstallResult(false, false, null, null, null, string.Empty);

        if (_installed.TryGetValue(entry.Id, out var existing) &&
            !IsNewerVersion(entry.Version, existing.Definition.Version))
        {
            return new RepoPackAutoInstallResult(true, false, entry.Name, existing.Definition.Version, "已安装",
                $"已启用匹配的仓库资源包「{entry.Name}」v{existing.Definition.Version}。");
        }

        try
        {
            var route = await InstallPackAsync(entry, progress, token).ConfigureAwait(false);
            return new RepoPackAutoInstallResult(true, true, entry.Name, entry.Version, route,
                $"已自动下载并启用资源包「{entry.Name}」v{entry.Version}（{route}）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"[RepoPack] Auto-install for {entry.Id} failed: {ex.Message}");
            return new RepoPackAutoInstallResult(true, false, entry.Name, entry.Version, null,
                $"检测到专属资源包「{entry.Name}」，但自动下载失败（{ex.Message}），可在设置 → 片源 → 仓库资源包中手动下载。");
        }
    }

    public async Task<string> InstallPackAsync(
        RepoPackManifestEntry entry,
        IProgress<GitHubDownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        ValidatePackId(entry.Id);
        if (!Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var dlUri) || dlUri.UserInfo.Length != 0 ||
            dlUri.Scheme is not ("https" or "http") ||
            (dlUri.Scheme == "http" && !dlUri.IsLoopback))
            throw new InvalidDataException("资源包下载地址不受信任。");
        if (!GitHubMirrorService.IsSha256Hex(entry.Sha256))
            throw new InvalidDataException("资源包清单缺少有效的 SHA-256 校验值。");

        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_packsRoot);
            var tempZip = Path.Combine(_packsRoot, $"{entry.Id}-{Guid.NewGuid():N}.zip");
            var stagingDir = Path.Combine(_packsRoot, $".staging-{entry.Id}-{Guid.NewGuid():N}");
            var targetDir = Path.Combine(_packsRoot, entry.Id);
            try
            {
                var routeLabel = await GitHubMirrorService.DownloadVerifiedFileAsync(
                    _downloads, entry.DownloadUrl, tempZip, entry.Sha256,
                    MaxPackZipBytes, progress, token).ConfigureAwait(false);

                Directory.CreateDirectory(stagingDir);
                ExtractZipSafely(tempZip, stagingDir);

                var packJsonPath = Path.Combine(stagingDir, "pack.json");
                if (!File.Exists(packJsonPath))
                    throw new InvalidDataException("资源包缺少 pack.json 描述文件。");
                var def = ParsePackDefinition(await File.ReadAllTextAsync(packJsonPath, token).ConfigureAwait(false));
                if (string.IsNullOrWhiteSpace(def.Id)) def.Id = entry.Id;
                if (!def.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(def.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("资源包标识或版本与清单不一致。");
                token.ThrowIfCancellationRequested();

                // Atomic swap into targetDir
                var backupDir = targetDir + ".bak";
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
                if (Directory.Exists(targetDir)) Directory.Move(targetDir, backupDir);
                try
                {
                    Directory.Move(stagingDir, targetDir);
                    if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
                }
                catch
                {
                    if (!Directory.Exists(targetDir) && Directory.Exists(backupDir))
                        Directory.Move(backupDir, targetDir);
                    throw;
                }

                ReloadInstalledPacks();
                Changed?.Invoke(this, EventArgs.Empty);
                return routeLabel;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
            }
        }
        finally { _ioGate.Release(); }
    }

    public async Task RemovePackAsync(string packId, CancellationToken token = default)
    {
        ValidatePackId(packId);
        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var targetDir = Path.Combine(_packsRoot, packId);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            ReloadInstalledPacks();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _ioGate.Release(); }
    }

    public void ReloadInstalledPacks()
    {
        var loaded = new Dictionary<string, InstalledRepoPack>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_packsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(_packsRoot).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                var jsonPath = Path.Combine(dir, "pack.json");
                if (!File.Exists(jsonPath)) continue;
                try
                {
                    var def = ParsePackDefinition(File.ReadAllText(jsonPath, Encoding.UTF8));
                    if (string.IsNullOrWhiteSpace(def.Id)) def.Id = name;
                    if (def.Id.Equals(name, StringComparison.OrdinalIgnoreCase))
                        loaded[def.Id] = new InstalledRepoPack(def, dir, File.GetLastWriteTimeUtc(jsonPath));
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"[RepoPack] Failed to load installed pack '{name}': {ex.Message}");
                }
            }
        }
        _installed = loaded;
        Spider.SpiderCatalog.SetPackApis(loaded.Values.SelectMany(pack => pack.Definition.Sites.Select(rule => rule.Api)));
    }

    public static bool IsNewerVersion(string remoteVersion, string installedVersion)
    {
        if (Version.TryParse(remoteVersion.TrimStart('v', 'V'), out var r) &&
            Version.TryParse(installedVersion.TrimStart('v', 'V'), out var i))
            return r > i;
        return !string.Equals(remoteVersion.Trim(), installedVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static void ExtractZipSafely(string zipPath, string destinationDir)
    {
        var fullDest = Path.GetFullPath(destinationDir);
        if (!fullDest.EndsWith(Path.DirectorySeparatorChar))
            fullDest += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        long totalUncompressed = 0;
        foreach (var entry in archive.Entries)
        {
            var destPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
            if (!destPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"资源包包含非法路径（已阻止 Zip-Slip）：{entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            totalUncompressed += entry.Length;
            if (totalUncompressed > 64 * 1024 * 1024)
                throw new InvalidDataException("资源包解压体积超过安全上限。");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static RepoPackManifest ParseManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<RepoPackManifest>(json.TrimStart('\uFEFF'), JsonUtil.Options)
            ?? throw new InvalidDataException("资源包清单为空。");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("资源包清单版本不受支持。");
        manifest.Packs ??= [];
        manifest.Packs.RemoveAll(static entry => entry is null);
        foreach (var entry in manifest.Packs)
        {
            entry.MatchHosts ??= [];
            entry.MatchUrlKeywords ??= [];
            entry.MatchSiteApis ??= [];
        }
        return manifest;
    }

    private static RepoPackDefinition ParsePackDefinition(string json)
    {
        var def = JsonSerializer.Deserialize<RepoPackDefinition>(json.TrimStart('\uFEFF'), JsonUtil.Options)
            ?? throw new InvalidDataException("资源包 pack.json 为空。");
        def.Sites ??= [];
        def.Sites.RemoveAll(static rule => rule is null);
        def.Lives ??= [];
        return def;
    }

    private static void ValidatePackId(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId) || packId.Length > 64 ||
            !packId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("资源包标识无效。", nameof(packId));
    }

    public void Dispose()
    {
        _downloads.Dispose();
        _ioGate.Dispose();
    }
}
