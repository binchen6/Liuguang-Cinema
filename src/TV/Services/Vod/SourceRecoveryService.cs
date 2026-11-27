using System.Collections.Concurrent;
using TV.Models;
using TV.Services.Playback;

namespace TV.Services.Vod;

public sealed class SourceRecoveryService(AppServices services)
{
    public async Task<IReadOnlyList<VodItem>> FindAsync(VodItem source, CancellationToken token, int limit = 6)
    {
        var config = services.Config.Current ?? await services.Config.LoadAsync(false, token);
        var wanted = PlaybackParsing.CleanTitle(source.Name);
        if (wanted.Length == 0) return [];
        var sites = config.Sites.Where(site => site.IsSupported && site.Searchable != 0 && site.Key != source.SiteKey &&
            !Native.MyDriveVodProvider.Supports(site)).Take(12);
        var found = new ConcurrentBag<(VodItem Item, double Score)>();
        await Parallel.ForEachAsync(sites, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token }, async (site, cancellationToken) =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 5, 12)));
                var result = await services.Vod.SearchAsync(site, source.Name, 1, deadline.Token);
                foreach (var item in result.List.Take(8))
                {
                    if (!Matches(source, item, wanted)) continue;
                    var detail = await services.Vod.GetDetailAsync(site, item.Id, cancellationToken);
                    if (string.IsNullOrWhiteSpace(detail.PlayUrl)) continue;
                    detail.SiteKey = site.Key;
                    detail.SiteName = site.Name;
                    if (string.IsNullOrWhiteSpace(detail.Picture)) detail.Picture = source.BestPicture;
                    if (string.IsNullOrWhiteSpace(detail.Content)) detail.Content = source.Description;
                    found.Add((detail, SearchScoring.Score(source.Name, detail)));
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Diagnostics.Log($"换源检索失败 ({site.Key}): {ex.GetType().Name}");
            }
        });
        return found.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Item)
            .DistinctBy(item => item.SiteKey, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
    }

    public PlaybackRequest Switch(PlaybackRequest current, VodItem source)
    {
        var lines = PlaybackParsing.ParseLines(source);
        if (lines.Count == 0) throw new InvalidDataException("新来源没有可播放线路。");
        var oldLine = current.Lines.ElementAtOrDefault(current.LineIndex);
        var oldEpisode = oldLine?.Episodes.ElementAtOrDefault(current.EpisodeIndex);
        var lineIndex = oldLine is null ? 0 : lines.FindIndex(line => line.Name == oldLine.Name);
        if (lineIndex < 0) lineIndex = 0;
        var line = lines[lineIndex];
        var episodeIndex = oldEpisode is null ? Math.Min(current.EpisodeIndex, line.Episodes.Count - 1)
            : line.Episodes.FindIndex(episode => episode.Name == oldEpisode.Name);
        if (episodeIndex < 0) episodeIndex = Math.Clamp(current.EpisodeIndex, 0, line.Episodes.Count - 1);
        if (line.Episodes.Count == 0) throw new InvalidDataException("新来源没有可播放集数。");
        var episode = line.Episodes[episodeIndex];
        var site = services.Config?.FindSite(source.SiteKey);
        var copy = current.Copy();
        copy.Item = source;
        copy.SiteKey = source.SiteKey;
        copy.VodId = source.Id;
        copy.Url = episode.Url;
        copy.Lines = lines;
        copy.LineIndex = lineIndex;
        copy.EpisodeIndex = episodeIndex;
        copy.Subtitle = $"{source.SiteName} · {line.Name} · {episode.Name}";
        copy.Picture = source.BestPicture;
        copy.Headers = site?.GetHeaders().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        copy.Subtitles = [];
        copy.AudioTracks = [];
        copy.Danmaku = null;
        copy.AvailableQualities = null;
        copy.AvailableVideoHeights = null;
        copy.ResolvedQuality = null;
        copy.HasFixedVideoTrack = false;
        copy.UsesProviderVideoHeights = false;
        return copy;
    }

    private static bool Matches(VodItem source, VodItem candidate, string wanted)
    {
        if (SearchScoring.Normalize(PlaybackParsing.CleanTitle(candidate.Name)) != SearchScoring.Normalize(wanted) || candidate.Id.Length == 0) return false;
        if (source.Year.Length == 4 && candidate.Year.Length == 4 && source.Year != candidate.Year) return false;
        if (source.TypeName.Length > 0 && candidate.TypeName.Length > 0 &&
            !SearchScoring.MatchesType(candidate, source.TypeName) && !SearchScoring.MatchesType(source, candidate.TypeName)) return false;
        return true;
    }
}
