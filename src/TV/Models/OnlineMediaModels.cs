namespace TV.Models;

public sealed record OnlineMediaMatch(string Title, string EpisodeTitle, long Aid, long Cid)
{
    public Uri Danmaku => new($"https://api.bilibili.com/x/v1/dm/list.so?oid={Cid}");
}

public sealed record OnlineSubtitleTrack(string Name, Uri Address);

public sealed record LocalMediaEntry(string Path, string Title, string Folder, long Size, DateTimeOffset ModifiedAt);
