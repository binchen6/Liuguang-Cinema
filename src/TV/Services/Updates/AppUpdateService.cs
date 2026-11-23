using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Updates;

public sealed record AppUpdateAsset(string Name, long Size, string DownloadUrl, string? Digest);
public sealed record AppUpdateRelease(Version Version, string Tag, string Title, string Notes, string PageUrl,
    DateTimeOffset PublishedAt, IReadOnlyList<AppUpdateAsset> Assets);
public sealed record AppUpdatePackage(AppUpdateAsset Asset, string? ChecksumUrl, string? ExpectedHash, bool Incremental);
public sealed record DownloadedUpdate(string Path, AppUpdatePackage Package);
public readonly record struct UpdateDownloadProgress(double Percent, long Downloaded, long Total);

public sealed partial class AppUpdateService : IDisposable
{
    private const string Repository = "binchen6/Liuguang-Cinema";
    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const long MaxPackageBytes = 512 * 1024 * 1024;
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(12);
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
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = "Liuguang-Cinema/" + CurrentVersion,
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2022-11-28"
            };
            var json = await _http.GetStringAsync(_endpoint, headers, deadline.Token).ConfigureAwait(false);
            var release = ParseRelease(json);
            await _database.SetSettingAsync("updates.lastCheck",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            return release.Version > CurrentVersion ? release : null;
        }
        finally { _checkGate.Release(); }
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
            using var checksumDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            checksumDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var text = await _http.GetStringAsync(package.ChecksumUrl, cancellationToken: checksumDeadline.Token).ConfigureAwait(false);
            var match = HashPattern().Match(text);
            if (!match.Success) throw new InvalidDataException("SHA-256 校验文件格式无效。");
            expected = match.Value;
        }
        if (!IsHash(expected)) throw new InvalidDataException("安装包缺少有效的 SHA-256。");

        var folder = Path.Combine(AppPaths.UpdateCache, CurrentVersion.ToString(3) + "-to-" + package.Asset.Name);
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, package.Asset.Name);
        if (File.Exists(destination) && new FileInfo(destination).Length == package.Asset.Size &&
            await HashMatchesAsync(destination, expected!, token).ConfigureAwait(false))
        {
            progress?.Report(new UpdateDownloadProgress(100, package.Asset.Size, package.Asset.Size));
            return new DownloadedUpdate(destination, package);
        }

        var partial = destination + ".partial";
        await DownloadWithResumeAsync(package.Asset, partial, progress, token).ConfigureAwait(false);
        if (new FileInfo(partial).Length != package.Asset.Size)
            throw new InvalidDataException("下载大小与 GitHub 发行版记录不一致。");
        if (!await HashMatchesAsync(partial, expected!, token).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new InvalidDataException("SHA-256 校验失败，已删除损坏的下载文件。");
        }
        File.Move(partial, destination, true);
        return new DownloadedUpdate(destination, package);
    }

    private async Task DownloadWithResumeAsync(AppUpdateAsset asset, string partial,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken token)
    {
        var offset = File.Exists(partial) ? Math.Min(new FileInfo(partial).Length, asset.Size) : 0;
        if (offset == asset.Size && offset > 0) offset = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", "Liuguang-Cinema/" + CurrentVersion);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await _downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            downloaded += read;
            if (downloaded > asset.Size) throw new InvalidDataException("下载内容超过发行版记录大小。");
            progress?.Report(new UpdateDownloadProgress(total > 0 ? downloaded * 100d / total : 0, downloaded, total));
        }
        await target.FlushAsync(token).ConfigureAwait(false);
    }

    private void ValidateAssetUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidDataException("下载地址无效。");
        var production = uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0;
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
}
