using System.Text.Json;
using System.Text.Json.Serialization;

namespace TV.Models;

public sealed partial class VodCategory : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [JsonPropertyName("type_id"), JsonConverter(typeof(FlexibleStringConverter))] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type_name")] public string Name { get; set; } = string.Empty;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, JsonIgnore] private bool _isSelected;
    public override string ToString() => Name;
}

public sealed class VodItem
{
    [JsonPropertyName("vod_id"), JsonConverter(typeof(FlexibleStringConverter))] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("vod_name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("vod_pic")] public string Picture { get; set; } = string.Empty;
    [JsonPropertyName("vod_remarks")] public string Remarks { get; set; } = string.Empty;
    [JsonPropertyName("vod_year")] public string Year { get; set; } = string.Empty;
    [JsonPropertyName("vod_area")] public string Area { get; set; } = string.Empty;
    [JsonPropertyName("vod_score"), JsonConverter(typeof(FlexibleStringConverter))] public string SiteRating { get; set; } = string.Empty;
    [JsonPropertyName("vod_director")] public string Director { get; set; } = string.Empty;
    [JsonPropertyName("vod_actor")] public string Actor { get; set; } = string.Empty;
    [JsonPropertyName("vod_content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("vod_blurb")] public string Blurb { get; set; } = string.Empty;
    [JsonPropertyName("type_id"), JsonConverter(typeof(FlexibleStringConverter))] public string TypeId { get; set; } = string.Empty;
    [JsonPropertyName("type_name")] public string TypeName { get; set; } = string.Empty;
    [JsonPropertyName("vod_play_from")] public string PlayFrom { get; set; } = string.Empty;
    [JsonPropertyName("vod_play_url")] public string PlayUrl { get; set; } = string.Empty;
    [JsonPropertyName("vod_lyrics")] public string? Lyrics { get; set; }

    public string SiteKey { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    [JsonIgnore] public string? DoubanId { get; set; }
    [JsonIgnore] public string? DoubanPicture { get; set; }
    private string? _enrichedRating;
    [JsonIgnore] public string? Rating
    {
        get => string.IsNullOrWhiteSpace(SiteRating) ? _enrichedRating : SiteRating;
        set => _enrichedRating = value;
    }
    [JsonIgnore] public string? CloudProvider { get; set; }
    [JsonIgnore] public List<SourceShareEntry> SourceShares { get; } = [];
    [JsonIgnore] public bool HasSourceShares => SourceShares.Count != 0;
    [JsonIgnore] public string BestPicture => string.IsNullOrWhiteSpace(Picture) ? DoubanPicture ?? string.Empty : Picture;
    [JsonIgnore] public string Description => string.IsNullOrWhiteSpace(Content) ? Blurb : Content;

    public VodItem Copy() => (VodItem)MemberwiseClone();
}

public sealed record SourceShareEntry(string Name, Uri Address, string Status);

public sealed class CmsResponse
{
    [JsonPropertyName("class")] public List<VodCategory> Categories { get; set; } = [];
    [JsonPropertyName("list")] public List<VodItem> List { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HomeCategoryName { get; set; }
    [JsonPropertyName("page"), JsonConverter(typeof(FlexibleIntConverter))] public int Page { get; set; }
    [JsonPropertyName("pagecount"), JsonConverter(typeof(FlexibleIntConverter))] public int PageCount { get; set; }
    [JsonPropertyName("total"), JsonConverter(typeof(FlexibleIntConverter))] public int Total { get; set; }
    [JsonPropertyName("limit"), JsonConverter(typeof(FlexibleIntConverter))] public int Limit { get; set; }
    [JsonPropertyName("filters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public JsonElement Filters { get; set; }
}

public sealed class Episode
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public override string ToString() => Name;
}

public sealed class PlaybackLine
{
    public string Name { get; set; } = string.Empty;
    public List<Episode> Episodes { get; set; } = [];
}

public enum PlaybackKind
{
    Vod,
    Live
}

public sealed class PlaybackRequest
{
    public PlaybackKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string SiteKey { get; set; } = string.Empty;
    public string VodId { get; set; } = string.Empty;
    public string? Picture { get; set; }
    public long StartPositionMs { get; set; }
    public bool IsMusic { get; set; }
    public string? MusicCover { get; set; }
    public string? MusicLyrics { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public VodItem? Item { get; set; }
    public List<PlaybackLine> Lines { get; set; } = [];
    public int LineIndex { get; set; }
    public int EpisodeIndex { get; set; }
    public LiveChannel? Channel { get; init; }
    public List<Uri> Subtitles { get; set; } = [];
    public List<Uri> AudioTracks { get; set; } = [];
    [JsonIgnore] public IReadOnlyList<CloudSubtitleTrack> CloudSubtitles { get; set; } = [];
    [JsonIgnore] internal IReadOnlySet<int>? AvailableVideoHeights { get; set; }
    [JsonIgnore] internal IReadOnlyList<QualityOption>? AvailableQualities { get; set; }
    [JsonIgnore] internal QualityOption? ResolvedQuality { get; set; }
    [JsonIgnore] internal string? Notice { get; set; }
    [JsonIgnore] internal bool UsesProviderVideoHeights { get; set; }
    [JsonIgnore] internal bool HasFixedVideoTrack { get; set; }
    [JsonIgnore] internal bool SeekOnAudioTrackChange { get; set; }
    [JsonIgnore] internal bool UseAvformatDemuxer { get; set; }
    public Uri? Danmaku { get; set; }
    public bool RecoverMissingSource { get; set; }

    public PlaybackRequest Copy() => new()
    {
        Kind = Kind, Title = Title, Subtitle = Subtitle, Url = Url, SiteKey = SiteKey, VodId = VodId,
        Picture = Picture, StartPositionMs = StartPositionMs, Headers = new(Headers, StringComparer.OrdinalIgnoreCase),
        IsMusic = IsMusic, MusicCover = MusicCover, MusicLyrics = MusicLyrics,
        Item = Item, Lines = Lines, LineIndex = LineIndex, EpisodeIndex = EpisodeIndex, Channel = Channel,
        Subtitles = [.. Subtitles], AudioTracks = [.. AudioTracks], CloudSubtitles = CloudSubtitles, Danmaku = Danmaku,
        RecoverMissingSource = RecoverMissingSource,
        AvailableVideoHeights = AvailableVideoHeights, AvailableQualities = AvailableQualities, ResolvedQuality = ResolvedQuality,
        Notice = Notice,
        UsesProviderVideoHeights = UsesProviderVideoHeights, HasFixedVideoTrack = HasFixedVideoTrack,
        SeekOnAudioTrackChange = SeekOnAudioTrackChange, UseAvformatDemuxer = UseAvformatDemuxer
    };
}

public sealed record QualityOption(string Name, int Height)
{
    public int? ProviderId { get; init; }
    public int? FrameRate { get; init; }
    public bool IsOriginal { get; init; }
    public bool IsAvailable { get; init; } = true;

    internal bool RequestsOriginal => IsOriginal || Height < 0;
    internal bool IsSpecific => ProviderId.HasValue || Height > 0;
}

public sealed class LiveChannel
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Logo { get; set; } = string.Empty;
    public string EpgId { get; set; } = string.Empty;
    public string EpgName { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string EpgUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DoubanMetadata
{
    public string SubjectId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Picture { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}
