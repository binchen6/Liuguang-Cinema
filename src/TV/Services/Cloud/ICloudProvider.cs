using TV.Models;

namespace TV.Services.Cloud;

public interface ICloudProvider
{
    CloudProviderKind Kind { get; }
    bool CanHandle(string url);
    Task<CloudShare> GetShareAsync(string url, CancellationToken cancellationToken = default);
    Task<CloudPlayResult> PlayAsync(CloudFile file, string quality, CancellationToken cancellationToken = default);
    Task<CloudPlayResult> PlayAsync(CloudFile file, QualityOption quality, CancellationToken cancellationToken = default) =>
        PlayAsync(file, quality.RequestsOriginal ? "原画" : quality.Name, cancellationToken);
}

public interface ICloudDriveProvider : ICloudProvider
{
    Task<CloudDriveListing> ListFolderAsync(string folderId, string path, int page, CancellationToken cancellationToken = default);
    Task<CloudDriveListing> SearchFolderAsync(string keyword, int page, CancellationToken cancellationToken = default);
}
