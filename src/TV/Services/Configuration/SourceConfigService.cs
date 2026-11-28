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
    private static readonly HashSet<string> LinkExtensionFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "site", "sites", "siteUrl", "siteUrls", "host", "hosts", "url", "urls", "baseUrl", "homeUrl", "endpoint"
    };
    private const string LegacyRepositoryId = "builtin";
    public const string EmptyRepositoryName = "尚未导入仓库";
    private const string SettingActiveRepo = "source.activeRepo";
    private const string SettingImports = "source.imports";
    private const string SettingLegacyMigration = "source.fantaitangMigration";
    private const string SettingPendingPackUrl = "source.fantaitangPackPending";
    private static readonly string[] LegacyEndpoints =
    [
        "http://www.饭太硬.cc/tv",
        "http://www.饭太硬.net/tv",
        "http://fty.888484.xyz/tv"
    ];

    private readonly SourceHttpClient _http;
    private readonly DatabaseService _database;
    private readonly Func<string, CancellationToken, Task<bool>>? _installMigratedPack;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private List<ImportedRepository>? _imports;
    private string _activeRepositoryId = string.Empty;

    public SourceConfigService(SourceHttpClient http, DatabaseService database) : this(http, database, null) { }

    internal SourceConfigService(SourceHttpClient http, DatabaseService database,
        Func<string, CancellationToken, Task<bool>>? installMigratedPack)
    {
        _http = http;
        _database = database;
        _installMigratedPack = installMigratedPack;
    }

    public ConfigDocument? Current { get; private set; }
    public SourceRepository? ActiveRepository { get; private set; }
    public IReadOnlyList<SourceRepository> Repositories { get; private set; } = [];
    public string ActiveRepositoryId => _activeRepositoryId;
    public string ActiveUrl { get; private set; } = "尚未加载";
    /// <summary>最近一次刷新的提示（主地址失效回退备用地址等）；设置页读取后展示给用户。</summary>
    public string? LastRefreshNotice { get; private set; }
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
                ?? throw new InvalidOperationException("没有可切换的片源仓库。");
            await _database.SetSettingAsync(SettingActiveRepo, target.Id, cancellationToken).ConfigureAwait(false);
            _activeRepositoryId = target.Id;
            PublishCurrent();
        }
        finally { _loadLock.Release(); }
    }

    public Task ImportAsync(string url, CancellationToken cancellationToken = default) =>
        ImportAsync(url, null, cancellationToken);

    public async Task ImportAsync(string url, string? customName, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new ArgumentException("请输入有效的 HTTP/HTTPS 配置地址。");
        var fetched = await FetchRepositoriesAsync(uri, cancellationToken).ConfigureAwait(false);
        var trimmedCustomName = customName?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedCustomName) && fetched.Count > 0)
        {
            if (fetched.Count == 1)
            {
                fetched[0] = fetched[0] with { Name = trimmedCustomName };
            }
            else
            {
                for (var i = 0; i < fetched.Count; i++)
                {
                    var suffix = string.IsNullOrWhiteSpace(fetched[i].Name) ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : fetched[i].Name;
                    fetched[i] = fetched[i] with { Name = $"{trimmedCustomName} · {suffix}" };
                }
            }
        }
        var selectRepoId = fetched.Count > 0 ? ComputeRepositoryId(fetched[0].Url) : null;
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
        }, selectRepoId, cancellationToken).ConfigureAwait(false);
    }

    public Task RenameImportAsync(string url, string newName, CancellationToken cancellationToken = default)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("仓库名称不能为空。", nameof(newName));
        if (trimmed.Length > 60)
            trimmed = trimmed[..60];
        return UpdateImportsAsync(imports =>
        {
            var idx = imports.FindIndex(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                imports[idx] = imports[idx] with { Name = trimmed };
        }, null, cancellationToken);
    }

    public Task RemoveImportAsync(string url, CancellationToken cancellationToken = default) =>
        UpdateImportsAsync(imports => imports.RemoveAll(item =>
            string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)), null, cancellationToken);

    /// <summary>为导入仓库设置或清除备用地址；主地址刷新失败时自动回退到备用地址并在 LastRefreshNotice 中提示。</summary>
    public Task SetBackupUrlAsync(string url, string? backupUrl, CancellationToken cancellationToken = default)
    {
        string? normalized;
        if (string.IsNullOrWhiteSpace(backupUrl))
        {
            normalized = null;
        }
        else
        {
            var trimmed = backupUrl.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var backup) || backup.Scheme is not ("http" or "https") || backup.UserInfo.Length > 0)
                throw new ArgumentException("备用地址必须是有效的 HTTP/HTTPS 配置地址。", nameof(backupUrl));
            if (string.Equals(trimmed, url, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("备用地址不能与主地址相同。", nameof(backupUrl));
            normalized = backup.AbsoluteUri;
        }
        return UpdateImportsAsync(imports =>
        {
            var idx = imports.FindIndex(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new InvalidOperationException("仓库不存在或已被移除。");
            imports[idx] = imports[idx] with { BackupUrl = normalized };
        }, null, cancellationToken);
    }

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
            if (Current?.Sites.Any(site => site.Key == siteKey) == true) return Current.Parses;
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
            var nextId = switchToRepoId ?? (imports.Any(entry => ComputeRepositoryId(entry.Url) == _activeRepositoryId)
                ? _activeRepositoryId : imports.Count > 0 ? ComputeRepositoryId(imports[0].Url) : string.Empty);
            await _database.SetSettingsAsync([(SettingImports, JsonSerializer.Serialize(imports, JsonUtil.Options)),
                (SettingActiveRepo, nextId)], token).ConfigureAwait(false);
            _imports = imports;
            _activeRepositoryId = nextId;
            PublishCurrent();
        }
        finally { _loadLock.Release(); }
    }

    private async Task ReadImportsAsync(CancellationToken token)
    {
        if (_imports is not null) return;
        var activeId = (await _database.GetSettingAsync(SettingActiveRepo, token).ConfigureAwait(false))?.Trim() ?? "";
        var raw = await _database.GetSettingAsync(SettingImports, token).ConfigureAwait(false);
        List<ImportedRepository> imports;
        try { imports = raw is null ? [] : JsonSerializer.Deserialize<List<ImportedRepository>>(raw, JsonUtil.Options) ?? []; }
        catch (JsonException) { imports = []; }

        if (await _database.GetSettingAsync(SettingLegacyMigration, token).ConfigureAwait(false) != "1")
        {
            var previousUrl = await _database.GetSettingAsync("source.active", token).ConfigureAwait(false);
            List<string> oldEndpoints;
            try
            {
                var previous = await _database.GetSettingAsync("source.endpoints", token).ConfigureAwait(false);
                oldEndpoints = previous is null ? [] : JsonSerializer.Deserialize<List<string>>(previous) ?? [];
            }
            catch (JsonException) { oldEndpoints = []; }
            foreach (var url in oldEndpoints)
            {
                if (IsLegacyEndpoint(url) || !TryGetRepositoryUri(url, out var entryUri) ||
                    imports.Any(entry => entry.Url.Equals(entryUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;
                imports.Add(new ImportedRepository(entryUri.AbsoluteUri, entryUri.Host, new ConfigDocument()));
            }

            var writes = new List<(string Key, string Value)> { (SettingLegacyMigration, "1") };
            var legacy = await ReadLegacySnapshotAsync(token).ConfigureAwait(false);
            if (legacy is not null || activeId == LegacyRepositoryId || IsLegacyEndpoint(previousUrl) || oldEndpoints.Any(IsLegacyEndpoint))
            {
                var url = IsLegacyEndpoint(previousUrl) ? new Uri(previousUrl!).AbsoluteUri : LegacyEndpoints[0];
                var index = imports.FindIndex(entry => IsLegacyEndpoint(entry.Url) || IsLegacyEndpoint(entry.GroupUrl));
                if (legacy is not null) NormalizeLinks(legacy, new Uri(url));
                var imported = index < 0 ? new ImportedRepository(url, "饭太硬", legacy ?? new ConfigDocument()) : imports[index];
                if (index < 0) imports.Insert(0, imported);
                else if (imported.Document.Sites.Count == 0 && imported.Document.Lives.Count == 0 && legacy is not null)
                    imports[index] = imported = imported with { Document = legacy };
                var migratedId = ComputeRepositoryId(imported.Url);
                if (activeId.Length == 0 || activeId == LegacyRepositoryId) activeId = migratedId;
                var siteKey = await _database.GetSettingAsync("source.homeSite", token).ConfigureAwait(false);
                var newHomeKey = $"source.homeSite.{migratedId}";
                if (!string.IsNullOrWhiteSpace(siteKey) && await _database.GetSettingAsync(newHomeKey, token).ConfigureAwait(false) is null)
                    writes.Add((newHomeKey, siteKey));
                writes.Add((SettingPendingPackUrl, imported.Url));
            }
            writes.Add((SettingImports, JsonSerializer.Serialize(imports, JsonUtil.Options)));
            writes.Add((SettingActiveRepo, activeId == LegacyRepositoryId ? "" : activeId));
            await _database.SetSettingsAsync(writes, token).ConfigureAwait(false);
        }
        foreach (var entry in imports) HydrateDocument(entry.Document, entry.Url, entry.GroupUrl);
        _activeRepositoryId = activeId == LegacyRepositoryId ? "" : activeId;
        _imports = imports;
    }

    private static async Task<ConfigDocument?> ReadLegacySnapshotAsync(CancellationToken token)
    {
        foreach (var path in new[] { AppPaths.Config, AppPaths.Config + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var snapshot = Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                if (snapshot.Sites.Count > 0 || snapshot.Lives.Count > 0) return snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Diagnostics.Log("迁移旧版仓库缓存: " + ex.GetType().Name);
            }
        }
        return null;
    }

    private static bool TryGetRepositoryUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !MediaRequestHeaders.IsHttpAddress(parsed) || parsed.UserInfo.Length != 0)
            return false;
        uri = parsed;
        return true;
    }

    private static bool IsLegacyEndpoint(string? value) => TryGetRepositoryUri(value, out var uri) &&
        LegacyEndpoints.Any(endpoint =>
        {
            var old = new Uri(endpoint);
            return old.IdnHost.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                old.AbsolutePath.TrimEnd('/').Equals(uri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        });

    private List<SourceRepository> BuildRepositoryList()
    {
        var list = new List<SourceRepository>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _imports ?? [])
        {
            var id = ComputeRepositoryId(entry.Url);
            if (!seenIds.Add(id)) continue;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? HostDisplayName(entry.Url) : entry.Name.Trim();
            list.Add(new SourceRepository(id, name, entry.Url, false, entry.Document, entry.GroupUrl));
        }
        return list;
    }

    private void PublishCurrent()
    {
        var repos = BuildRepositoryList();
        Repositories = repos;
        var active = repos.FirstOrDefault(r => r.Id.Equals(_activeRepositoryId, StringComparison.OrdinalIgnoreCase))
            ?? repos.FirstOrDefault();
        _activeRepositoryId = active?.Id ?? "";
        ActiveRepository = active;
        Current = active?.Document ?? new ConfigDocument();
        ActiveUrl = active?.Url ?? EmptyRepositoryName;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void HydrateDocument(ConfigDocument doc, string repositoryUrl, string? groupUrl)
    {
        var spider = doc.Spider?.Trim() ?? string.Empty;
        foreach (var site in doc.Sites)
        {
            site.RepositoryUrl = repositoryUrl;
            site.RepositoryGroupUrl = groupUrl;
            if (string.IsNullOrWhiteSpace(site.RepositorySpider) && spider.Length > 0)
                site.RepositorySpider = spider;
        }
        foreach (var live in doc.Lives)
            live.IsBuiltIn = false;
    }

    public static string ComputeRepositoryId(string url) =>
        "repo-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..12].ToLowerInvariant();

    private static string HostDisplayName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private Task? _refreshLoop;
    private readonly CancellationTokenSource _lifetime = new();

    public async Task<ConfigDocument> LoadAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (Current is null)
        {
            await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Current is null)
                {
                    await ReadImportsAsync(cancellationToken).ConfigureAwait(false);
                    PublishCurrent();
                    EnsurePeriodicRefresh();
                    _ = RefreshBackgroundAsync();
                }
            }
            finally { _loadLock.Release(); }
        }
        if (force) await RefreshActiveAsync(cancellationToken).ConfigureAwait(false);
        return Current!;
    }

    private async Task RefreshActiveAsync(CancellationToken token)
    {
        await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = _imports?.FirstOrDefault(entry => ComputeRepositoryId(entry.Url) == ActiveRepositoryId);
            if (snapshot is null || !TryGetRepositoryUri(snapshot.Url, out var address)) return;
            var (document, error, _) = await ReadRepositoryEntryAsync(address, snapshot.Name, token).ConfigureAwait(false);
            string? usedUrl = snapshot.Url;
            string? notice = null;
            if (document is null && TryGetRepositoryUri(snapshot.BackupUrl, out var backupAddress))
            {
                // 主地址失效时回退备用地址拉取，并把可执行的提示交给设置页展示
                var (backupDocument, backupError, _) = await ReadRepositoryEntryAsync(backupAddress, snapshot.Name, token).ConfigureAwait(false);
                if (backupDocument is not null)
                {
                    document = backupDocument;
                    usedUrl = snapshot.BackupUrl;
                    notice = $"「{snapshot.Name}」主地址失效，已使用备用地址刷新。可在仓库列表中调整备用地址。";
                    Diagnostics.Log($"仓库主地址失效，备用地址接管: {snapshot.Name}");
                }
                else
                {
                    Diagnostics.Log($"仓库主地址与备用地址均刷新失败: {snapshot.Name} {backupError ?? "未知"}");
                }
            }
            if (document is null) throw new InvalidOperationException("仓库刷新失败。" + (error ?? "未获取到可用片源。"));
            HydrateDocument(document, usedUrl, snapshot.GroupUrl);
            LastRefreshNotice = notice;
            await _loadLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var index = _imports!.IndexOf(snapshot);
                if (index < 0) return;
                var updated = _imports.ToList();
                updated[index] = snapshot with { Document = document };
                await _database.SetSettingsAsync([(SettingImports, JsonSerializer.Serialize(updated, JsonUtil.Options)),
                    ("source.lastRefresh", DateTimeOffset.Now.ToString("O"))], token).ConfigureAwait(false);
                _imports = updated;
                PublishCurrent();
            }
            finally { _loadLock.Release(); }
        }
        finally { _refreshLock.Release(); }
    }

    private async Task RefreshBackgroundAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var pending = await _database.GetSettingAsync(SettingPendingPackUrl, token).ConfigureAwait(false);
            if (_installMigratedPack is not null && !string.IsNullOrEmpty(pending))
            {
                var imported = _imports?.FirstOrDefault(entry => entry.Url.Equals(pending, StringComparison.OrdinalIgnoreCase));
                if (imported is null || await _installMigratedPack(imported.GroupUrl ?? pending, token).ConfigureAwait(false))
                {
                    await _database.SetSettingAsync(SettingPendingPackUrl, "", token).ConfigureAwait(false);
                    // Apply newly available rules to the visible catalogue without waiting for a full restart.
                    if (imported is not null) Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) { Diagnostics.Log("迁移饭太硬适配包: " + ex.GetType().Name); }
        try
        {
            if (ActiveRepository is not null && await _database.GetSettingAsync("source.autoRefresh", token).ConfigureAwait(false) != "false"
                && await IsRefreshDueAsync(token).ConfigureAwait(false))
                await RefreshActiveAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Log("仓库后台刷新: " + ex.GetType().Name); }
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
                    await RefreshBackgroundAsync().ConfigureAwait(false);
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
        var completed = new HashSet<Uri>();
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
                    batch.Add(ReadEntryAsync(entry));
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
                if (!completed.Add(baseUri)) continue;

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
                    var displayName = string.IsNullOrWhiteSpace(entryName) ? baseUri.Host : entryName;
                    var groupUrl = isRoot && baseUri == root ? null : root.AbsoluteUri;
                    HydrateDocument(document, baseUri.AbsoluteUri, groupUrl);
                    results.Add(new ImportedRepository(baseUri.AbsoluteUri, displayName, document, groupUrl));
                }
            }
        }

        if (results.Count == 0)
            throw new InvalidDataException(errors.FirstOrDefault() ?? "配置中没有可用的点播或直播源。");
        if (errors.Count > 0) Diagnostics.Log($"多仓部分条目读取失败：{string.Join(" · ", errors)}");
        return results;

        async Task<(ConfigDocument? Document, string? Error, Uri? BaseUri, string Name, bool IsRoot)> ReadEntryAsync(
            (Uri Uri, string Name, bool IsRoot) entry)
        {
            var (document, error, baseUri) = await ReadRepositoryEntryAsync(entry.Uri, entry.Name, token).ConfigureAwait(false);
            return (document, error, baseUri, entry.Name, entry.IsRoot);
        }
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
            if (TryHttpUri(parent, site.DesktopApi) is { } desktop)
                site.DesktopApi = desktop.AbsoluteUri;
            if (site.Type is 0 or 1 or 4 || IsRelativeOrUrlPath(site.Api))
            {
                if (TryHttpUri(parent, site.Api) is { } api) site.Api = api.AbsoluteUri;
            }
            site.Ext = NormalizeExtLinks(parent, site.Ext, addressField: true);
            if (!string.IsNullOrWhiteSpace(document.Spider) && string.IsNullOrWhiteSpace(site.RepositorySpider))
                site.RepositorySpider = document.Spider;
        }
        foreach (var live in document.Lives)
            if (TryHttpUri(parent, live.Url) is { } url) live.Url = url.AbsoluteUri;
        foreach (var parser in document.Parses)
            if (IsRelativeOrUrlPath(parser.Url)
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

    private static JsonElement NormalizeExtLinks(Uri parent, JsonElement value, bool addressField = false, int depth = 0)
    {
        if (depth >= 5) return value;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var objectChanged = false;
                foreach (var property in value.EnumerateObject())
                {
                    var nested = NormalizeExtLinks(parent, property.Value, LinkExtensionFields.Contains(property.Name), depth + 1);
                    objectChanged |= !nested.Equals(property.Value);
                    properties[property.Name] = nested;
                }
                return objectChanged ? JsonSerializer.SerializeToElement(properties) : value;
            case JsonValueKind.Array:
                var items = new List<JsonElement>();
                var arrayChanged = false;
                foreach (var item in value.EnumerateArray())
                {
                    var nested = NormalizeExtLinks(parent, item, addressField, depth + 1);
                    arrayChanged |= !nested.Equals(item);
                    items.Add(nested);
                }
                return arrayChanged ? JsonSerializer.SerializeToElement(items) : value;
            case JsonValueKind.String:
                var raw = value.GetString() ?? string.Empty;
                var trimmed = raw.Trim();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(trimmed);
                        var normalized = NormalizeExtLinks(parent, document.RootElement, addressField, depth + 1);
                        return normalized.Equals(document.RootElement) ? value : JsonSerializer.SerializeToElement(normalized.GetRawText());
                    }
                    catch (JsonException) { return value; }
                }

                var resolved = ResolveRelativeSegments(parent, raw, addressField);
                return resolved.Equals(raw, StringComparison.Ordinal) ? value : JsonSerializer.SerializeToElement(resolved);
            default:
                return value;
        }
    }

    private static string ResolveRelativeSegments(Uri parent, string raw, bool addressField)
    {
        foreach (var separator in new[] { "$$$", "|", "," })
        {
            if (!raw.Contains(separator, StringComparison.Ordinal) || separator != "$$$" && !addressField) continue;
            var parts = raw.Split(separator);
            for (var i = 0; i < parts.Length; i++)
                parts[i] = Resolve(parts[i]);
            return string.Join(separator, parts);
        }
        return Resolve(raw);

        string Resolve(string item)
        {
            var trimmed = item.Trim();
            var relative = trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal) ||
                addressField && trimmed.StartsWith('/');
            return relative && TryHttpUri(parent, trimmed) is { } uri ? uri.AbsoluteUri : item;
        }
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
        document.Urls.RemoveAll(static entry => entry is null);
        document.StoreHouse.RemoveAll(static entry => entry is null);
        document.Sites.RemoveAll(static site => site is null);
        document.Lives.RemoveAll(static live => live is null);
        document.Parses.RemoveAll(static parser => parser is null);
        foreach (var site in document.Sites)
        {
            site.Api = site.Api?.Trim() ?? string.Empty;
            site.Name = site.Name?.Trim() ?? string.Empty;
            site.Jar ??= string.Empty;
            site.Key = string.IsNullOrWhiteSpace(site.Key) ? ComputeRepositoryId(site.Api + "\n" + site.Name) : site.Key.Trim();
            if (site.Name.Length == 0) site.Name = site.Key;
            site.Categories ??= [];
        }
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
