namespace TV.Models;

public enum CloudProviderKind { Ali, Quark, UC, Baidu }

public enum CloudAccountState { NotLoggedIn, Saved, Checking, LoggedIn, Expired, Error }

public sealed record CloudAccountInfo(
    CloudProviderKind Provider,
    CloudAccountState State,
    string Status,
    string AccountName = "")
{
    public string Name => DisplayName(Provider);
    public override string ToString() => Name;

    public static string DisplayName(CloudProviderKind provider) => provider switch
    {
        CloudProviderKind.Ali => "阿里云盘",
        CloudProviderKind.Quark => "夸克网盘",
        CloudProviderKind.UC => "UC 网盘",
        CloudProviderKind.Baidu => "百度网盘",
        _ => provider.ToString()
    };
    public string LoginAutomationName => Name + "扫码";
    public bool IsLoggedIn => State == CloudAccountState.LoggedIn;
    public bool IsWarning => State is CloudAccountState.Expired or CloudAccountState.Error;
    public bool IsNeutral => !IsLoggedIn && !IsWarning;
}

public sealed class CloudAuthenticationException : InvalidOperationException
{
    public CloudAuthenticationException(CloudProviderKind provider, string message, Exception? inner = null)
        : base(message, inner) => Provider = provider;

    public CloudProviderKind Provider { get; }
}

public sealed record CloudFile(
    CloudProviderKind Provider,
    string ShareId,
    string FileId,
    string FileToken,
    string Name,
    long Size,
    string ParentName = "",
    string Passcode = "",
    string ShareFolderId = "",
    string ParentDirectoryId = "",
    string DrivePath = "");

public sealed class CloudShare
{
    public required CloudProviderKind Provider { get; init; }
    public required string ShareId { get; init; }
    public required string SourceUrl { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Picture { get; init; } = string.Empty;
    public List<CloudFile> Files { get; init; } = [];
    public List<CloudFile> SubtitleFiles { get; init; } = [];
}

public sealed record CloudSubtitleTrack(string Name, Uri Source);

public sealed record CloudPlayResult(string Url, IReadOnlyDictionary<string, string> Headers, string? ContentType = null,
    string? FileExtension = null, IReadOnlyList<CloudSubtitleTrack>? SubtitleTracks = null)
{
    public bool UnwrapPngHlsSegments { get; init; }
    public IReadOnlySet<int>? AvailableVideoHeights { get; init; }
    public IReadOnlyList<QualityOption>? AvailableQualities { get; init; }
    public QualityOption? ResolvedQuality { get; init; }
    internal bool UseBoundedRanges { get; init; }
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(45);
}

public sealed record CloudDriveItem(
    CloudProviderKind Provider,
    string Id,
    string ParentId,
    string Name,
    string Path,
    long Size,
    bool IsFolder,
    DateTimeOffset? ModifiedAt = null,
    string Token = "")
{
    public string Glyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public bool IsVideoFile => !IsFolder && IsVideoExtension(Name);
    public bool IsGenericFile => !IsFolder && !IsVideoFile;
    public string MetaText => IsFolder ? "文件夹" : SizeText;
    public string SizeText => Size <= 0 ? "" : Size >= 1024 * 1024 * 1024 ? $"{Size / 1024d / 1024 / 1024:0.##} GB" : $"{Size / 1024d / 1024:0.##} MB";
    public string ModifiedText => ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static bool IsVideoExtension(string name)
    {
        var ext = System.IO.Path.GetExtension(name);
        return ext.Length > 0 && ext.ToLowerInvariant() is
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".ts" or ".m2ts" or
            ".m3u8" or ".webm" or ".rmvb" or ".m4v" or ".mpg" or ".mpeg" or ".iso" or ".asf" or ".3gp";
    }
}

public sealed record CloudDriveListing(IReadOnlyList<CloudDriveItem> Items, bool HasMore);

public sealed record CloudDriveFolder(string Id, string Path, string Name);

public sealed class CloudLoginSession
{
    public required string Id { get; init; }
    public required CloudProviderKind Provider { get; init; }
    public required string Content { get; init; }
    public required byte[] QrPng { get; init; }
    public string Status { get; set; } = "等待扫码";
    public string Credential { get; set; } = string.Empty;
    public bool IsWebLogin { get; init; }
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(3);
}
