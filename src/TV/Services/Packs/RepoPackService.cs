using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private const long MaxPackZipBytes = 32 * 1024 * 1024;
    private static readonly Lazy<RepoPackManifest> DefaultCatalog = new(() =>
    {
        using var stream = typeof(RepoPackService).Assembly.GetManifestResourceStream("TV.RepoPacks.Manifest")
            ?? throw new InvalidDataException("随包资源清单缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return ParseManifest(reader.ReadToEnd());
    });

    private readonly SourceHttpClient _http;
    private readonly HttpClient _downloads;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly string _packsRoot;
    private readonly string _manifestUrl;
    private readonly string _manifestCachePath;
    private readonly bool _useBundledCatalog;
    private readonly SourceHttpClient? _systemCatalogHttp;

    private volatile RepoPackCatalog? _catalog;
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
        _manifestUrl = _manifestUrl.Trim();
        _useBundledCatalog = string.Equals(_manifestUrl, DefaultManifestUrl, StringComparison.Ordinal);
        var cacheName = _useBundledCatalog ? "manifest-origin-cache.json" :
            "manifest-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_manifestUrl)))[..24] + ".json";
        _manifestCachePath = Path.Combine(_packsRoot, cacheName);

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

    internal RepoPackService(SourceHttpClient http, DnsResolverService dns, SourceHttpClient systemCatalogHttp)
        : this(http, dns) => _systemCatalogHttp = systemCatalogHttp;

    public event EventHandler? Changed;

    public IReadOnlyCollection<InstalledRepoPack> InstalledPacks => _installed.Values.ToArray();

    public bool TryGetInstalled(string packId, out InstalledRepoPack pack) =>
        _installed.TryGetValue(packId, out pack!);

    public bool TryFindRule(SourceSite site, bool overrideUniversalOnly, out InstalledRepoPack pack, out RepoPackSiteRule rule)
    {
        (InstalledRepoPack Pack, RepoPackSiteRule Rule)? apiMatch = null;
        foreach (var installed in _installed.Values)
        {
            if (installed.Definition.RestrictToRepository && !MatchesHost(site.RepositoryUrl, installed.Definition.MatchHosts) &&
                !MatchesHost(site.RepositoryGroupUrl, installed.Definition.MatchHosts)) continue;
            foreach (var candidate in installed.Definition.Sites)
            {
                if (overrideUniversalOnly && !candidate.OverrideUniversal) continue;
                if (installed.Definition.RestrictToRepository && candidate.Api.Length > 0 &&
                    !candidate.Api.Equals(site.Api, StringComparison.OrdinalIgnoreCase)) continue;
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

    public async Task<RepoPackManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken token = default) =>
        (await GetCatalogAsync(forceRefresh, token).ConfigureAwait(false)).Manifest;

    internal async Task<RepoPackCatalog?> GetLocalCatalogAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_catalog is { } inMemory) return inMemory;
        return await ReadCatalogCacheAsync(token).ConfigureAwait(false) ?? BundledCatalog();
    }

    internal async Task<RepoPackCatalog> GetCatalogAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var observed = _catalog;
        if (!forceRefresh && IsCurrent(observed)) return observed!;

        await _catalogGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_catalog is { } latest && (!ReferenceEquals(observed, latest) || !forceRefresh && IsCurrent(latest)))
                return latest;

            if (!Uri.TryCreate(_manifestUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                uri.UserInfo.Length != 0)
                throw new InvalidOperationException("资源包清单地址无效。");

            try
            {
                var (manifest, json) = await FetchManifestAsync(uri, token).ConfigureAwait(false);
                var catalog = new RepoPackCatalog(manifest, RepoPackCatalogSource.Online, DateTimeOffset.UtcNow);
                _catalog = catalog;
                await SaveCatalogCacheAsync(json, token).ConfigureAwait(false);
                Diagnostics.Log($"[RepoPack] Loaded origin manifest ({manifest.Packs.Count} packs)");
                return catalog;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsCatalogFailure(ex))
            {
                var fallback = _catalog is { Source: not RepoPackCatalogSource.Bundled } previous
                    ? previous with { Source = RepoPackCatalogSource.Cached }
                    : await ReadCatalogCacheAsync(token).ConfigureAwait(false) ?? BundledCatalog();
                if (fallback is not null)
                {
                    Diagnostics.Log($"[RepoPack] Using {fallback.Source} manifest after {ex.GetType().Name}");
                    return _catalog = fallback with { CheckedAt = DateTimeOffset.UtcNow };
                }
                var reason = ex is OperationCanceledException or TimeoutException
                    ? "连接清单服务器超时。可以稍后刷新或导入本地 ZIP。"
                    : "暂时无法连接或读取清单。可以检查清单地址或导入本地 ZIP。";
                throw new InvalidOperationException(reason, ex);
            }
        }
        finally { _catalogGate.Release(); }
    }

    private static bool IsCurrent(RepoPackCatalog? catalog) => catalog is not null &&
        DateTimeOffset.UtcNow - catalog.CheckedAt < (catalog.Source == RepoPackCatalogSource.Online
            ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));

    private RepoPackCatalog? BundledCatalog() => _useBundledCatalog
        ? new RepoPackCatalog(DefaultCatalog.Value, RepoPackCatalogSource.Bundled, DateTimeOffset.MinValue)
        : null;

    private async Task<RepoPackCatalog?> ReadCatalogCacheAsync(CancellationToken token)
    {
        try
        {
            var file = new FileInfo(_manifestCachePath);
            if (!file.Exists || file.Length > MaxManifestBytes) return null;
            var json = await File.ReadAllTextAsync(file.FullName, token).ConfigureAwait(false);
            return new RepoPackCatalog(ParseManifest(json), RepoPackCatalogSource.Cached, file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (IsCatalogFailure(ex) && ex is not OperationCanceledException) { return null; }
    }

    private async Task SaveCatalogCacheAsync(string json, CancellationToken token)
    {
        var temporary = _manifestCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_packsRoot);
            await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, token).ConfigureAwait(false);
            File.Move(temporary, _manifestCachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"[RepoPack] Could not save the online catalog: {ex.GetType().Name}");
        }
        finally { TryDeleteFile(temporary); }
    }

    private static bool IsCatalogFailure(Exception error) => error is
        OperationCanceledException or TimeoutException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException;

    private async Task<(RepoPackManifest Manifest, string Json)> FetchManifestAsync(Uri origin, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var operationToken = stop.Token;
        var candidates = new List<Task<(RepoPackManifest, string)>> { FetchAsync(_http, origin, false, TimeSpan.Zero) };
        if (_useBundledCatalog)
        {
            var api = new Uri("https://api.github.com/repos/" + DefaultRepository + "/contents/manifest.json?ref=main");
            candidates.Add(FetchAsync(_systemCatalogHttp ?? _http, api, true, TimeSpan.FromMilliseconds(350)));
        }

        Exception? lastError = null;
        try
        {
            while (candidates.Count > 0)
            {
                var result = await Task.WhenAny(candidates).WaitAsync(token).ConfigureAwait(false);
                candidates.Remove(result);
                try { return await result.ConfigureAwait(false); }
                catch (Exception ex) when (!token.IsCancellationRequested && IsCatalogFailure(ex)) { lastError = ex; }
            }
            throw lastError ?? new InvalidDataException("未收到有效的资源包清单。");
        }
        finally
        {
            stop.Cancel();
            foreach (var pending in candidates) _ = ObserveCatalogFailureAsync(pending);
        }

        async Task<(RepoPackManifest, string)> FetchAsync(SourceHttpClient client, Uri address, bool api, TimeSpan delay)
        {
            await Task.Delay(delay, operationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = "Liuguang-Cinema/RepoPack",
                ["Accept"] = api ? "application/vnd.github.raw+json" : "application/json"
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            deadline.CancelAfter(_useBundledCatalog ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(15));
            var payload = await client.GetTrustedBytesAsync(address, headers, MaxManifestBytes,
                destination => MediaRequestHeaders.SameOrigin(address, destination), deadline.Token).ConfigureAwait(false);
            var json = SourceHttpClient.DecodeText(payload.Content);
            if (api) json = UnwrapGitHubContents(json);
            operationToken.ThrowIfCancellationRequested();
            return (ParseManifest(json), json);
        }
    }

    private static async Task ObserveCatalogFailureAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) when (IsCatalogFailure(ex)) { }
    }

    private static string UnwrapGitHubContents(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("encoding", out var encoding) && encoding.ValueKind == JsonValueKind.String && encoding.ValueEquals("base64") &&
            root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return SourceHttpClient.DecodeText(Convert.FromBase64String(content.GetString()!));
        return json;
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
        foreach (var entry in manifest.Packs)
        {
            if (MatchesHost(repoUrl, entry.MatchHosts)) return entry;

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

    private static bool MatchesHost(string? repositoryUrl, IEnumerable<string> hosts)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository)) return false;
        var host = repository.IdnHost;
        return hosts.Any(value => Uri.TryCreate("https://" + value.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.AbsolutePath == "/" && parsed.UserInfo.Length == 0 &&
            (host.Equals(parsed.IdnHost, StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith("." + parsed.IdnHost, StringComparison.OrdinalIgnoreCase)));
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

    internal async Task<bool> InstallMigratedFantaitangPackAsync(string repositoryUrl, CancellationToken token)
    {
        const string id = "fantaitang";
        if (_installed.ContainsKey(id)) return true;
        var manifest = await GetManifestAsync(false, token).ConfigureAwait(false);
        if (!manifest.Packs.Any(pack => pack.Id == id)) manifest = await GetManifestAsync(true, token).ConfigureAwait(false);
        var entry = MatchManifestEntry(new RepoPackManifest { Packs = manifest.Packs.Where(pack => pack.Id == id).ToList() }, repositoryUrl, null);
        if (entry is null) return false;
        await InstallPackAsync(entry, null, token).ConfigureAwait(false);
        return true;
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
            try
            {
                var routeLabel = await TryCopyBundledZipAsync(entry, tempZip, token).ConfigureAwait(false)
                    ? "客户端自带资源包"
                    : await GitHubMirrorService.DownloadVerifiedFileAsync(_downloads, entry.DownloadUrl, tempZip, entry.Sha256,
                        MaxPackZipBytes, progress, token).ConfigureAwait(false);
                await InstallArchiveAsync(tempZip, entry, token).ConfigureAwait(false);
                return routeLabel;
            }
            finally { TryDeleteFile(tempZip); }
        }
        finally { _ioGate.Release(); }
    }

    private async Task<bool> TryCopyBundledZipAsync(RepoPackManifestEntry entry, string destination, CancellationToken token)
    {
        if (!_useBundledCatalog || !entry.Id.Equals("y456y", StringComparison.OrdinalIgnoreCase) ||
            !DefaultCatalog.Value.Packs.Any(pack => pack.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) &&
                pack.Version == entry.Version && pack.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))) return false;
        var assembly = typeof(RepoPackService).Assembly;
        await using var source = assembly.GetManifestResourceStream("TV.RepoPacks.Y456y");
        if (source is null) return false;
        await using (var output = File.Create(destination))
            await source.CopyToAsync(output, token).ConfigureAwait(false);
        if (string.Equals(await GitHubMirrorService.ComputeFileSha256Async(destination, token).ConfigureAwait(false),
            entry.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        TryDeleteFile(destination);
        Diagnostics.Log("[RepoPack] Bundled ZIP digest mismatch");
        return false;
    }

    public async Task<RepoPackDefinition> ImportPackAsync(string path, CancellationToken token = default)
    {
        var zip = new FileInfo(path);
        if (!zip.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || zip.Length > MaxPackZipBytes)
            throw new InvalidDataException("请选择不超过 32 MiB 的 ZIP 资源包。");
        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_packsRoot);
            return await InstallArchiveAsync(zip.FullName, null, token).ConfigureAwait(false);
        }
        finally { _ioGate.Release(); }
    }

    private async Task<RepoPackDefinition> InstallArchiveAsync(string archive, RepoPackManifestEntry? entry, CancellationToken token)
    {
        var stagingDir = Path.Combine(_packsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDir);
            await Task.Run(() => ExtractZipSafely(archive, stagingDir), token).ConfigureAwait(false);
            var packJsonPath = Path.Combine(stagingDir, "pack.json");
            if (!File.Exists(packJsonPath)) throw new InvalidDataException("资源包缺少根目录的 pack.json 描述文件。");
            var def = ParsePackDefinition(await File.ReadAllTextAsync(packJsonPath, token).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(def.Id) && entry is not null) def.Id = entry.Id;
            ValidatePackId(def.Id);
            if (!Version.TryParse(def.Version?.TrimStart('v', 'V'), out _))
                throw new InvalidDataException("资源包需要有效的版本号。");
            if (entry is not null && (!def.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(def.Version, entry.Version, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("资源包标识或版本与清单不一致。");
            ValidatePackRules(def, stagingDir);
            token.ThrowIfCancellationRequested();

            var targetDir = Path.Combine(_packsRoot, def.Id);
            var backupDir = targetDir + ".bak";
            if (!Directory.Exists(targetDir) && Directory.Exists(backupDir))
                Directory.Move(backupDir, targetDir);
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            if (Directory.Exists(targetDir)) Directory.Move(targetDir, backupDir);
            try { Directory.Move(stagingDir, targetDir); }
            catch
            {
                if (Directory.Exists(backupDir)) Directory.Move(backupDir, targetDir);
                throw;
            }
            TryDeleteDirectory(backupDir);
            ReloadInstalledPacks();
            Changed?.Invoke(this, EventArgs.Empty);
            return def;
        }
        finally { TryDeleteDirectory(stagingDir); }
    }

    private static void ValidatePackRules(RepoPackDefinition definition, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        foreach (var rule in definition.Sites)
        {
            if (string.IsNullOrWhiteSpace(rule.Key) && string.IsNullOrWhiteSpace(rule.Api))
                throw new InvalidDataException("资源包规则缺少站点 key 或 api。");
            switch (rule.Kind?.ToLowerInvariant())
            {
                case "js":
                    var file = Path.GetFullPath(Path.Combine(root, rule.ScriptFile ?? ""));
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        !Path.GetExtension(file).Equals(".js", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                        throw new InvalidDataException("资源包规则引用的 JavaScript 文件无效或不存在。");
                    break;
                case "alias":
                case "cms":
                case "bili-preset":
                case "info":
                    break;
                default:
                    throw new InvalidDataException("资源包包含不支持的规则类型。");
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        if (archive.Entries.Count > 1024) throw new InvalidDataException("资源包文件数量超过上限。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        foreach (var entry in archive.Entries)
        {
            var destPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
            if (!destPath.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"资源包包含非法路径（已阻止 Zip-Slip）：{entry.FullName}");
            if (entry.FullName.Contains(':') || !seen.Add(Path.TrimEndingDirectorySeparator(destPath)))
                throw new InvalidDataException("资源包包含重复或非法路径。");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }
            if (Path.GetExtension(entry.Name).ToLowerInvariant() is not (".json" or ".js" or ".md" or ".txt" or ""))
                throw new InvalidDataException("资源包只允许规则、JavaScript 和说明文本。");

            totalUncompressed += entry.Length;
            if (totalUncompressed > 64 * 1024 * 1024)
                throw new InvalidDataException("资源包解压体积超过安全上限。");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: false);
        }
    }

    private static RepoPackManifest ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("packs", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("资源包清单缺少 packs 列表。");
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("资源包清单版本不受支持。");
        var manifest = document.RootElement.Deserialize<RepoPackManifest>(JsonUtil.Options)
            ?? throw new InvalidDataException("资源包清单为空。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Packs)
        {
            if (entry is null || !IsValidPackId(entry.Id) || !seen.Add(entry.Id) ||
                !Version.TryParse(entry.Version?.TrimStart('v', 'V'), out _) ||
                !GitHubMirrorService.IsSha256Hex(entry.Sha256) ||
                !Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var url) || !MediaRequestHeaders.IsHttpAddress(url) ||
                (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback))
                throw new InvalidDataException("资源包清单包含无效或重复的包。");
            if (string.IsNullOrWhiteSpace(entry.Name)) entry.Name = entry.Id;
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

    private static bool IsValidPackId(string? packId) => !string.IsNullOrWhiteSpace(packId) && packId.Length <= 64 &&
        packId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void ValidatePackId(string packId)
    {
        if (!IsValidPackId(packId))
            throw new ArgumentException("资源包标识无效。", nameof(packId));
    }

    public void Dispose()
    {
        _systemCatalogHttp?.Dispose();
        _downloads.Dispose();
        _ioGate.Dispose();
        _catalogGate.Dispose();
    }
}
