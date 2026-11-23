using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;

namespace TV.Services.Metadata;

public sealed class DoubanService
{
    private static readonly Dictionary<string, string> BrowserHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
        ["Accept-Language"] = "zh-CN,zh;q=0.9"
    };

    private readonly SourceHttpClient _http;
    private readonly DatabaseService _database;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<DoubanMetadata?>> _pending = new();
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public DoubanService(SourceHttpClient http, DatabaseService database)
    {
        _http = http;
        _database = database;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        !string.Equals(await _database.GetSettingAsync("douban.enabled", cancellationToken).ConfigureAwait(false), "false", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<VodItem>> GetTrendingAsync(CancellationToken token)
    {
        const string key = "douban.trending.movies";
        var cached = await _database.GetMetadataAsync(key, token).ConfigureAwait(false);
        var previous = cached is null ? [] : ReadTrending(cached.Json);
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return previous;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var json = await ThrottledGetStringAsync("https://movie.douban.com/j/search_subjects?type=movie&tag=%E7%83%AD%E9%97%A8&page_limit=24&page_start=0", deadline.Token).ConfigureAwait(false);
            var items = ReadTrending(json);
            if (items.Count == 0) return previous;
            await _database.SaveMetadataAsync(key, json, DateTimeOffset.UtcNow.AddHours(6), deadline.Token).ConfigureAwait(false);
            return items;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            Diagnostics.Log($"豆瓣推荐不可用: {ex.Message}");
            return previous;
        }
    }

    internal static IReadOnlyList<VodItem> ReadTrending(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array) return [];
            var items = new List<VodItem>();
            foreach (var subject in subjects.EnumerateArray().Take(40))
            {
                var id = ReadSubjectString(subject, "id");
                var title = ReadSubjectString(subject, "title");
                if (id.Length == 0 || !id.All(char.IsAsciiDigit) || title.Length == 0) continue;
                items.Add(new VodItem
                {
                    Id = "msearch:douban:" + id, Name = title, DoubanId = id, SiteName = "豆瓣推荐",
                    Rating = ReadSubjectString(subject, "rate"), Picture = ReadSubjectString(subject, "cover")
                });
            }
            return items;
        }
        catch (JsonException) { return []; }
    }

    private static string ReadSubjectString(JsonElement subject, string name) =>
        subject.ValueKind == JsonValueKind.Object && subject.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    public Task<DoubanMetadata?> GetMetadataAsync(VodItem item, CancellationToken cancellationToken = default)
    {
        var title = PlaybackParsing.CleanTitle(item.Name);
        var key = $"{Normalize(title)}|{item.Year}";
        return _pending.GetOrAdd(key, _ => GetCoreAsync(key, title, item.Year, CancellationToken.None)).WaitAsync(cancellationToken);
    }

    public async Task<DoubanMetadata?> GetCachedMetadataAsync(VodItem item, CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false)) return null;
        var title = PlaybackParsing.CleanTitle(item.Name);
        var cached = await _database.GetMetadataAsync($"{Normalize(title)}|{item.Year}", cancellationToken).ConfigureAwait(false);
        return ReadMetadata(cached?.Json);
    }

    private static DoubanMetadata? ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DoubanMetadata>(json, JsonUtil.Options); }
        catch (JsonException) { return null; }
    }

    private async Task<DoubanMetadata?> GetCoreAsync(string key, string title, string year, CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false)) return null;
            var cached = await _database.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
                return ReadMetadata(cached.Json);

            try
            {
                var metadata = await FetchAsync(title, year, cancellationToken).ConfigureAwait(false);
                var json = metadata is null ? string.Empty : JsonSerializer.Serialize(metadata, JsonUtil.Options);
                var expiry = DateTimeOffset.UtcNow.Add(metadata is null ? TimeSpan.FromHours(12) : TimeSpan.FromDays(30));
                await _database.SaveMetadataAsync(key, json, expiry, cancellationToken).ConfigureAwait(false);
                return metadata;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (cached is not null && !string.IsNullOrWhiteSpace(cached.Json))
                    return JsonSerializer.Deserialize<DoubanMetadata>(cached.Json, JsonUtil.Options);
                await _database.SaveMetadataAsync(key, string.Empty, DateTimeOffset.UtcNow.AddHours(3), cancellationToken).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"豆瓣元数据获取失败: {ex.Message}");
                return null;
            }
        }
        finally { _pending.TryRemove(key, out _); }
    }

    private async Task<DoubanMetadata?> FetchAsync(string title, string year, CancellationToken cancellationToken)
    {
        var candidates = await GetSuggestionsAsync(title, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) candidates = await SearchAsync(title, cancellationToken).ConfigureAwait(false);
        var best = candidates.Select(candidate => new { Candidate = candidate, Score = Score(candidate, title, year) })
            .OrderByDescending(item => item.Score).FirstOrDefault();
        if (best is null || best.Score < 80) return null;

        var subjectUrl = $"https://movie.douban.com/subject/{best.Candidate.Id}/";
        var html = await ThrottledGetStringAsync(subjectUrl, cancellationToken).ConfigureAwait(false);
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        var document = await context.OpenAsync(request => request.Content(html).Address(subjectUrl), cancellationToken).ConfigureAwait(false);
        var rating = document.QuerySelector("strong[property='v:average']")?.TextContent.Trim()
            ?? document.QuerySelector("[property='v:average']")?.TextContent.Trim() ?? string.Empty;
        if (decimal.TryParse(rating, out var score)) rating = score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var picture = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
            ?? document.QuerySelector("#mainpic img")?.GetAttribute("src") ?? best.Candidate.Picture;
        var summary = document.QuerySelector("span[property='v:summary']")?.TextContent.Trim()
            ?? document.QuerySelector("meta[property='og:description']")?.GetAttribute("content") ?? string.Empty;
        summary = Regex.Replace(summary, @"\s+", " ");
        return new DoubanMetadata
        {
            SubjectId = best.Candidate.Id,
            Title = document.QuerySelector("span[property='v:itemreviewed']")?.TextContent.Trim() ?? best.Candidate.Title,
            Picture = picture,
            Rating = rating,
            Summary = summary
        };
    }

    private async Task<List<SubjectCandidate>> GetSuggestionsAsync(string title, CancellationToken cancellationToken)
    {
        var url = "https://movie.douban.com/j/subject_suggest?q=" + Uri.EscapeDataString(title);
        var json = await ThrottledGetStringAsync(url, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var result = new List<SubjectCandidate>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            result.Add(new SubjectCandidate(
                item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("title", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("year", out var year) ? year.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("img", out var image) ? image.GetString() ?? string.Empty : string.Empty));
        }
        return result.Where(item => item.Id.Length > 0 && item.Title.Length > 0).ToList();
    }

    private async Task<List<SubjectCandidate>> SearchAsync(string title, CancellationToken cancellationToken)
    {
        var url = "https://www.douban.com/search?cat=1002&q=" + Uri.EscapeDataString(title);
        var html = await ThrottledGetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        var document = await context.OpenAsync(request => request.Content(html).Address(url), cancellationToken).ConfigureAwait(false);
        var result = new List<SubjectCandidate>();
        foreach (var anchor in document.QuerySelectorAll(".result h3 a, .result .content h3 a"))
        {
            var href = ResolveLink(anchor.GetAttribute("href") ?? string.Empty);
            var match = Regex.Match(href, @"movie\.douban\.com/subject/(\d+)");
            if (!match.Success) continue;
            var parent = anchor.Closest(".result");
            result.Add(new SubjectCandidate(match.Groups[1].Value, anchor.TextContent.Trim(), Regex.Match(parent?.TextContent ?? string.Empty, @"(19|20)\d{2}").Value, string.Empty));
        }
        return result;
    }

    private async Task<string> ThrottledGetStringAsync(string url, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < TimeSpan.FromMilliseconds(900))
                await Task.Delay(TimeSpan.FromMilliseconds(900) - elapsed, cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(BrowserHeaders, StringComparer.OrdinalIgnoreCase)
            {
                ["Referer"] = "https://www.douban.com/"
            };
            var value = await _http.GetStringAsync(url, headers, cancellationToken).ConfigureAwait(false);
            _lastRequest = DateTimeOffset.UtcNow;
            return value;
        }
        finally { _requestLock.Release(); }
    }

    private static int Score(SubjectCandidate candidate, string title, string year)
    {
        var expected = Normalize(title);
        var actual = Normalize(PlaybackParsing.CleanTitle(candidate.Title));
        var score = actual == expected ? 90 : actual.Contains(expected) || expected.Contains(actual) ? 55 : 0;
        var expectedYear = 0;
        var actualYear = 0;
        var expectedYearValid = year.Length >= 4 && int.TryParse(year[..4], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out expectedYear);
        var actualYearValid = candidate.Year.Length >= 4 && int.TryParse(candidate.Year[..4], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out actualYear);
        if (expectedYearValid && actualYearValid && candidate.Year == year[..4]) score += 20;
        else if (expectedYearValid && actualYearValid && Math.Abs(expectedYear - actualYear) <= 1) score += 8;
        else if (expectedYearValid && actualYearValid) score -= 25;
        return score;
    }

    private static string Normalize(string value) => Regex.Replace(value, @"[\s\p{P}\p{S}]", string.Empty).ToLowerInvariant();

    private static string ResolveLink(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return href;
        if (!uri.Host.Equals("www.douban.com", StringComparison.OrdinalIgnoreCase)) return href;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query["url"] ?? href;
    }

    private sealed record SubjectCandidate(string Id, string Title, string Year, string Picture);
}
