using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Configuration;

public sealed partial class SourceConfigService : IDisposable
{
    public const string BuiltInRepositoryId = "builtin";
    public const string BuiltInRepositoryName = "内置仓库";
    private const string SettingActiveRepo = "source.activeRepo";

    public static readonly string[] BuiltInEndpoints =
    [
        "http://www.饭太硬.cc/tv",
        "http://www.饭太硬.net/tv",
        "http://fty.888484.xyz/tv"
    ];

    private readonly SourceHttpClient _http;
    private readonly DatabaseService _database;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private ConfigDocument? _builtIn;
    private string _builtInUrl = "尚未加载";
    private List<ImportedRepository>? _imports;
    private string _activeRepositoryId = BuiltInRepositoryId;
    private bool _activeRepoLoaded;

    public SourceConfigService(SourceHttpClient http, DatabaseService database)
    {
        _http = http;
        _database = database;
    }

    public ConfigDocument? Current { get; private set; }
    public SourceRepository? ActiveRepository { get; private set; }
    public IReadOnlyList<SourceRepository> Repositories { get; private set; } = [];
    public string ActiveRepositoryId => _activeRepositoryId;
    public string ActiveUrl { get; private set; } = "尚未加载";
    public string LastError { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<ImportedRepository>> GetImportsAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadImportsAsync(cancellationToken).ConfigureAwait(false);
            return _imports!.ToArray();
        }
        finally { _loadLock.Release(); }
    }

    public async Task<IReadOnlyList<SourceRepository>> GetRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadImportsAsync(cancellationToken).ConfigureAwait(false);
            return BuildRepositoryList();
        }
        finally { _loadLock.Release(); }
    }

    public async Task SelectRepositoryAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadImportsAsync(cancellationToken).ConfigureAwait(false);
            var repos = BuildRepositoryList();
            var target = repos.FirstOrDefault(r => r.Id.Equals(repositoryId, StringComparison.OrdinalIgnoreCase))
                ?? repos.FirstOrDefault()
                ?? throw new InvalidOperationException("没有可切换的片源仓库。");
            _activeRepositoryId = target.Id;
            _activeRepoLoaded = true;
            await _database.SetSettingAsync(SettingActiveRepo, _activeRepositoryId, cancellationToken).ConfigureAwait(false);
            PublishCurrent();
        }
        finally { _loadLock.Release(); }
    }

    public async Task ImportAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new ArgumentException("请输入有效的 HTTP/HTTPS 配置地址。");
        var fetched = await FetchRepositoriesAsync(uri, cancellationToken).ConfigureAwait(false);
        string? selectRepoId = null;
        await UpdateImportsAsync(imports =>
        {
            // Remove existing entries for the same root URL or individual entry URLs
            var newUrls = fetched.Select(f => f.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            imports.RemoveAll(item =>
                string.Equals(item.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.GroupUrl, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) ||
                newUrls.Contains(item.Url));
            for (var i = fetched.Count - 1; i >= 0; i--)
                imports.Insert(0, fetched[i]);
            if (fetched.Count > 0)
                selectRepoId = ComputeRepositoryId(fetched[0].Url);
        }, selectRepoId, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveImportAsync(string url, CancellationToken cancellationToken = default) =>
        UpdateImportsAsync(imports => imports.RemoveAll(item =>
            string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)), null, cancellationToken);

    /// <summary>
    /// Looks up a site by key in the active repository first, then across all other repositories
    /// so favorites and watch history from inactive repositories remain playable.
    /// </summary>
    public SourceSite? FindSite(string? siteKey)
    {
        if (string.IsNullOrWhiteSpace(siteKey)) return null;
        var current = Current?.Sites.FirstOrDefault(s => s.Key == siteKey);
        if (current is not null) return current;
        foreach (var repo in Repositories)
        {
            var found = repo.Document.Sites.FirstOrDefault(s => s.Key == siteKey);
            if (found is not null) return found;
        }
        return null;
    }

    public IReadOnlyList<ParseSource> FindParsesForSite(string? siteKey)
    {
        if (!string.IsNullOrWhiteSpace(siteKey))
        {
            foreach (var repo in Repositories)
            {
                if (repo.Document.Sites.Any(s => s.Key == siteKey))
                    return repo.Document.Parses;
            }
        }
        return Current?.Parses ?? [];
    }

    private async Task UpdateImportsAsync(Action<List<ImportedRepository>> update, string? switchToRepoId, CancellationToken token)
    {
        await _loadLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ReadImportsAsync(token).ConfigureAwait(false);
            var imports = _imports!.ToList();
            update(imports);
            await _database.SetSettingAsync("source.imports", JsonSerializer.Serialize(imports, JsonUtil.Options), token).ConfigureAwait(false);
            _imports = imports;
            if (!string.IsNullOrWhiteSpace(switchToRepoId))
            {
                _activeRepositoryId = switchToRepoId;
                _activeRepoLoaded = true;
                await _database.SetSettingAsync(SettingActiveRepo, _activeRepositoryId, token).ConfigureAwait(false);
            }
            PublishCurrent();
        }
        finally { _loadLock.Release(); }
    }

    private async Task ReadImportsAsync(CancellationToken token)
    {
        if (!_activeRepoLoaded)
        {
            var savedRepo = await _database.GetSettingAsync(SettingActiveRepo, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(savedRepo)) _activeRepositoryId = savedRepo.Trim();
            _activeRepoLoaded = true;
        }
        if (_imports is not null) return;
        var json = await _database.GetSettingAsync("source.imports", token).ConfigureAwait(false);
        if (json is not null)
        {
            try { _imports = JsonSerializer.Deserialize<List<ImportedRepository>>(json, JsonUtil.Options) ?? []; }
            catch (JsonException) { _imports = []; }
            foreach (var entry in _imports) HydrateDocument(entry.Document, isBuiltIn: false);
            return;
        }
        var previous = await _database.GetSettingAsync("source.endpoints", token).ConfigureAwait(false);
        var migrated = new List<ImportedRepository>();
        if (previous is not null)
        {
            List<string> urls;
            try { urls = JsonSerializer.Deserialize<List<string>>(previous) ?? []; }
            catch (JsonException) { urls = []; }
            foreach (var url in urls.Except(BuiltInEndpoints, StringComparer.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                migrated.Add(new ImportedRepository(uri.AbsoluteUri, uri.Host, new ConfigDocument()));
            }
        }
        _imports = migrated;
    }

    private List<SourceRepository> BuildRepositoryList()
    {
        var builtInDoc = _builtIn ?? new ConfigDocument();
        HydrateDocument(builtInDoc, isBuiltIn: true);
        var list = new List<SourceRepository>
        {
            new(BuiltInRepositoryId, BuiltInRepositoryName, BuiltInRepositoryId, true, builtInDoc)
        };
        if (_imports is not null)
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BuiltInRepositoryId };
            foreach (var entry in _imports)
            {
                HydrateDocument(entry.Document, isBuiltIn: false);
                var id = ComputeRepositoryId(entry.Url);
                if (!seenIds.Add(id)) continue;
                var name = string.IsNullOrWhiteSpace(entry.Name) ? HostDisplayName(entry.Url) : entry.Name.Trim();
                list.Add(new SourceRepository(id, name, entry.Url, false, entry.Document, entry.GroupUrl));
            }
        }
        return list;
    }

    private void PublishCurrent()
    {
        var repos = BuildRepositoryList();
        Repositories = repos;
        var active = repos.FirstOrDefault(r => r.Id.Equals(_activeRepositoryId, StringComparison.OrdinalIgnoreCase))
            ?? repos[0];
        if (!active.Id.Equals(_activeRepositoryId, StringComparison.OrdinalIgnoreCase))
            _activeRepositoryId = active.Id;

        ActiveRepository = active;
        Current = active.Document;
        ActiveUrl = active.IsBuiltIn ? _builtInUrl : active.Url;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void HydrateDocument(ConfigDocument doc, bool isBuiltIn)
    {
        var spider = doc.Spider?.Trim() ?? string.Empty;
        foreach (var site in doc.Sites)
        {
            if (string.IsNullOrWhiteSpace(site.RepositorySpider) && spider.Length > 0)
                site.RepositorySpider = spider;
        }
        foreach (var live in doc.Lives)
            live.IsBuiltIn = isBuiltIn;
    }

    public static string ComputeRepositoryId(string url) =>
        "repo-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..12].ToLowerInvariant();

    private static string HostDisplayName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private Task? _refreshLoop;
    private readonly CancellationTokenSource _lifetime = new();

    public async Task<ConfigDocument> LoadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && _builtIn is not null && Current is not null) return Current;
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _builtIn is not null && Current is not null) return Current;
            await ReadImportsAsync(cancellationToken).ConfigureAwait(false);

            // If user explicitly forces reload while viewing an imported repository, refresh that repository directly!
            if (force && !string.Equals(_activeRepositoryId, BuiltInRepositoryId, StringComparison.OrdinalIgnoreCase) && _imports is not null)
            {
                var idx = _imports.FindIndex(item => ComputeRepositoryId(item.Url).Equals(_activeRepositoryId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && Uri.TryCreate(_imports[idx].Url, UriKind.Absolute, out var importUri))
                {
                    var (refreshedDoc, error, _) = await ReadRepositoryEntryAsync(importUri, _imports[idx].Name, cancellationToken).ConfigureAwait(false);
                    if (refreshedDoc is not null)
                    {
                        HydrateDocument(refreshedDoc, isBuiltIn: false);
                        _imports[idx] = _imports[idx] with { Document = refreshedDoc };
                        await _database.SetSettingAsync("source.imports", JsonSerializer.Serialize(_imports, JsonUtil.Options), cancellationToken).ConfigureAwait(false);
                        PublishCurrent();
                        return Current!;
                    }
                    if (!string.IsNullOrWhiteSpace(error))
                        throw new InvalidOperationException("仓库刷新失败：" + error);
                }
            }

            var autoRefresh = !string.Equals(await _database.GetSettingAsync("source.autoRefresh", cancellationToken).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);
            if ((!force || _builtIn is null) && File.Exists(AppPaths.Config))
            {
                try
                {
                    var cachedRaw = await File.ReadAllTextAsync(AppPaths.Config, cancellationToken).ConfigureAwait(false);
                    _builtIn = Parse(cachedRaw);
                    HydrateDocument(_builtIn, isBuiltIn: true);
                    _builtInUrl = "本地缓存";
                    LastError = string.Empty;
                    PublishCurrent();
                    if (!force && autoRefresh)
                    {
                        EnsurePeriodicRefresh();
                        if (await IsRefreshDueAsync(cancellationToken).ConfigureAwait(false))
                            _ = RefreshBackgroundAsync();
                    }
                    if (!force) return Current!;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Diagnostics.Log("配置缓存读取失败: " + ex.Message);
                }
            }
            try
            {
                var remote = await LoadRemoteAsync(BuiltInEndpoints, cancellationToken).ConfigureAwait(false);
                await CommitRemoteAsync(remote.Document, remote.Raw, remote.FinalUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (!force && _imports!.Count > 0) { PublishCurrent(); }
            if (autoRefresh) EnsurePeriodicRefresh();
            return Current!;
        }
        finally { _loadLock.Release(); }
    }

    private async Task<(ConfigDocument Document, string Raw, string FinalUrl)> LoadRemoteAsync(IReadOnlyList<string> endpoints, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = endpoints.Select(endpoint => FetchSafeAsync(endpoint, linked.Token)).ToList();
        var errors = new List<string>();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            var result = await completed.ConfigureAwait(false);
            if (result.Document is not null)
            {
                linked.Cancel();
                return (result.Document, result.Raw, result.FinalUrl);
            }
            errors.Add(result.Error ?? "未知错误");
        }
        LastError = string.Join("\n", errors);
        throw new InvalidOperationException("所有配置源均加载失败，请检查网络后重试。" + (errors.Count > 0 ? "\n" + LastError : string.Empty));
    }

    private async Task<(ConfigDocument? Document, string Raw, string FinalUrl, string? Error)> FetchSafeAsync(string endpoint, CancellationToken cancellationToken)
    {
        Diagnostics.Log($"Trying config endpoint: {endpoint}");
        try
        {
            var result = await FetchDocumentAsync(endpoint, 0, cancellationToken).ConfigureAwait(false);
            return (result.Document, result.Raw, result.FinalUrl, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, string.Empty, string.Empty, $"{endpoint}: 请求超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, string.Empty, string.Empty, $"{endpoint}: {ex.Message}");
        }
    }

    private async Task CommitRemoteAsync(ConfigDocument document, string raw, string finalUrl, CancellationToken cancellationToken)
    {
        if (_builtIn is not null)
        {
            var count = document.Sites.Count;
            var keys = document.Sites.Select(site => site.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var site in _builtIn.Sites.Where(site => site.IsSupported && keys.Add(site.Key))) document.Sites.Add(site);
            var keptLives = document.Lives.Count == 0 && _builtIn.Lives.Count > 0;
            if (keptLives) document.Lives.AddRange(_builtIn.Lives);
            if (document.Sites.Count != count || keptLives) raw = JsonSerializer.Serialize(document, JsonUtil.Options);
        }
        HydrateDocument(document, isBuiltIn: true);
        AppPaths.EnsureCreated();
        var temp = AppPaths.Config + ".tmp";
        await File.WriteAllTextAsync(temp, raw, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (File.Exists(AppPaths.Config)) File.Copy(AppPaths.Config, AppPaths.Config + ".bak", true);
        File.Move(temp, AppPaths.Config, true);
        await _database.SetSettingAsync("source.active", finalUrl, cancellationToken).ConfigureAwait(false);
        await _database.SetSettingAsync("source.lastRefresh", DateTimeOffset.Now.ToString("O"), cancellationToken).ConfigureAwait(false);
        _builtIn = document;
        _builtInUrl = finalUrl;
        LastError = string.Empty;
        PublishCurrent();
    }

    private async Task RefreshBackgroundAsync()
    {
        try
        {
            await _loadLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                var remote = await LoadRemoteAsync(BuiltInEndpoints, _lifetime.Token).ConfigureAwait(false);
                await CommitRemoteAsync(remote.Document, remote.Raw, remote.FinalUrl, _lifetime.Token).ConfigureAwait(false);
            }
            finally { _loadLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Log("后台配置刷新失败: " + ex.Message); }
    }

    private static readonly int[] RefreshSlots = [6, 12, 18];

    private static DateTimeOffset MostRecentSlotStart(DateTimeOffset now)
    {
        var todaySlots = RefreshSlots.Select(hour => new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset)).ToArray();
        return todaySlots.Where(slot => slot <= now).DefaultIfEmpty(todaySlots[^1].AddDays(-1)).Max();
    }

    private async Task<bool> IsRefreshDueAsync(CancellationToken token)
    {
        try
        {
            var raw = await _database.GetSettingAsync("source.lastRefresh", token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw) || !DateTimeOffset.TryParse(raw, out var last)) return true;
            return last < MostRecentSlotStart(DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Log($"配置刷新时段判断失败: {ex.Message}"); return true; }
    }

    private void EnsurePeriodicRefresh()
    {
        _refreshLoop ??= Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            try
            {
                while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    var enabled = !string.Equals(await _database.GetSettingAsync("source.autoRefresh", _lifetime.Token).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);
                    if (!enabled) continue;
                    if (!await IsRefreshDueAsync(_lifetime.Token).ConfigureAwait(false)) continue;
                    await RefreshBackgroundAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Dispose() => _lifetime.Cancel();

    /// <summary>
    /// Fetches a single TVBox repository OR expands a multi-repo (`urls` / `storeHouse`) into separate,
    /// independently selectable repositories so each depot retains its own spider JAR and site list.
    /// </summary>
    private async Task<List<ImportedRepository>> FetchRepositoriesAsync(Uri root, CancellationToken token)
    {
        var results = new List<ImportedRepository>();
        var visited = new HashSet<Uri>();
        var pending = new Queue<(Uri Uri, string Name, bool IsRoot)>();
        pending.Enqueue((root, root.Host, true));
        var errors = new List<string>();

        while (pending.Count > 0 && visited.Count < 32)
        {
            var batch = new List<Task<(ConfigDocument? Document, string? Error, Uri? BaseUri, string Name, bool IsRoot)>>();
            while (pending.TryDequeue(out var entry) && visited.Count < 32)
            {
                if (visited.Add(entry.Uri))
                {
                    var current = entry;
                    batch.Add(Task.Run(async () =>
                    {
                        var (doc, err, baseUri) = await ReadRepositoryEntryAsync(current.Uri, current.Name, token).ConfigureAwait(false);
                        return (doc, err, baseUri, current.Name, current.IsRoot);
                    }, token));
                }
                if (batch.Count >= 6) break;
            }

            foreach (var (document, error, baseUri, entryName, isRoot) in await Task.WhenAll(batch).ConfigureAwait(false))
            {
                if (document is null || baseUri is null)
                {
                    if (error is not null) errors.Add(error);
                    continue;
                }

                // Expand multi-warehouse (`storeHouse`) and multi-repo (`urls`) entries
                foreach (var house in document.StoreHouse.Take(16))
                {
                    if (TryHttpUri(baseUri, house.SourceUrl) is { } subUri)
                        pending.Enqueue((subUri, string.IsNullOrWhiteSpace(house.SourceName) ? subUri.Host : house.SourceName.Trim(), false));
                }
                foreach (var depot in document.Urls.Take(32))
                {
                    if (TryHttpUri(baseUri, depot.Url) is { } subUri)
                        pending.Enqueue((subUri, string.IsNullOrWhiteSpace(depot.Name) ? subUri.Host : depot.Name.Trim(), false));
                }

                if (document.Sites.Count > 0 || document.Lives.Count > 0)
                {
                    HydrateDocument(document, isBuiltIn: false);
                    var displayName = string.IsNullOrWhiteSpace(entryName) ? baseUri.Host : entryName;
                    var groupUrl = isRoot ? null : root.AbsoluteUri;
                    results.Add(new ImportedRepository(baseUri.AbsoluteUri, displayName, document, groupUrl));
                }
            }
        }

        if (results.Count == 0)
            throw new InvalidDataException(errors.FirstOrDefault() ?? "配置中没有可用的点播或直播源。");
        if (errors.Count > 0) Diagnostics.Log($"多仓部分条目读取失败：{string.Join(" · ", errors)}");
        return results;
    }

    private async Task<(ConfigDocument? Document, string? Error, Uri? BaseUri)> ReadRepositoryEntryAsync(Uri uri, string name, CancellationToken token)
    {
        try
        {
            var payload = await _http.GetBytesAsync(uri.AbsoluteUri, maxBytes: 8 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
            ConfigDocument? document;
            try { document = Parse(DecodeConfig(payload.Content)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { document = null; }
            if (document is not null && (document.Sites.Count > 0 || document.Lives.Count > 0 || document.Urls.Count > 0 || document.StoreHouse.Count > 0))
            {
                NormalizeLinks(document, payload.FinalUri);
                return (document, null, payload.FinalUri);
            }

            var source = new LiveSource { Name = name, Url = payload.FinalUri.AbsoluteUri, IsBuiltIn = false };
            if (!Live.LiveService.IsPlaylist(payload.Content, source)) throw new InvalidDataException("地址未返回点播配置或直播列表。");
            return (new ConfigDocument { Lives = [source] }, null, payload.FinalUri);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            return (null, $"{name}：{ex.Message}", null);
        }
    }

    private static Uri? TryHttpUri(Uri parent, string? link) =>
        !string.IsNullOrWhiteSpace(link) && Uri.TryCreate(parent, link.Trim(), out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri : null;

    internal static void NormalizeLinks(ConfigDocument document, Uri parent)
    {
        if (!string.IsNullOrWhiteSpace(document.Spider))
            document.Spider = ResolveRelativeWithParams(parent, document.Spider);

        foreach (var site in document.Sites)
        {
            if (!string.IsNullOrWhiteSpace(site.Jar))
                site.Jar = ResolveRelativeWithParams(parent, site.Jar);
            if (site.Type is 0 or 1 or 4 || IsRelativeOrUrlPath(site.Api))
            {
                if (TryHttpUri(parent, site.Api) is { } api) site.Api = api.AbsoluteUri;
            }
            if (site.Ext.ValueKind == JsonValueKind.String)
            {
                var rawExt = site.Ext.GetString();
                if (!string.IsNullOrWhiteSpace(rawExt))
                {
                    var normalizedExt = ResolveRelativeSegments(parent, rawExt);
                    if (!string.Equals(normalizedExt, rawExt, StringComparison.Ordinal))
                        site.Ext = JsonSerializer.SerializeToElement(normalizedExt);
                }
            }
            if (!string.IsNullOrWhiteSpace(document.Spider) && string.IsNullOrWhiteSpace(site.RepositorySpider))
                site.RepositorySpider = document.Spider;
        }
        foreach (var live in document.Lives)
            if (TryHttpUri(parent, live.Url) is { } url) live.Url = url.AbsoluteUri;
        foreach (var parser in document.Parses)
            if ((parser.Url.StartsWith('/') || parser.Url.StartsWith("./", StringComparison.Ordinal) || parser.Url.StartsWith("../", StringComparison.Ordinal))
                && TryHttpUri(parent, parser.Url) is { } url)
                parser.Url = url.AbsoluteUri;
    }

    private static bool IsRelativeOrUrlPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return v.StartsWith("./", StringComparison.Ordinal) ||
               v.StartsWith("../", StringComparison.Ordinal) ||
               v.StartsWith('/') ||
               v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               v.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRelativeWithParams(Uri parent, string raw)
    {
        var trimmed = raw.Trim();
        var semi = trimmed.IndexOf(';');
        var urlPart = semi >= 0 ? trimmed[..semi].Trim() : trimmed;
        var suffix = semi >= 0 ? trimmed[semi..] : string.Empty;
        if (IsRelativeOrUrlPath(urlPart) && TryHttpUri(parent, urlPart) is { } resolved)
            return resolved.AbsoluteUri + suffix;
        return trimmed;
    }

    private static string ResolveRelativeSegments(Uri parent, string raw)
    {
        if (!raw.Contains("./", StringComparison.Ordinal) && !raw.Contains("../", StringComparison.Ordinal))
            return raw;
        if (raw.Contains("$$$", StringComparison.Ordinal))
        {
            var parts = raw.Split("$$$");
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                if ((p.StartsWith("./", StringComparison.Ordinal) || p.StartsWith("../", StringComparison.Ordinal)) && TryHttpUri(parent, p) is { } u)
                    parts[i] = u.AbsoluteUri;
            }
            return string.Join("$$$", parts);
        }
        var trimmed = raw.Trim();
        if ((trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal)) && TryHttpUri(parent, trimmed) is { } single)
            return single.AbsoluteUri;
        return raw;
    }

    private async Task<(ConfigDocument Document, string Raw, string FinalUrl)> FetchDocumentAsync(string url, int depth, CancellationToken cancellationToken)
    {
        if (depth > 3) throw new InvalidDataException("配置分发层级过深。");
        var payload = await _http.GetBytesAsync(url, maxBytes: 8 * 1024 * 1024, cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = DecodeConfig(payload.Content);
        var document = Parse(raw);
        if (document.Urls.Count > 0)
        {
            var next = document.Urls.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Url))?.Url
                ?? throw new InvalidDataException("配置分发地址为空。");
            var absolute = new Uri(payload.FinalUri, next).ToString();
            return await FetchDocumentAsync(absolute, depth + 1, cancellationToken).ConfigureAwait(false);
        }
        if (document.Sites.Count == 0 && document.Lives.Count == 0) throw new InvalidDataException("配置中没有可用的点播或直播站点。");
        NormalizeLinks(document, payload.FinalUri);
        return (document, JsonSerializer.Serialize(document, JsonUtil.Options), payload.FinalUri.ToString());
    }

    internal static ConfigDocument Parse(string raw)
    {
        var cleaned = StripJsonCommentsAndTrailingCommas(raw);
        var document = JsonSerializer.Deserialize<ConfigDocument>(cleaned, JsonUtil.Options)
            ?? throw new InvalidDataException("配置 JSON 为空。");
        if (!string.IsNullOrWhiteSpace(document.Message)) throw new InvalidDataException(document.Message);
        document.Urls ??= [];
        document.StoreHouse ??= [];
        document.Sites ??= [];
        document.Lives ??= [];
        document.Parses ??= [];
        document.Flags ??= [];
        document.Ads ??= [];
        return document;
    }

    /// <summary>
    /// Decodes all standard TVBox configuration formats:
    /// 1. Plain JSON (with optional comments / trailing commas)
    /// 2. Image-embedded Base64 (`[A-Za-z0-9]{8}**...` or trailing `**...`)
    /// 3. Plain Base64 JSON
    /// 4. AES-128-CBC hex encrypted configs (`$#key#$` / `2423...2324` Xiaosa/TVBox format)
    /// </summary>
    internal static string DecodeConfig(byte[] bytes)
    {
        // 1. Check image-appended Base64 with 8-char** or general ** marker
        var transport = Encoding.Latin1.GetString(bytes);
        var marker = EightCharStarMarkerRegex().Match(transport);
        if (marker.Success && marker.Index + marker.Length < transport.Length)
        {
            if (TryDecodeBase64Json(transport[(marker.Index + marker.Length)..], out var decoded))
                return decoded;
        }
        var lastStars = transport.LastIndexOf("**", StringComparison.Ordinal);
        if (lastStars >= 0 && lastStars + 2 < transport.Length)
        {
            if (TryDecodeBase64Json(transport[(lastStars + 2)..], out var decoded))
                return decoded;
        }

        // 2. Check UTF-8 plain JSON, plain Base64 JSON, or AES-CBC hex ($#key#$)
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).Trim().Trim('\uFEFF');
            if (LooksLikeJsonObject(text)) return text;
            if (TryDecodeAesHexConfig(text, out var aesDecoded)) return aesDecoded;
            if (TryDecodeBase64Json(text, out var b64Decoded)) return b64Decoded;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { }

        throw new InvalidDataException("响应不是受支持的 JSON、Base64 或 TVBox 加密配置。");
    }

    private static bool LooksLikeJsonObject(string text)
    {
        var stripped = StripJsonCommentsAndTrailingCommas(text).TrimStart();
        return stripped.StartsWith('{');
    }

    private static bool TryDecodeBase64Json(string candidate, out string json)
    {
        json = string.Empty;
        try
        {
            var cleaned = candidate.Trim();
            if (cleaned.Length < 4) return false;
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cleaned)).Trim().Trim('\uFEFF');
            if (LooksLikeJsonObject(decoded))
            {
                json = decoded;
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }

    private static bool TryDecodeAesHexConfig(string raw, out string json)
    {
        json = string.Empty;
        var text = raw.Trim();
        if (text.Length < 40) return false;

        string keyStr;
        string cipherHex;
        string ivStr;

        // Format A: literal "$#<key>#$<hexCipher><13-digit-ts>" or hex-encoded "2423...2324"
        if (text.StartsWith("$#", StringComparison.Ordinal))
        {
            var endIdx = text.IndexOf("#$", 2, StringComparison.Ordinal);
            if (endIdx <= 2) return false;
            keyStr = text[2..endIdx];
            var rest = text[(endIdx + 2)..].Trim();
            if (rest.Length <= 26) return false;
            ivStr = rest[^13..];
            cipherHex = rest[..^13];
        }
        else if (text.StartsWith("2423", StringComparison.OrdinalIgnoreCase))
        {
            var endHex = text.IndexOf("2324", 4, StringComparison.OrdinalIgnoreCase);
            if (endHex <= 4 || (endHex - 4) % 2 != 0) return false;
            try
            {
                keyStr = Encoding.UTF8.GetString(Convert.FromHexString(text[4..endHex]));
                var rest = text[(endHex + 4)..].Trim();
                if (rest.Length <= 26) return false;
                ivStr = Encoding.UTF8.GetString(Convert.FromHexString(rest[^26..]));
                cipherHex = rest[..^26];
            }
            catch (FormatException) { return false; }
        }
        else
        {
            return false;
        }

        try
        {
            var keyBytes = Pad16Zero(keyStr);
            var ivBytes = Pad16Zero(ivStr);
            var cipherBytes = Convert.FromHexString(cipherHex.Trim());
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = keyBytes;
            aes.IV = ivBytes;
            var plain = aes.CreateDecryptor().TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            var decoded = Encoding.UTF8.GetString(plain).Trim().Trim('\uFEFF');
            if (LooksLikeJsonObject(decoded))
            {
                json = decoded;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static byte[] Pad16Zero(string input)
    {
        var padded = input.Length >= 16 ? input[..16] : input.PadRight(16, '0');
        return Encoding.UTF8.GetBytes(padded)[..16];
    }

    internal static string StripJsonCommentsAndTrailingCommas(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var inString = false;
        var escaped = false;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '/' && i + 1 < raw.Length)
            {
                if (raw[i + 1] == '/')
                {
                    i += 2;
                    while (i < raw.Length && raw[i] != '\n' && raw[i] != '\r') i++;
                    continue;
                }
                if (raw[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < raw.Length && !(raw[i] == '*' && raw[i + 1] == '/')) i++;
                    i = Math.Min(raw.Length, i + 2);
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }

        // Remove trailing commas before '}' or ']' outside strings
        var text = sb.ToString();
        var cleaned = new StringBuilder(text.Length);
        inString = false;
        escaped = false;
        for (var j = 0; j < text.Length; j++)
        {
            var c = text[j];
            if (inString)
            {
                cleaned.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                cleaned.Append(c);
                continue;
            }
            if (c == ',')
            {
                var k = j + 1;
                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                if (k < text.Length && (text[k] == '}' || text[k] == ']'))
                    continue;
            }
            cleaned.Append(c);
        }
        return cleaned.ToString();
    }

    [GeneratedRegex(@"[A-Za-z0-9]{8}\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex EightCharStarMarkerRegex();
}
