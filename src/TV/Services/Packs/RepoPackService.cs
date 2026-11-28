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

        if (!Uri.TryCreate(_manifestUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0)
            throw new InvalidOperationException("资源包清单地址无效。");

        try
        {
            var (parsed, text) = await FetchManifestAsync(uri, token).ConfigureAwait(false);
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
                catch (Exception cacheError) when (cacheError is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
            }
            var reason = ex is OperationCanceledException or TimeoutException
                ? "连接 GitHub 超时，请检查网络后刷新，或从本地导入 ZIP。"
                : ex.Message;
            throw new InvalidOperationException("仓库资源包清单获取失败。 " + reason, ex);
        }
    }

    private async Task<(RepoPackManifest Manifest, string Json)> FetchManifestAsync(Uri origin, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "Liuguang-Cinema/RepoPack",
            ["Accept"] = "application/json"
        };

        // Both official endpoints serve the manifest that supplies ZIP checksums.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var candidates = new List<Task<(RepoPackManifest, string)>> { FetchAsync(origin, headers, TimeSpan.Zero) };
        if (origin.AbsoluteUri.Equals(DefaultManifestUrl, StringComparison.Ordinal))
        {
            var api = new Uri("https://api.github.com/repos/" + DefaultRepository + "/contents/manifest.json?ref=main");
            candidates.Add(FetchAsync(api, new Dictionary<string, string>(headers)
            {
                ["Accept"] = "application/vnd.github.raw+json"
            }, TimeSpan.FromMilliseconds(500)));
        }

        Exception? lastError = null;
        try
        {
            while (candidates.Count > 0)
            {
                var result = await Task.WhenAny(candidates).ConfigureAwait(false);
                candidates.Remove(result);
                try { return await result.ConfigureAwait(false); }
                catch (Exception ex) when (!token.IsCancellationRequested &&
                    ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException)
                {
                    lastError = ex;
                }
            }
            throw lastError ?? new InvalidDataException("未收到有效的资源包清单。");
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(candidates).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException) { }
        }

        async Task<(RepoPackManifest, string)> FetchAsync(Uri address, Dictionary<string, string> requestHeaders, TimeSpan delay)
        {
            await Task.Delay(delay, stop.Token).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var payload = await _http.GetTrustedBytesAsync(address, requestHeaders, 2 * 1024 * 1024,
                destination => MediaRequestHeaders.SameOrigin(address, destination), deadline.Token).ConfigureAwait(false);
            var json = SourceHttpClient.DecodeText(payload.Content);
            return (ParseManifest(json), json);
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
            try
            {
                var routeLabel = await GitHubMirrorService.DownloadVerifiedFileAsync(
                    _downloads, entry.DownloadUrl, tempZip, entry.Sha256,
                    MaxPackZipBytes, progress, token).ConfigureAwait(false);
                await InstallArchiveAsync(tempZip, entry, token).ConfigureAwait(false);
                return routeLabel;
            }
            finally { TryDeleteFile(tempZip); }
        }
        finally { _ioGate.Release(); }
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
