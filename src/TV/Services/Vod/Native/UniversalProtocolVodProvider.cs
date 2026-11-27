using System.Globalization;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Spider;

namespace TV.Services.Vod.Native;

/// <summary>
/// Universal native C# adapter for widely used Android TVBox JSON/HTTP protocols:
/// 1. AppYsV2 / AppV2 / AppGet / AppQi / AppRJ mobile CMS APIs (/api.php/app/* and /xgapp.php/v1..v3/*)
/// 2. AList v3 directory/media servers (csp_AList / csp_AListGuard)
/// </summary>
public sealed class UniversalProtocolVodProvider(SourceHttpClient http) : INativePlaybackProvider
{
    private static readonly HashSet<string> AppApiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csp_AppYsV2", "csp_AppYsV2Guard",
        "csp_AppV2", "csp_AppV2Guard",
        "csp_AppGet", "csp_AppGetGuard",
        "csp_AppQi", "csp_AppQiGuard",
        "csp_AppRJ", "csp_AppRJGuard"
    };

    private static readonly HashSet<string> AListApiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csp_AList", "csp_AListGuard", "csp_AListV3"
    };

    public static bool Supports(SourceSite site) =>
        IsAppApiSite(site) || IsAListSite(site);

    public bool CanHandle(SourceSite site) => Supports(site);

    private static bool IsAppApiSite(SourceSite site) =>
        site.Type == 3 && AppApiNames.Contains(site.Api) && ResolveAppEndpoint(site) is not null;

    private static bool IsAListSite(SourceSite site) =>
        site.Type == 3 && AListApiNames.Contains(site.Api) && ParseAListDrives(site).Count > 0;

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        IsAListSite(site) ? AListHomeAsync(site, cancellationToken) : AppHomeAsync(site, cancellationToken);

    public Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default) =>
        IsAListSite(site) ? AListCategoryAsync(site, categoryId, page, cancellationToken) : AppCategoryAsync(site, categoryId, page, cancellationToken);

    public Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        IsAListSite(site) ? AListSearchAsync(site, keyword, page, cancellationToken) : AppSearchAsync(site, keyword, page, cancellationToken);

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default) =>
        IsAListSite(site) ? AListDetailAsync(site, id, cancellationToken) : AppDetailAsync(site, id, cancellationToken);

    public Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token) =>
        IsAListSite(site) ? AListPlayAsync(site, id, token) : AppPlayAsync(site, flag, id, token);

    #region AppYsV2 / AppV2 / xgapp Protocol

    private static string? ResolveAppEndpoint(SourceSite site)
    {
        string? raw = null;
        if (site.Ext.ValueKind == JsonValueKind.String)
            raw = site.Ext.GetString()?.Split("$$$", 2)[0].Trim();
        else if (site.Ext.ValueKind == JsonValueKind.Object)
            raw = NativeSiteToolkit.ReadJsonStringAny(site.Ext, "url", "api", "siteUrl", "host", "site");

        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool IsXgAppEndpoint(string endpoint) =>
        endpoint.Contains("xgapp.php", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Contains("/v1.vod", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Contains("/v2.vod", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Contains("/v3.vod", StringComparison.OrdinalIgnoreCase);

    private static string AppUrl(string endpoint, string suffix)
    {
        var baseClean = endpoint.TrimEnd('/');
        if (baseClean.EndsWith("/api.php/app", StringComparison.OrdinalIgnoreCase) ||
            baseClean.EndsWith("/xgapp.php/v1", StringComparison.OrdinalIgnoreCase) ||
            baseClean.EndsWith("/xgapp.php/v2", StringComparison.OrdinalIgnoreCase) ||
            baseClean.EndsWith("/xgapp.php/v3", StringComparison.OrdinalIgnoreCase) ||
            baseClean.EndsWith(".vod", StringComparison.OrdinalIgnoreCase))
        {
            return baseClean + suffix;
        }
        return baseClean + "/api.php/app" + suffix;
    }

    private async Task<CmsResponse> AppHomeAsync(SourceSite site, CancellationToken token)
    {
        var endpoint = ResolveAppEndpoint(site) ?? throw new InvalidDataException("App 接口地址无效。");
        var headers = NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders());
        var navUrl = IsXgAppEndpoint(endpoint) ? AppUrl(endpoint, "/nav?token=") : AppUrl(endpoint, "/nav?token=");
        var raw = await http.GetStringAsync(navUrl, headers, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var categories = new List<VodCategory>();
        var list = root.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : default;

        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                var id = NativeSiteToolkit.ReadJsonStringAny(entry, "type_id", "id");
                var name = NativeSiteToolkit.ReadJsonStringAny(entry, "type_name", "name");
                if (id.Length > 0 && name.Length > 0 && !name.Contains("伦理", StringComparison.Ordinal))
                    categories.Add(new VodCategory { Id = id, Name = name });
            }
        }

        List<VodItem> items = [];
        try
        {
            var indexRaw = await http.GetStringAsync(AppUrl(endpoint, "/index_video?token="), headers, token).ConfigureAwait(false);
            using var indexDoc = JsonDocument.Parse(indexRaw);
            items = ExtractAppVodList(indexDoc.RootElement, site);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        if (items.Count == 0 && categories.Count > 0)
        {
            var first = await AppCategoryAsync(site, categories[0].Id, 1, token).ConfigureAwait(false);
            items = first.List;
        }

        return new CmsResponse
        {
            Categories = categories,
            List = items,
            Page = 1,
            PageCount = 1,
            Total = items.Count
        };
    }

    private async Task<CmsResponse> AppCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken token)
    {
        var endpoint = ResolveAppEndpoint(site) ?? throw new InvalidDataException("App 接口地址无效。");
        var headers = NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders());
        page = Math.Max(1, page);
        var url = AppUrl(endpoint, $"/video?tid={Uri.EscapeDataString(categoryId)}&pg={page.ToString(CultureInfo.InvariantCulture)}&token=");
        var raw = await http.GetStringAsync(url, headers, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var items = ExtractAppVodList(doc.RootElement, site);
        var pageCount = NativeSiteToolkit.ReadJsonInt(doc.RootElement, "pagecount", items.Count > 0 ? page + 1 : page);
        return new CmsResponse { List = items, Page = page, PageCount = Math.Max(page, pageCount), Total = items.Count };
    }

    private async Task<CmsResponse> AppSearchAsync(SourceSite site, string keyword, int page, CancellationToken token)
    {
        var endpoint = ResolveAppEndpoint(site) ?? throw new InvalidDataException("App 接口地址无效。");
        var headers = NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders());
        page = Math.Max(1, page);
        var url = AppUrl(endpoint, $"/search?text={Uri.EscapeDataString(keyword)}&pg={page.ToString(CultureInfo.InvariantCulture)}");
        var raw = await http.GetStringAsync(url, headers, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var items = ExtractAppVodList(doc.RootElement, site);
        return new CmsResponse { List = items, Page = page, PageCount = items.Count > 0 ? page + 1 : page, Total = items.Count };
    }

    private async Task<VodItem> AppDetailAsync(SourceSite site, string id, CancellationToken token)
    {
        var endpoint = ResolveAppEndpoint(site) ?? throw new InvalidDataException("App 接口地址无效。");
        var headers = NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders());
        var url = AppUrl(endpoint, $"/video_detail?id={Uri.EscapeDataString(id)}&token=");
        var raw = await http.GetStringAsync(url, headers, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : doc.RootElement;
        if (data.TryGetProperty("vod_info", out var vodInfo) && vodInfo.ValueKind == JsonValueKind.Object) data = vodInfo;

        var item = ParseAppVod(data, site) ?? new VodItem { Id = id, SiteKey = site.Key, SiteName = site.Name, Name = id };
        if (data.TryGetProperty("vod_url_with_player", out var players) && players.ValueKind == JsonValueKind.Array)
        {
            var fromList = new List<string>();
            var urlList = new List<string>();
            foreach (var p in players.EnumerateArray())
            {
                var name = NativeSiteToolkit.FirstNonEmpty(
                    NativeSiteToolkit.ReadJsonString(p, "name"),
                    NativeSiteToolkit.ReadJsonString(p, "code"),
                    $"线路{fromList.Count + 1}");
                var parseApi = NativeSiteToolkit.ReadJsonString(p, "parse_api");
                var epRaw = NativeSiteToolkit.ReadJsonString(p, "url");
                if (epRaw.Length == 0) continue;
                if (parseApi.Length > 0 && Uri.TryCreate(parseApi, UriKind.Absolute, out _))
                {
                    var rewritten = string.Join('#', epRaw.Split('#', StringSplitOptions.RemoveEmptyEntries).Select(seg =>
                    {
                        var idx = seg.IndexOf('$');
                        if (idx < 0) return $"播放${parseApi}@@@{seg}";
                        return $"{seg[..idx]}${parseApi}@@@{seg[(idx + 1)..]}";
                    }));
                    epRaw = rewritten;
                }
                fromList.Add(NativeSiteToolkit.SanitizeLabel(name));
                urlList.Add(epRaw);
            }
            if (fromList.Count > 0)
            {
                item.PlayFrom = string.Join("$$$", fromList);
                item.PlayUrl = string.Join("$$$", urlList);
            }
        }
        return item;
    }

    private async Task<SpiderPlayResult> AppPlayAsync(SourceSite site, string flag, string id, CancellationToken token)
    {
        var headers = NativeSiteToolkit.CreateBrowserHeaders(null, site.GetHeaders());
        if (id.Contains("@@@", StringComparison.Ordinal))
        {
            var parts = id.Split("@@@", 2);
            var parseApi = parts[0].Trim();
            var mediaId = parts[1].Trim();
            if (PlayUrlResolver.IsDirectMediaUrl(mediaId))
                return new SpiderPlayResult(mediaId, headers, 0, 0);
            try
            {
                var sep = parseApi.Contains('?') ? "" : "";
                var raw = await http.GetStringAsync(parseApi + Uri.EscapeDataString(mediaId) + sep, headers, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);
                var resolved = NativeSiteToolkit.ReadJsonStringAny(doc.RootElement, "url", "playurl");
                if (resolved.Length == 0 && doc.RootElement.TryGetProperty("data", out var dataObj))
                    resolved = NativeSiteToolkit.ReadJsonStringAny(dataObj, "url", "playurl");
                if (resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return new SpiderPlayResult(resolved, headers, PlayUrlResolver.IsDirectMediaUrl(resolved) ? 0 : 1, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            id = mediaId;
        }
        return new SpiderPlayResult(id, headers, PlayUrlResolver.IsDirectMediaUrl(id) ? 0 : 1, 0);
    }

    private static List<VodItem> ExtractAppVodList(JsonElement root, SourceSite site)
    {
        var items = new List<VodItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddArray(JsonElement arr)
        {
            if (arr.ValueKind != JsonValueKind.Array) return;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.TryGetProperty("vlist", out var vlist) && vlist.ValueKind == JsonValueKind.Array)
                {
                    AddArray(vlist);
                    continue;
                }
                var item = ParseAppVod(el, site);
                if (item is not null && seen.Add(item.Id)) items.Add(item);
            }
        }

        if (root.TryGetProperty("list", out var list)) AddArray(list);
        if (items.Count == 0 && root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array) AddArray(data);
            else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("list", out var nested)) AddArray(nested);
        }
        return items;
    }

    private static VodItem? ParseAppVod(JsonElement el, SourceSite site)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = NativeSiteToolkit.ReadJsonStringAny(el, "vod_id", "id");
        var name = NativeSiteToolkit.ReadJsonStringAny(el, "vod_name", "name");
        if (id.Length == 0 || name.Length == 0) return null;
        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = name,
            Picture = NativeSiteToolkit.ReadJsonStringAny(el, "vod_pic", "pic"),
            Remarks = NativeSiteToolkit.ReadJsonStringAny(el, "vod_remarks", "remarks"),
            Year = NativeSiteToolkit.ReadJsonStringAny(el, "vod_year", "year"),
            Area = NativeSiteToolkit.ReadJsonStringAny(el, "vod_area", "area"),
            Actor = NativeSiteToolkit.ReadJsonStringAny(el, "vod_actor", "actor"),
            Director = NativeSiteToolkit.ReadJsonStringAny(el, "vod_director", "director"),
            TypeName = NativeSiteToolkit.ReadJsonStringAny(el, "type_name", "vod_class"),
            Content = PlaybackParsing.StripHtml(NativeSiteToolkit.ReadJsonStringAny(el, "vod_content", "vod_blurb")),
            PlayFrom = NativeSiteToolkit.ReadJsonString(el, "vod_play_from"),
            PlayUrl = NativeSiteToolkit.ReadJsonString(el, "vod_play_url")
        };
    }

    #endregion

    #region AList v3 Protocol

    private sealed record AListDrive(string Name, Uri Server, string RootPath, string Password);

    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".flv", ".ts", ".m3u8", ".webm", ".wmv", ".rmvb", ".mpg", ".mpeg", ".m4v"];

    private static List<AListDrive> ParseAListDrives(SourceSite site)
    {
        var drives = new List<AListDrive>();
        void TryAdd(string? name, string? serverRaw, string? password = null)
        {
            if (string.IsNullOrWhiteSpace(serverRaw)) return;
            if (!Uri.TryCreate(serverRaw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                return;
            var root = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
            var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            drives.Add(new AListDrive(string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(), root, path, password ?? string.Empty));
        }

        if (site.Ext.ValueKind == JsonValueKind.String)
        {
            var text = site.Ext.GetString()?.Trim() ?? string.Empty;
            if (text.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    ReadAListDrivesFromJson(doc.RootElement, TryAdd);
                    return drives;
                }
                catch (JsonException) { }
            }
            foreach (var part in text.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = part.IndexOf('$');
                if (idx > 0) TryAdd(part[..idx], part[(idx + 1)..]);
                else TryAdd(null, part);
            }
        }
        else if (site.Ext.ValueKind == JsonValueKind.Object)
        {
            ReadAListDrivesFromJson(site.Ext, TryAdd);
        }
        return drives;
    }

    private static void ReadAListDrivesFromJson(JsonElement root, Action<string?, string?, string?> add)
    {
        if (root.TryGetProperty("drives", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in arr.EnumerateArray())
            {
                add(NativeSiteToolkit.ReadJsonString(entry, "name"),
                    NativeSiteToolkit.ReadJsonStringAny(entry, "server", "url"),
                    NativeSiteToolkit.ReadJsonString(entry, "password"));
            }
        }
        else
        {
            add(NativeSiteToolkit.ReadJsonString(root, "name"),
                NativeSiteToolkit.ReadJsonStringAny(root, "server", "url"),
                NativeSiteToolkit.ReadJsonString(root, "password"));
        }
    }

    private async Task<CmsResponse> AListHomeAsync(SourceSite site, CancellationToken token)
    {
        var drives = ParseAListDrives(site);
        var categories = drives.Select((d, idx) => new VodCategory { Id = $"{idx}|{d.RootPath}", Name = d.Name }).ToList();
        var items = drives.Count > 0 ? (await AListCategoryAsync(site, categories[0].Id, 1, token).ConfigureAwait(false)).List : [];
        return new CmsResponse { Categories = categories, List = items, Page = 1, PageCount = 1, Total = items.Count };
    }

    private async Task<CmsResponse> AListCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken token)
    {
        var drives = ParseAListDrives(site);
        var parts = categoryId.Split('|', 2);
        var driveIdx = parts.Length > 0 && int.TryParse(parts[0], out var idx) && idx >= 0 && idx < drives.Count ? idx : 0;
        var path = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : drives[driveIdx].RootPath;
        var drive = drives[driveIdx];
        var body = JsonSerializer.Serialize(new { path, password = drive.Password, page = Math.Max(1, page), per_page = 60, refresh = false });
        var exchange = await http.PostJsonAsync(new Uri(drive.Server, "/api/fs/list").AbsoluteUri, body, NativeSiteToolkit.CreateBrowserHeaders(drive.Server), cancellationToken: token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(exchange.Payload.Content);
        var items = new List<VodItem>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in content.EnumerateArray())
            {
                var name = NativeSiteToolkit.ReadJsonString(entry, "name");
                var isDir = entry.TryGetProperty("is_dir", out var dirEl) && dirEl.ValueKind == JsonValueKind.True;
                if (name.Length == 0) continue;
                if (!isDir && !IsVideoFileName(name)) continue;
                var fullPath = path.TrimEnd('/') + "/" + name;
                var thumb = NativeSiteToolkit.ReadJsonString(entry, "thumb");
                items.Add(new VodItem
                {
                    Id = $"{driveIdx}|{(isDir ? "dir" : "file")}|{fullPath}",
                    SiteKey = site.Key,
                    SiteName = site.Name,
                    Name = name,
                    Picture = thumb,
                    Remarks = isDir ? "文件夹" : "视频"
                });
            }
        }
        return new CmsResponse { List = items, Page = page, PageCount = items.Count >= 60 ? page + 1 : page, Total = items.Count };
    }

    private async Task<CmsResponse> AListSearchAsync(SourceSite site, string keyword, int page, CancellationToken token)
    {
        var drives = ParseAListDrives(site);
        var items = new List<VodItem>();
        for (var i = 0; i < Math.Min(drives.Count, 3); i++)
        {
            var drive = drives[i];
            try
            {
                var body = JsonSerializer.Serialize(new { parent = drive.RootPath, keywords = keyword, scope = 0, page = Math.Max(1, page), per_page = 30, password = drive.Password });
                var exchange = await http.PostJsonAsync(new Uri(drive.Server, "/api/fs/search").AbsoluteUri, body, NativeSiteToolkit.CreateBrowserHeaders(drive.Server), cancellationToken: token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(exchange.Payload.Content);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in content.EnumerateArray())
                    {
                        var name = NativeSiteToolkit.ReadJsonString(entry, "name");
                        var parent = NativeSiteToolkit.ReadJsonString(entry, "parent");
                        var isDir = entry.TryGetProperty("is_dir", out var dirEl) && dirEl.ValueKind == JsonValueKind.True;
                        if (name.Length == 0 || (!isDir && !IsVideoFileName(name))) continue;
                        var fullPath = parent.TrimEnd('/') + "/" + name;
                        items.Add(new VodItem
                        {
                            Id = $"{i}|{(isDir ? "dir" : "file")}|{fullPath}",
                            SiteKey = site.Key,
                            SiteName = site.Name,
                            Name = name,
                            Remarks = drive.Name
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return new CmsResponse { List = items, Page = page, PageCount = 1, Total = items.Count };
    }

    private async Task<VodItem> AListDetailAsync(SourceSite site, string id, CancellationToken token)
    {
        var drives = ParseAListDrives(site);
        var parts = id.Split('|', 3);
        if (parts.Length < 3 || !int.TryParse(parts[0], out var driveIdx) || driveIdx < 0 || driveIdx >= drives.Count)
            throw new InvalidDataException("AList 资源标识无效。");
        var kind = parts[1];
        var path = parts[2];
        var drive = drives[driveIdx];
        var title = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? drive.Name;
        var episodes = new List<string>();

        if (kind == "file")
        {
            episodes.Add($"{NativeSiteToolkit.SanitizeLabel(title)}${driveIdx}|{path}");
        }
        else
        {
            var body = JsonSerializer.Serialize(new { path, password = drive.Password, page = 1, per_page = 200, refresh = false });
            var exchange = await http.PostJsonAsync(new Uri(drive.Server, "/api/fs/list").AbsoluteUri, body, NativeSiteToolkit.CreateBrowserHeaders(drive.Server), cancellationToken: token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(exchange.Payload.Content);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in content.EnumerateArray())
                {
                    var name = NativeSiteToolkit.ReadJsonString(entry, "name");
                    var isDir = entry.TryGetProperty("is_dir", out var dirEl) && dirEl.ValueKind == JsonValueKind.True;
                    if (isDir || !IsVideoFileName(name)) continue;
                    var fullPath = path.TrimEnd('/') + "/" + name;
                    episodes.Add($"{NativeSiteToolkit.SanitizeLabel(name)}${driveIdx}|{fullPath}");
                }
            }
        }

        return new VodItem
        {
            Id = id,
            SiteKey = site.Key,
            SiteName = site.Name,
            Name = title,
            PlayFrom = episodes.Count > 0 ? drive.Name : string.Empty,
            PlayUrl = string.Join('#', episodes)
        };
    }

    private async Task<SpiderPlayResult> AListPlayAsync(SourceSite site, string id, CancellationToken token)
    {
        var drives = ParseAListDrives(site);
        var parts = id.Split('|', 2);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var driveIdx) || driveIdx < 0 || driveIdx >= drives.Count)
            throw new InvalidDataException("AList 播放标识无效。");
        var drive = drives[driveIdx];
        var body = JsonSerializer.Serialize(new { path = parts[1], password = drive.Password });
        var exchange = await http.PostJsonAsync(new Uri(drive.Server, "/api/fs/get").AbsoluteUri, body, NativeSiteToolkit.CreateBrowserHeaders(drive.Server), cancellationToken: token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(exchange.Payload.Content);
        var rawUrl = doc.RootElement.TryGetProperty("data", out var data) ? NativeSiteToolkit.ReadJsonString(data, "raw_url") : string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
            throw new InvalidDataException("AList 未返回直链地址。");
        return new SpiderPlayResult(rawUrl, NativeSiteToolkit.CreateBrowserHeaders(drive.Server), 0, 0);
    }

    private static bool IsVideoFileName(string name) =>
        VideoExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    #endregion
}
