using TV.Models;
using TV.Services.Cloud;

namespace TV.Services.Vod.Native;

public sealed class MyDriveVodProvider(CloudDriveService cloud) : INativeVodProvider
{
    public static bool Supports(SourceSite site) => site.Type == 3 &&
        (site.Api.Equals("csp_MyDriveGuard", StringComparison.OrdinalIgnoreCase) || site.Api.Equals("csp_MyDrive", StringComparison.OrdinalIgnoreCase));

    public bool CanHandle(SourceSite site) => Supports(site);

    public Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CmsResponse
        {
            Categories = Enum.GetValues<CloudProviderKind>().Select(provider => new VodCategory { Id = provider.ToString(), Name = Name(provider) }).ToList(),
            HomeCategoryName = "我的云盘"
        });

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string categoryId, int page, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<CloudProviderKind>(categoryId, true, out var provider)) return new CmsResponse();
        var root = provider == CloudProviderKind.Ali ? "root" : "0";
        var listing = await cloud.ListFolderAsync(provider, root, "/", page, cancellationToken).ConfigureAwait(false);
        return new CmsResponse
        {
            Page = page, PageCount = listing.HasMore ? page + 1 : page,
            List = listing.Items.Where(item => !item.IsFolder).Select(item => Item(site, item)).ToList()
        };
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new CmsResponse();
        var providers = Enum.GetValues<CloudProviderKind>();
        var results = await Task.WhenAll(providers.Select(async provider =>
        {
            if ((await cloud.GetAccountAsync(provider, cancellationToken).ConfigureAwait(false)).State == CloudAccountState.NotLoggedIn)
                return Array.Empty<CloudDriveItem>();
            try
            {
                var listing = await cloud.SearchFolderAsync(provider, keyword, page, cancellationToken).ConfigureAwait(false);
                return listing.Items.Where(item => !item.IsFolder).ToArray();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Services.Diagnostics.Log($"我的云盘搜索失败 ({provider}): {ex.GetType().Name}");
                return Array.Empty<CloudDriveItem>();
            }
        })).ConfigureAwait(false);
        var items = results.SelectMany(items => items).Select(item => Item(site, item)).ToList();
        return new CmsResponse { List = items, Total = items.Count, Page = page, PageCount = page };
    }

    public Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(id, UriKind.Absolute, out var uri) || uri.Scheme != "cloud") throw new InvalidDataException("个人云盘文件地址无效。");
        var name = CloudShareLink.ReadQuery(uri, "name");
        return Task.FromResult(new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name, Name = name,
            TypeName = "我的云盘", PlayFrom = "我的云盘", PlayUrl = name + "$" + id
        });
    }

    private static VodItem Item(SourceSite site, CloudDriveItem item)
    {
        var file = new CloudFile(item.Provider, "drive", item.Id, item.Token, item.Name, item.Size,
            ParentDirectoryId: item.ParentId, DrivePath: item.Path);
        var id = CloudDriveService.GetPlaybackId(file);
        return new VodItem
        {
            Id = id, SiteKey = site.Key, SiteName = site.Name, Name = item.Name,
            TypeName = "我的云盘", Remarks = Name(item.Provider) + " · 个人文件",
            PlayFrom = Name(item.Provider), PlayUrl = CloudDriveService.SafeEpisodeName(item.Name) + "$" + id
        };
    }

    private static string Name(CloudProviderKind provider) => provider switch
    {
        CloudProviderKind.Ali => "阿里云盘",
        CloudProviderKind.Quark => "夸克网盘",
        CloudProviderKind.UC => "UC 网盘",
        CloudProviderKind.Baidu => "百度网盘",
        _ => provider.ToString()
    };
}
