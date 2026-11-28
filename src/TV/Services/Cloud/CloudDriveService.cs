using System.Collections.Concurrent;
using TV.Models;
using TV.Services.Persistence;
using TV.Services.Http;

namespace TV.Services.Cloud;

public sealed class CloudDriveService : IDisposable
{
    private readonly Lazy<CloudMediaProxy> _mediaProxy;
    private sealed record CachedShare(Lazy<Task<CloudShare>> Request, DateTimeOffset ExpiresAt);
    private sealed record CachedFile(CloudFile File, IReadOnlyList<CloudSubtitleTrack> Subtitles, DateTimeOffset ExpiresAt);
    private sealed class ShareCache
    {
        public readonly ConcurrentDictionary<string, CachedShare> Shares = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<(CloudProviderKind Provider, string ShareId, string FileId, string Passcode, string Folder), CachedFile> Files = new();
    }

    private static readonly TimeSpan ShareLifetime = TimeSpan.FromMinutes(8);
    private readonly DatabaseService _database;
    private readonly CloudHttpClient _apiHttp;
    private readonly CloudHttpClient _loginHttp;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyDictionary<CloudProviderKind, ICloudProvider> _providers;
    private ShareCache _cache = new();

    public CloudDriveService(DatabaseService database, DnsResolverService? dns = null)
    {
        _database = database;
        _mediaProxy = new Lazy<CloudMediaProxy>(() =>
        {
            var proxy = new CloudMediaProxy(dns);
            proxy.AuthenticationRequired += (_, provider) => AuthenticationRequired?.Invoke(this, provider);
            return proxy;
        });
        _apiHttp = new CloudHttpClient(dns);
        _loginHttp = new CloudHttpClient(dns, allowAutoRedirect: false);
        var ali = new AliCloudProvider(_apiHttp, token => GetCredentialAsync(CloudProviderKind.Ali, token), database);
        var quark = new QuarkCloudProvider(_apiHttp, CloudProviderKind.Quark, token => GetCredentialAsync(CloudProviderKind.Quark, token), database);
        var uc = new QuarkCloudProvider(_apiHttp, CloudProviderKind.UC, token => GetCredentialAsync(CloudProviderKind.UC, token), database);
        var baidu = new BaiduCloudProvider(_apiHttp, token => GetCredentialAsync(CloudProviderKind.Baidu, token), database);
        _providers = new ICloudProvider[] { ali, quark, uc, baidu }.ToDictionary(provider => provider.Kind);
        Login = new CloudLoginService(_apiHttp, this, _loginHttp);
    }

    public CloudLoginService Login { get; }
    public event EventHandler<CloudProviderKind>? AccountChanged;
    public event EventHandler<CloudProviderKind>? AuthenticationRequired;

