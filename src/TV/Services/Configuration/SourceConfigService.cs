using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Configuration;

public sealed class SourceConfigService : IDisposable
{
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
    private List<ImportedRepository>? _imports;

    public SourceConfigService(SourceHttpClient http, DatabaseService database)
    {
        _http = http;
        _database = database;
    }

    public ConfigDocument? Current { get; private set; }
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

    public async Task ImportAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new ArgumentException("请输入有效的 HTTP/HTTPS 配置地址。");
        var document = await FetchRepositoryAsync(uri, cancellationToken).ConfigureAwait(false);
        await UpdateImportsAsync(imports =>
        {
            imports.RemoveAll(item => item.Url == uri.AbsoluteUri);
            imports.Insert(0, new ImportedRepository(uri.AbsoluteUri, uri.Host, document));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveImportAsync(string url, CancellationToken cancellationToken = default) =>
        UpdateImportsAsync(imports => imports.RemoveAll(item => item.Url == url), cancellationToken);

    private async Task UpdateImportsAsync(Action<List<ImportedRepository>> update, CancellationToken token)
    {
        await _loadLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ReadImportsAsync(token).ConfigureAwait(false);
            var imports = _imports!.ToList();
            update(imports);
            await _database.SetSettingAsync("source.imports", JsonSerializer.Serialize(imports, JsonUtil.Options), token).ConfigureAwait(false);
            _imports = imports;
            PublishCurrent();
        }
        finally { _loadLock.Release(); }
    }

    private async Task ReadImportsAsync(CancellationToken token)
    {
        if (_imports is not null) return;
        var json = await _database.GetSettingAsync("source.imports", token).ConfigureAwait(false);
        if (json is not null)
        {
            try { _imports = JsonSerializer.Deserialize<List<ImportedRepository>>(json, JsonUtil.Options) ?? []; }
            catch (JsonException) { _imports = []; }
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
                // Keep previously saved links visible even when their host is offline.
                migrated.Add(new ImportedRepository(uri.AbsoluteUri, uri.Host, new ConfigDocument()));
            }
        }
        _imports = migrated;
    }

