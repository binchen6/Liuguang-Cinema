using TV.Models;
using TV.Services.Cloud;

namespace TV.Services.Vod.Native;

internal static class CloudSearchDetail
{
    public static async Task<VodItem> ReadAsync(CloudDriveService cloud, SourceSite site, string id, string url,
        string? title, CancellationToken token)
    {
        if (!CloudShareLink.TryParse(url, out var link)) throw new InvalidDataException("网盘分享地址无效。");
        CloudShare share;
        try { share = await cloud.GetShareAsync(link.ToShareUrl(), token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is CloudAuthenticationException or HttpRequestException or InvalidDataException or InvalidOperationException)
        {
            var hint = ex is CloudAuthenticationException ? "登录后加载分享" : $"需登录或检查分享（{ProviderName(link.Provider)}）";
            return new VodItem
            {
                Id = id, SiteKey = site.Key, SiteName = site.Name,
                Name = string.IsNullOrWhiteSpace(title) ? ProviderName(link.Provider) + "分享" : title,
                Remarks = ProviderName(link.Provider), PlayFrom = ProviderName(link.Provider),
                PlayUrl = hint + "$" + link.ToShareUrl(),
                Content = ex.Message
            };
        }
        if (share.Files.Count == 0) throw new InvalidDataException("分享中没有可播放的媒体文件。");
        var name = share.Name;
        if (string.IsNullOrWhiteSpace(name) || name == $"{link.Provider} 分享")
            name = Path.GetFileNameWithoutExtension(share.Files[0].Name);
        var provider = ProviderName(link.Provider);
        return new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name, Name = string.IsNullOrWhiteSpace(title) ? name : title,
            Remarks = provider, PlayFrom = provider,
            PlayUrl = string.Join('#', share.Files.Select(CloudDriveService.FormatEpisode))
        };
    }

    public static string ProviderName(CloudProviderKind provider) => provider switch
    {
        CloudProviderKind.Quark => "夸克网盘",
        CloudProviderKind.UC => "UC 网盘",
        CloudProviderKind.Baidu => "百度网盘",
        _ => "阿里云盘"
    };
}