    public async Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = _providers.Values.FirstOrDefault(provider => provider.CanHandle(url)) ?? throw new NotSupportedException("暂不支持该网盘链接。");
        var key = $"{provider.Kind}:{url.Trim()}";
        var now = DateTimeOffset.UtcNow;
        var cache = Volatile.Read(ref _cache);
        if (cache.Shares.Count >= 256)
            foreach (var pair in cache.Shares)
                if (pair.Value.ExpiresAt <= now) cache.Shares.TryRemove(pair);
        var expiresAt = now.Add(ShareLifetime);
        var replacement = new CachedShare(new Lazy<Task<CloudShare>>(() => ReadShareAsync(provider, url, expiresAt, cache)), expiresAt);
        var entry = cache.Shares.AddOrUpdate(key, replacement, (_, current) =>
            current.ExpiresAt > now && (!current.Request.IsValueCreated || !current.Request.Value.IsCompleted || current.Request.Value.IsCompletedSuccessfully)
                ? current : replacement);
        var task = entry.Request.Value;
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            if (task.IsFaulted || task.IsCanceled) cache.Shares.TryRemove(new(key, entry));
            throw;
        }
    }

    private async Task<CloudShare> ReadShareAsync(ICloudProvider provider, string url, DateTimeOffset expiresAt, ShareCache cache)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        CloudShare share;
        try { share = await provider.GetShareAsync(url, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("网盘分享读取超时。");
        }
        CacheFiles(cache, share, expiresAt);
        return share;
    }

    public Task<CloudPlayResult> ResolveAsync(string id, string quality, CancellationToken cancellationToken = default) =>
        ResolveAsync(id, (provider, file, token) => provider.PlayAsync(file, quality, token), cancellationToken);

    internal Task<CloudPlayResult> ResolveAsync(string id, QualityOption quality, CancellationToken cancellationToken = default) =>
        ResolveAsync(id, (provider, file, token) => provider.PlayAsync(file, quality, token), cancellationToken);

    private async Task<CloudPlayResult> ResolveAsync(string id, Func<ICloudProvider, CloudFile, CancellationToken, Task<CloudPlayResult>> play,
        CancellationToken cancellationToken)
    {
        var entry = await FindFileAsync(id, cancellationToken).ConfigureAwait(false);
        if (CloudSubtitleFiles.IsSubtitle(entry.File.Name)) throw new InvalidDataException("该文件是字幕。");
        var provider = entry.File.Provider;
        var result = await play(_providers[provider], entry.File, cancellationToken).ConfigureAwait(false);
        var accelerate = !string.Equals(await _database.GetSettingAsync("playback.cloudAcceleration", cancellationToken).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);
        var threads = accelerate ? await ReadIntAsync("playback.cloudThreads", 3, 1, 6, cancellationToken).ConfigureAwait(false) : 1;
        var chunk = await ReadIntAsync("playback.cloudChunkMb", 2, 1, 8, cancellationToken).ConfigureAwait(false);
        Task<CloudPlayResult> RefreshAsync(CancellationToken token) => provider switch
        {
            CloudProviderKind.UC when result.AvailableVideoHeights is null => _providers[provider].PlayAsync(entry.File, "原画", token),
            CloudProviderKind.UC when result.ResolvedQuality is { } actual => _providers[provider].PlayAsync(entry.File, actual, token),
            _ => play(_providers[provider], entry.File, token)
        };
        return new CloudPlayResult(_mediaProxy.Value.Register(result, provider, threads, chunk,
            refresh: RefreshAsync), new Dictionary<string, string>(),
            result.ContentType, SubtitleTracks: entry.Subtitles)
        {
            AvailableVideoHeights = result.AvailableVideoHeights,
            AvailableQualities = result.AvailableQualities,
            ResolvedQuality = result.ResolvedQuality,
            Notice = result.Notice,
            ExpiresAt = result.ExpiresAt
        };
    }

    public async Task<Uri> ResolveSubtitleAsync(CloudSubtitleTrack track, CancellationToken cancellationToken = default)
    {
        var entry = await FindFileAsync(track.Source.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        var file = entry.File;
        var extension = CloudSubtitleFiles.Extension(file.Name) ?? throw new InvalidDataException("该文件不是受支持的字幕。");
        var result = await _providers[file.Provider].PlayAsync(file, "原画", cancellationToken).ConfigureAwait(false);
        return new Uri(ProxyMedia(result with { FileExtension = extension }));
    }

    private async Task<CachedFile> FindFileAsync(string id, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) || uri.Scheme != "cloud") throw new ArgumentException("云播放地址无效。", nameof(id));
        if (!Enum.TryParse<CloudProviderKind>(uri.Host, true, out var provider) || !Enum.IsDefined(provider))
            throw new ArgumentException("网盘类型无效。", nameof(id));
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments.All(CloudShareLink.IsIdentifier)) throw new ArgumentException("云播放地址缺少文件信息。", nameof(id));
        var passcode = CloudShareLink.ReadPasscode(uri);
        var folder = CloudShareLink.ReadQuery(uri, "folder");
        if (!CloudShareLink.IsIdentifier(folder)) folder = string.Empty;
        var key = (provider, segments[0], segments[1], passcode, folder);
        if (Volatile.Read(ref _cache).Files.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow) return entry;
        if (segments[0] == "drive")
        {
            var path = CloudShareLink.ReadQuery(uri, "path");
            var name = CloudShareLink.ReadQuery(uri, "name");
            var parent = CloudShareLink.ReadQuery(uri, "parent");
            if (path.Length is 0 or > 2048 || name.Length is 0 or > 512) throw new ArgumentException("个人云盘文件信息不完整。", nameof(id));
            _ = long.TryParse(CloudShareLink.ReadQuery(uri, "size"), out var size);
            var driveFile = new CloudFile(provider, "drive", segments[1], CloudShareLink.ReadQuery(uri, "token"), name, size,
                ParentDirectoryId: parent, DrivePath: path);
            return new CachedFile(driveFile, [], DateTimeOffset.UtcNow.Add(ShareLifetime));
        }
        var shareUrl = new CloudShareLink(provider, segments[0], folder, passcode).ToShareUrl();
        var share = await GetShareAsync(shareUrl, cancellationToken).ConfigureAwait(false);
        var file = share.Files.Concat(share.SubtitleFiles).FirstOrDefault(item => item.FileId == segments[1])
            ?? throw new FileNotFoundException("分享中找不到该文件。");
        return new CachedFile(file, CloudSubtitleFiles.Match(file, share.SubtitleFiles), DateTimeOffset.UtcNow.Add(ShareLifetime));
    }

    public string ProxyMedia(CloudPlayResult media, string? manifest = null) =>
        ProxyMedia(media, manifest, null);

    internal string ProxyMedia(CloudPlayResult media, string? manifest, Func<CancellationToken, Task<CloudPlayResult>>? refresh) =>
        _mediaProxy.Value.Register(media, null, media.UseBoundedRanges ? 3 : 1, media.UseBoundedRanges ? 2 : 4, manifest, refresh);

    public void ReleaseMedia(string? address)
    {
        if (_mediaProxy.IsValueCreated) _mediaProxy.Value.ReleaseAddress(address);
    }

    public async Task<VodItem> ExpandAsync(VodItem item, CancellationToken cancellationToken = default)
    {
        if (!item.PlayUrl.Contains("pan.quark.cn", StringComparison.OrdinalIgnoreCase) && !item.PlayUrl.Contains("drive.uc.cn", StringComparison.OrdinalIgnoreCase) && !item.PlayUrl.Contains("aliyundrive.com", StringComparison.OrdinalIgnoreCase) && !item.PlayUrl.Contains("alipan.com", StringComparison.OrdinalIgnoreCase) && !item.PlayUrl.Contains("pan.baidu.com", StringComparison.OrdinalIgnoreCase)) return item;
        var lines = PlaybackParsing.ParseLines(item);
        using var limiter = new SemaphoreSlim(2);
        var tasks = lines.Select(async line =>
        {
            var episodes = new List<Episode>();
            foreach (var episode in line.Episodes)
            {
                if (episode.Url.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
                {
                    episodes.Add(episode);
                    continue;
                }
                if (!_providers.Values.Any(provider => provider.CanHandle(episode.Url)))
                {
                    episodes.Add(episode);
                    continue;
                }
                await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var share = await GetShareAsync(episode.Url, cancellationToken).ConfigureAwait(false);
                    var files = SelectFiles(share.Files, episode.Name);
                    foreach (var file in files)
                        episodes.Add(new Episode { Name = SafeEpisodeName(file.Name), Url = GetPlaybackId(file) });
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Diagnostics.Log("分享扩展失败: " + ex.Message);
                    episodes.Add(episode);
                }
                finally { limiter.Release(); }
            }
            return new PlaybackLine { Name = line.Name, Episodes = episodes };
        }).ToArray();
        var expanded = await Task.WhenAll(tasks).ConfigureAwait(false);
        item.PlayFrom = string.Join("$$$", expanded.Select(line => line.Name));
        item.PlayUrl = string.Join("$$$", expanded.Select(line => string.Join('#', line.Episodes.Select(episode => $"{episode.Name}${episode.Url}"))));
        return item;
    }

    private static List<CloudFile> SelectFiles(List<CloudFile> files, string episodeName)
    {
        var keyword = PlaybackParsing.CleanTitle(episodeName).Replace("网盘", string.Empty).Replace("播放", string.Empty).Trim();
        if (keyword.Length < 2 || files.Count == 1) return files;
        var matches = files.Where(file => file.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) || keyword.Contains(Path.GetFileNameWithoutExtension(file.Name), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count > 0 ? matches : files;
    }

    public ICloudProvider Provider(CloudProviderKind kind) => _providers[kind];
    public ICloudDriveProvider DriveProvider(CloudProviderKind kind) => _providers[kind] as ICloudDriveProvider
        ?? throw new NotSupportedException($"{kind} 尚未提供个人云盘目录能力。");
    public Task<CloudDriveListing> ListFolderAsync(CloudProviderKind kind, string folderId, string path, int page, CancellationToken token = default) =>
        DriveProvider(kind).ListFolderAsync(folderId, path, page, token);
    public Task<CloudDriveListing> SearchFolderAsync(CloudProviderKind kind, string keyword, int page, CancellationToken token = default) =>
        DriveProvider(kind).SearchFolderAsync(keyword, page, token);

    public async Task<string> GetCredentialAsync(CloudProviderKind provider, CancellationToken cancellationToken = default) =>
        SecretStore.Unprotect(await _database.GetSettingAsync(CredentialKey(provider), cancellationToken).ConfigureAwait(false));

    public async Task<CloudAccountInfo> GetAccountAsync(CloudProviderKind provider, CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        return new CloudAccountInfo(provider, credential.Length == 0 ? CloudAccountState.NotLoggedIn : CloudAccountState.Saved,
            credential.Length == 0 ? "未登录" : "凭据已保存，待验证");
    }

    public async Task<CloudAccountInfo> VerifyAccountAsync(CloudProviderKind provider, CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(provider, cancellationToken).ConfigureAwait(false);
        if (credential.Length == 0) return new CloudAccountInfo(provider, CloudAccountState.NotLoggedIn, "未登录");
        try
        {
            var valid = provider switch
            {
                CloudProviderKind.Ali => await Login.VerifyAliTokenAsync(credential, cancellationToken).ConfigureAwait(false),
                CloudProviderKind.Quark or CloudProviderKind.UC => await ((QuarkCloudProvider)_providers[provider]).VerifyCookieAsync(cancellationToken).ConfigureAwait(false),
                CloudProviderKind.Baidu => await Login.VerifyBaiduCookieAsync(credential, cancellationToken).ConfigureAwait(false),
                _ => false
            };
            var result = new CloudAccountInfo(provider, valid ? CloudAccountState.LoggedIn : CloudAccountState.Expired,
                valid ? "登录有效" : "登录已失效，请重新扫码");
            AccountChanged?.Invoke(this, provider);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Log($"网盘账号验证失败 ({provider}): {ex.GetType().Name}");
            return new CloudAccountInfo(provider, CloudAccountState.Error, "验证失败，请检查网络后重试");
        }
    }

    public async Task SaveCredentialAsync(CloudProviderKind provider, string credential, CancellationToken cancellationToken = default)
    {
        if (_providers[provider] is QuarkCloudProvider quark)
            await quark.SaveCredentialAsync(credential, cancellationToken).ConfigureAwait(false);
        else
        {
            await _database.SetSettingAsync(CredentialKey(provider), SecretStore.Protect(credential), cancellationToken).ConfigureAwait(false);
            if (_providers[provider] is AliCloudProvider ali) ali.InvalidateAccountCache();
        }
        AccountChanged?.Invoke(this, provider);
    }

    private static string CredentialKey(CloudProviderKind provider) => provider switch
    {
        CloudProviderKind.Ali => "cloud.aliToken",
        CloudProviderKind.Quark => "cloud.quarkCookie",
        CloudProviderKind.UC => "cloud.ucCookie",
        CloudProviderKind.Baidu => "cloud.baiduCookie",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public void ClearShareCache() => Interlocked.Exchange(ref _cache, new ShareCache());

    private static void CacheFiles(ShareCache cache, CloudShare share, DateTimeOffset expiresAt)
    {
        if (cache.Files.Count >= 2048)
            foreach (var pair in cache.Files)
                if (pair.Value.ExpiresAt <= DateTimeOffset.UtcNow) cache.Files.TryRemove(pair);
        var subtitles = share.SubtitleFiles.ToLookup(file => file.ParentDirectoryId, StringComparer.Ordinal);
        foreach (var file in share.Files)
            cache.Files[(file.Provider, file.ShareId, file.FileId, file.Passcode, file.ShareFolderId)] =
                new CachedFile(file, CloudSubtitleFiles.Match(file, subtitles[file.ParentDirectoryId]), expiresAt);
        foreach (var file in share.SubtitleFiles)
            cache.Files[(file.Provider, file.ShareId, file.FileId, file.Passcode, file.ShareFolderId)] = new CachedFile(file, [], expiresAt);
    }

    private async Task<int> ReadIntAsync(string key, int fallback, int minimum, int maximum, CancellationToken token)
    {
        var value = await _database.GetSettingAsync(key, token).ConfigureAwait(false);
        return int.TryParse(value, out var result) ? Math.Clamp(result, minimum, maximum) : fallback;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_mediaProxy.IsValueCreated) _mediaProxy.Value.Dispose();
        _apiHttp.Dispose();
        _loginHttp.Dispose();
    }

    internal static string GetPlaybackId(CloudFile file)
    {
        if (file.ShareId == "drive")
        {
            return $"cloud://{file.Provider.ToString().ToLowerInvariant()}/drive/{file.FileId}?path={Uri.EscapeDataString(file.DrivePath)}" +
                $"&name={Uri.EscapeDataString(file.Name)}&size={file.Size}&parent={Uri.EscapeDataString(file.ParentDirectoryId)}&token={Uri.EscapeDataString(file.FileToken)}";
        }
        var query = CloudShareLink.PasscodeQuery(file.Passcode);
        if (file.ShareFolderId.Length > 0)
            query += (query.Length == 0 ? "?" : "&") + "folder=" + Uri.EscapeDataString(file.ShareFolderId);
        return $"cloud://{file.Provider.ToString().ToLowerInvariant()}/{file.ShareId}/{file.FileId}{query}";
    }

    internal static string SafeEpisodeName(string name) => name.Replace('#', '＃').Replace('$', '＄');

    internal static string FormatEpisode(CloudFile file) => $"{SafeEpisodeName(file.Name)}${GetPlaybackId(file)}";

    public static CloudProviderKind ParseProvider(string value) =>
        value.Equals("ali", StringComparison.OrdinalIgnoreCase) || value.Contains("Ali", StringComparison.OrdinalIgnoreCase) ? CloudProviderKind.Ali
        : value.Equals("quark", StringComparison.OrdinalIgnoreCase) || value.Contains("Quark", StringComparison.OrdinalIgnoreCase) ? CloudProviderKind.Quark
        : value.Equals("baidu", StringComparison.OrdinalIgnoreCase) || value.Contains("Baidu", StringComparison.OrdinalIgnoreCase) ? CloudProviderKind.Baidu
        : CloudProviderKind.UC;
}