    private void PublishCurrent()
    {
        Current = Merge(_builtIn ?? new ConfigDocument(), _imports ?? []);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ConfigDocument Merge(ConfigDocument builtIn, IEnumerable<ImportedRepository> imports)
    {
        var config = new ConfigDocument
        {
            Sites = [.. builtIn.Sites], Lives = [.. builtIn.Lives], Parses = [.. builtIn.Parses],
            Flags = [.. builtIn.Flags], Ads = [.. builtIn.Ads], Spider = builtIn.Spider, Rules = builtIn.Rules
        };
        var keys = config.Sites.Select(site => site.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveUrls = config.Lives.Select(live => live.Url).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in imports)
        {
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Url)))[..12];
            foreach (var source in entry.Document.Sites)
            {
                var copy = JsonSerializer.Deserialize<SourceSite>(JsonSerializer.Serialize(source, JsonUtil.Options), JsonUtil.Options)!;
                var key = $"{source.Key}@{suffix}";
                copy.Key = key;
                if (keys.Add(key)) config.Sites.Add(copy);
            }
            foreach (var source in entry.Document.Lives)
            {
                if (!liveUrls.Add(source.Url)) continue;
                config.Lives.Add(new LiveSource
                {
                    Name = source.Name, Type = source.Type, Url = source.Url, UserAgent = source.UserAgent,
                    Epg = source.Epg, Logo = source.Logo, Header = source.Header, IsBuiltIn = false
                });
            }
            config.Parses.AddRange(entry.Document.Parses);
        }
        return config;
    }

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
            var autoRefresh = !string.Equals(await _database.GetSettingAsync("source.autoRefresh", cancellationToken).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);
            if ((!force || _builtIn is null) && File.Exists(AppPaths.Config))
            {
                try
                {
                    var cachedRaw = await File.ReadAllTextAsync(AppPaths.Config, cancellationToken).ConfigureAwait(false);
                    _builtIn = Parse(cachedRaw);
                    ActiveUrl = "本地缓存";
                    LastError = string.Empty;
                    PublishCurrent();
                    if (!force && autoRefresh)
                    {
                        EnsurePeriodicRefresh();
                        // 命中磁盘缓存后立即返回可用内容；只有“到点”时才在后台刷新，
                        // 避免每次启动都拉远端配置拖慢启动。
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
        AppPaths.EnsureCreated();
        var temp = AppPaths.Config + ".tmp";
        await File.WriteAllTextAsync(temp, raw, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (File.Exists(AppPaths.Config)) File.Copy(AppPaths.Config, AppPaths.Config + ".bak", true);
        File.Move(temp, AppPaths.Config, true);
        await _database.SetSettingAsync("source.active", finalUrl, cancellationToken).ConfigureAwait(false);
        await _database.SetSettingAsync("source.lastRefresh", DateTimeOffset.Now.ToString("O"), cancellationToken).ConfigureAwait(false);
        _builtIn = document;
        ActiveUrl = finalUrl;
        LastError = string.Empty;
        PublishCurrent();
    }

    private async Task RefreshBackgroundAsync()
    {
        try { await LoadAsync(true, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Log("后台配置刷新失败: " + ex.Message); }
    }

    // 配置刷新时段：每天 06:00 / 12:00 / 18:00 各一次；启动时命中缓存只判断是否“到点”，
    // 不再无条件刷新，保证启动到可用内容的时间最短。
    private static readonly int[] RefreshSlots = [6, 12, 18];

    private static DateTimeOffset MostRecentSlotStart(DateTimeOffset now)
    {
        var todaySlots = RefreshSlots.Select(hour => new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset)).ToArray();
        var latest = todaySlots.Where(slot => slot <= now).DefaultIfEmpty(todaySlots[^1].AddDays(-1)).Max();
        return latest;
    }

    private async Task<bool> IsRefreshDueAsync(CancellationToken token)
    {
        try
        {
            var raw = await _database.GetSettingAsync("source.lastRefresh", token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw) || !DateTimeOffset.TryParse(raw, out var last)) return true;
            var slot = MostRecentSlotStart(DateTimeOffset.Now);
            // 时区/时钟漂移兜底：最近一次刷新晚于当前时段起点即视为已刷新。
            return last < slot;
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

    private async Task<ConfigDocument> FetchRepositoryAsync(Uri root, CancellationToken token)
    {
        var result = new ConfigDocument();
        var visited = new HashSet<Uri>();
        var pending = new Queue<(Uri Uri, string Name)>();
        pending.Enqueue((root, root.Host));
        var errors = new List<string>();
        while (pending.Count > 0 && visited.Count < 32)
        {
            var batch = new List<Task<(ConfigDocument? Document, string? Error, Uri? BaseUri)>>();
            while (pending.TryDequeue(out var entry) && visited.Count < 32)
            {
                if (visited.Add(entry.Uri)) batch.Add(ReadRepositoryEntryAsync(entry.Uri, entry.Name, token));
                if (batch.Count >= 6) break;
            }
            foreach (var (document, error, baseUri) in await Task.WhenAll(batch).ConfigureAwait(false))
            {
                if (document is null)
                {
                    if (error is not null) errors.Add(error);
                    continue;
                }
                foreach (var depot in document.Urls.Take(32))
                {
                    if (TryHttpUri(baseUri!, depot.Url) is { } uri)
                        pending.Enqueue((uri, string.IsNullOrWhiteSpace(depot.Name) ? uri.Host : depot.Name));
                }
                var keys = result.Sites.Select(site => site.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var site in document.Sites)
                {
                    if (keys.Add(site.Key)) result.Sites.Add(site);
                    else if (result.Sites.Any(existing => existing.Key == site.Key && existing.Api != site.Api))
                    {
                        site.Key += "@" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUri!.AbsoluteUri)))[..12];
                        if (keys.Add(site.Key)) result.Sites.Add(site);
                    }
                }
                result.Lives.AddRange(document.Lives);
                result.Parses.AddRange(document.Parses);
            }
        }
        if (result.Sites.Count == 0 && result.Lives.Count == 0)
            throw new InvalidDataException(errors.FirstOrDefault() ?? "配置中没有可用的点播或直播源。");
        if (errors.Count > 0) Diagnostics.Log($"多仓部分条目读取失败：{string.Join(" · ", errors)}");
        return result;
    }

    private async Task<(ConfigDocument? Document, string? Error, Uri? BaseUri)> ReadRepositoryEntryAsync(Uri uri, string name, CancellationToken token)
    {
        try
        {
            var payload = await _http.GetBytesAsync(uri.AbsoluteUri, maxBytes: 8 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false);
            ConfigDocument? document;
            try { document = Parse(DecodeConfig(payload.Content)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { document = null; }
            if (document is not null && (document.Sites.Count > 0 || document.Lives.Count > 0 || document.Urls.Count > 0))
            {
                NormalizeLinks(document, payload.FinalUri);
                return (document, null, payload.FinalUri);
            }

            var source = new LiveSource { Name = name, Url = payload.FinalUri.AbsoluteUri };
            if (!Live.LiveService.IsPlaylist(payload.Content, source)) throw new InvalidDataException("地址未返回点播配置或直播列表。");
            return (new ConfigDocument { Lives = [source] }, null, payload.FinalUri);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            return (null, $"{name}：{ex.Message}", null);
        }
    }

    private static Uri? TryHttpUri(Uri parent, string? link) =>
        !string.IsNullOrWhiteSpace(link) && Uri.TryCreate(parent, link, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri : null;

    private static void NormalizeLinks(ConfigDocument document, Uri parent)
    {
        foreach (var site in document.Sites.Where(site => site.Type is 0 or 1 or 4))
            if (TryHttpUri(parent, site.Api) is { } api) site.Api = api.AbsoluteUri;
        foreach (var live in document.Lives)
            if (TryHttpUri(parent, live.Url) is { } url) live.Url = url.AbsoluteUri;
        foreach (var parser in document.Parses)
            if (parser.Url.StartsWith('/') && TryHttpUri(parent, parser.Url) is { } url) parser.Url = url.AbsoluteUri;
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

    private static ConfigDocument Parse(string raw)
    {
        var document = JsonSerializer.Deserialize<ConfigDocument>(raw, JsonUtil.Options)
            ?? throw new InvalidDataException("配置 JSON 为空。");
        if (!string.IsNullOrWhiteSpace(document.Message)) throw new InvalidDataException(document.Message);
        document.Urls ??= [];
        document.Sites ??= [];
        document.Lives ??= [];
        document.Parses ??= [];
        document.Flags ??= [];
        document.Ads ??= [];
        return document;
    }

    private static string DecodeConfig(byte[] bytes)
    {
        var transport = Encoding.Latin1.GetString(bytes);
        var marker = System.Text.RegularExpressions.Regex.Match(transport, "[A-Za-z0-9]{8}\\*\\*");
        if (marker.Success && marker.Index + marker.Length < transport.Length)
        {
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(transport[(marker.Index + marker.Length)..].Trim())).Trim().Trim('\uFEFF');
                if (json.StartsWith('{')) return json;
            }
            catch (FormatException) { }
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).Trim().Trim('\uFEFF');
            if (text.StartsWith('{')) return text;
            var decoded = Convert.FromBase64String(text);
            text = Encoding.UTF8.GetString(decoded).Trim();
            if (text.StartsWith('{')) return text;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { }
        throw new InvalidDataException("响应不是受支持的 JSON 或图片附加 Base64 配置。");
    }
}
