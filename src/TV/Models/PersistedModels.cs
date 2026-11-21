namespace TV.Models;

public sealed record DownloadEntry(string FilePath, string Title, string Episode);

public sealed class HistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Picture { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public double Progress => DurationMs > 0 ? Math.Clamp(PositionMs * 100d / DurationMs, 0, 100) : 0;
    public bool HasProgress => DurationMs > 0;
    public bool IsCompleted => PositionMs > 0 && DurationMs > 0 &&
        PositionMs >= Math.Max(DurationMs - 3000, DurationMs * 0.95);
}

public sealed class FavoriteEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Picture { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
    public string DataJson { get; set; } = string.Empty;
}

public sealed class MetadataCacheEntry
{
    public string CacheKey { get; set; } = string.Empty;
    public string Json { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
}
