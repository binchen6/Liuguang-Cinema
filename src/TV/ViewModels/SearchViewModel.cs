using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TV.Models;
using TV.Services;
using TV.Services.Http;
using TV.Services.Vod;

namespace TV.ViewModels;

public sealed partial class SearchScope(string name, SourceSite? site = null) : ObservableObject
{
    public string Name { get; } = name;
    public SourceSite? Site { get; } = site;
    [ObservableProperty] private SearchGroup? _group;
}
internal enum SiteSearchState { Queued, Searching, Finished, Failed, Paused }

public sealed partial class SearchGroup(SourceSite site) : ObservableObject
{
    internal SourceSite Site { get; } = site;
    internal IReadOnlyList<VodCardViewModel> Hits { get; set; } = [];
    internal SiteSearchState State { get; set; }
    internal bool IsPending => State is SiteSearchState.Queued or SiteSearchState.Searching;
    public string Name => Site.Name;
    internal SiteVerificationRequiredException? PendingVerification { get; set; }
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canVerify;
    [ObservableProperty] private string _status = "等待搜索";

}

public sealed partial class SearchViewModel(AppServices services, Action<VodCardViewModel> open) : ObservableObject
{
    private sealed record SearchRun(CancellationTokenSource Cancellation, SemaphoreSlim Requests);
    private sealed record CachedSuggestions(string[] Titles, DateTimeOffset ExpiresAt);
    private readonly record struct WorkKey(string Name, string Year, string Type);
    private SearchRun? _run;
    private Task? _readHistory;
    private readonly List<SearchGroup> _groups = [];
    private readonly Dictionary<WorkKey, List<List<VodCardViewModel>>> _visibleWorks = [];
    private readonly Dictionary<string, CachedSuggestions> _suggestCache = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly List<string> _sessionTitles = [];
    private readonly HashSet<string> _sessionTitleSet = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _slowSuggestSites = new(StringComparer.Ordinal);
    private readonly SearchScope _all = new("全部站点");
    private int _maximumRequests = 3;

    public ObservableCollection<VodCardViewModel> Rows { get; } = [];
    public ObservableCollection<string> RecentSearches { get; } = [];
    public ObservableCollection<SearchScope> Scopes { get; } = [];
    public IReadOnlyList<string> Types { get; } = ["全部", "电影", "电视剧", "动漫", "综艺"];
    internal Func<IEnumerable<string>>? SuggestionCatalog { get; set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _query = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultsHeading))] private string _submittedQuery = string.Empty;
    [ObservableProperty] private bool _showsHistory = true;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultsHeading))] private string _status = string.Empty;
    [ObservableProperty] private SearchScope? _scope;
    [ObservableProperty] private string _selectedType = "全部";
    [ObservableProperty] private string _year = string.Empty;
    [ObservableProperty] private int _sortIndex;
    public string ResultsHeading => $"{SubmittedQuery} · {Status}";

    public async Task PrepareAsync()
    {
        if (services.Config.Current is { } config) SetScopes(config);
        await (_readHistory ??= ReadHistoryAsync());
        try
        {
            if (int.TryParse(await services.Database.GetSettingAsync("search.concurrency"), out var limit))
                _maximumRequests = Math.Clamp(limit, 1, 6);
        }
        catch (Exception ex) { Diagnostics.Log($"读取搜索并发设置失败: {ex.GetType().Name}"); }
    }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var keyword = Query.Trim();
        if (keyword.Length == 0) return;

        CancelPending();
        foreach (var choice in Scopes) choice.Group = null;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var run = new SearchRun(cancellation, new SemaphoreSlim(_maximumRequests, _maximumRequests));
        _run = run;
        _groups.Clear();
        ClearRows();
        SubmittedQuery = keyword;
        ShowsHistory = false;
        IsSearching = true;
        Status = "正在准备站点…";
        _ = RememberAsync(keyword);
        try
        {
            var config = services.Config.Current ?? await services.Config.LoadAsync(false, token);
            token.ThrowIfCancellationRequested();
            SetScopes(config);
            _groups.AddRange(Scopes.Where(choice => choice.Site is not null)
                .Select(choice => new SearchGroup(choice.Site!)));
            if (_groups.Count == 0)
            {
                Status = "暂无可搜索站点";
                return;
            }
            var scopeBySite = Scopes.Where(c => c.Site is not null).ToDictionary(c => c.Site!);
            foreach (var group in _groups)
                if (scopeBySite.TryGetValue(group.Site, out var scope)) scope.Group = group;
            UpdateProgress();
            await Task.WhenAll(_groups.Select(group => SearchSiteAsync(group, run, token)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Diagnostics.Log($"搜索准备失败: {ex.GetType().Name}");
                Status = "载入站点失败，请重试";
            }
        }
        finally { if (_run == run && _groups.Count == 0) IsSearching = false; }
    }

    internal void CancelPending()
    {
        var run = _run;
        _run = null;
        if (run is null) return;
        try { run.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        foreach (var group in _groups.Where(group => group.IsPending))
        {
            group.State = SiteSearchState.Paused;
            group.Status = "已暂停";
            group.CanRetry = true;
        }
        IsSearching = false;
        if (_groups.Count > 0) UpdateProgress();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RetryAsync(SearchGroup group)
    {
        if ((!group.CanRetry && !group.CanVerify) || !_groups.Contains(group)) return;
        var run = _run ??= new SearchRun(new CancellationTokenSource(), new SemaphoreSlim(_maximumRequests, _maximumRequests));
        await SearchSiteAsync(group, run, run.Cancellation.Token);
    }

    private async Task SearchSiteAsync(SearchGroup group, SearchRun run, CancellationToken token)
    {
        var query = SubmittedQuery;
        group.CanRetry = false;
        group.CanVerify = false;
        group.PendingVerification = null;
        group.State = SiteSearchState.Queued;
        group.Status = "等待搜索";
        try
        {
            await run.Requests.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                group.State = SiteSearchState.Searching;
                group.Status = "正在搜索";
                IsSearching = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(group.Site.Timeout, 5, 35)));
                var response = await services.Vod.SearchAsync(group.Site, ServerKeyword(query), 1, deadline.Token);
                token.ThrowIfCancellationRequested();
                var items = response.List.Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
                    .DistinctBy(item => item.Id, StringComparer.Ordinal).Take(80);
                group.Hits = items.Select(item => new VodCardViewModel(item, services)
                {
                    SearchScore = SearchScoring.Score(query, item)
                }).ToArray();
                HarvestSessionTitles(group.Hits.Take(20).Select(hit => hit.Item.Name));
                group.State = SiteSearchState.Finished;
                UpdateGroupRows(group);
            }
            finally { run.Requests.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SiteVerificationRequiredException ex)
        {
            if (token.IsCancellationRequested) return;
            group.State = SiteSearchState.Failed;
            group.PendingVerification = ex;
            group.CanVerify = true;
            group.CanRetry = false;
            group.Status = ex.Kind == SiteVerificationKind.ImageCaptcha ? "需要验证码" : "需要安全验证";
            Diagnostics.Log($"站点搜索需要验证 ({group.Site.Key}): {ex.Kind}");
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            group.State = SiteSearchState.Failed;
            group.Status = ex switch
            {
                OperationCanceledException or TimeoutException => "搜索超时",
                HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "站点拒绝访问（403）",
                HttpRequestException { StatusCode: HttpStatusCode.PreconditionFailed } => "站点拒绝请求（412）",
                HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "站点请求过多（429）",
                InvalidDataException { Message: "站点搜索需要验证码。" } => "站点需要验证码",
                InvalidDataException { Message: "站点响应为空。" } => "站点响应为空",
                InvalidDataException { Message: "站点响应格式无法识别。" } or System.Text.Json.JsonException => "站点响应格式异常",
                _ => "搜索失败"
            };
            group.CanRetry = true;
            Diagnostics.Log($"站点搜索失败 ({group.Site.Key}): {ex.GetType().Name}");
        }
        finally { if (_run == run) UpdateProgress(); }
    }

    private void SetScopes(ConfigDocument config)
    {
        var selectedKey = Scope?.Site?.Key;
        var available = config.Sites.Where(site => site.IsSupported && site.Searchable != 0).ToArray();
        if (Scopes.Count == available.Length + 1 && Scopes.Skip(1).Select(entry => entry.Site).SequenceEqual(available)) return;
        Scopes.Clear();
        Scopes.Add(_all);
        foreach (var site in available) Scopes.Add(new SearchScope(site.Name, site));
        Scope = Scopes.FirstOrDefault(entry => entry.Site?.Key == selectedKey) ?? _all;
    }

    [RelayCommand]
    private void OpenResult(VodCardViewModel card)
    {
        var sources = new List<VodCardViewModel> { card };
        foreach (var group in _groups.Where(group => group.Site.Key != card.Item.SiteKey))
        {
            var matches = group.Hits.Where(hit => SameWork(card.Item, hit.Item)).Take(2).ToArray();
            if (matches.Length == 1) sources.Add(matches[0]);
        }
        var details = sources.Select(source => new VodCardViewModel(source.Item.Copy(), services)
        {
            Poster = source.Poster,
            Rating = source.Rating
        }).ToArray();
        foreach (var detail in details) detail.MatchedSources = details;
        open(details[0]);
    }

    private static bool SameWork(VodItem left, VodItem right)
    {
        return GetWorkKey(left) is { } key && key == GetWorkKey(right) &&
            CompatibleMetadata(left.Area, right.Area) && CompatibleMetadata(left.Director, right.Director);
    }

    private static WorkKey? GetWorkKey(VodItem item)
    {
        if (item.Year.Length != 4 || !item.Year.All(char.IsAsciiDigit)) return null;
        var name = SearchScoring.Normalize(item.Name);
        var type = SearchScoring.Normalize(item.TypeName);
        return name.Length > 0 && type.Length > 0 ? new(name, item.Year, type) : null;
    }

    private static bool CompatibleMetadata(string left, string right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || SearchScoring.Normalize(left) == SearchScoring.Normalize(right);

    private void UpdateGroupRows(SearchGroup group)
    {
        if (group.State != SiteSearchState.Finished) return;
        var year = Year.Trim();
        var hits = group.Hits.Where(card => SearchScoring.MatchesType(card.Item, SelectedType) &&
            (year.Length == 0 || card.Item.Year.Contains(year, StringComparison.Ordinal)));
        var visible = hits.ToArray();
        group.Status = visible.Length > 0 ? $"{visible.Length} 条结果" : group.Hits.Count > 0 ? "筛选无结果" : "无结果";
        if (Scope?.Site is not null && Scope.Site != group.Site) return;
        var ambiguous = group.Hits.Select(card => GetWorkKey(card.Item)).Where(key => key.HasValue).Select(key => key.GetValueOrDefault())
            .GroupBy(key => key).Where(items => items.Skip(1).Any()).Select(items => items.Key).ToHashSet();
        foreach (var card in visible)
        {
            if (Scope?.Site is null && GetWorkKey(card.Item) is { } key && !ambiguous.Contains(key) &&
                TryMergeVisibleWork(key, card)) continue;
            var low = 0;
            var high = Rows.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                var comparison = SortIndex == 1 ? string.CompareOrdinal(card.Item.Year, Rows[mid].Item.Year) : 0;
                if (comparison == 0) comparison = card.SearchScore.CompareTo(Rows[mid].SearchScore);
                if (comparison > 0) high = mid;
                else low = mid + 1;
            }
            Rows.Insert(low, card);
        }
        if (visible.Length > 0)
            services.Images.Prefetch(visible.Take(12).Select(c => c.Item.BestPicture), maxCount: 12);
    }

    private bool TryMergeVisibleWork(WorkKey key, VodCardViewModel card)
    {
        if (!_visibleWorks.TryGetValue(key, out var works))
            _visibleWorks[key] = works = [];
        var matches = works.Where(sources => sources.All(source =>
            source.Item.SiteKey != card.Item.SiteKey && SameWork(source.Item, card.Item))).Take(2).ToArray();
        if (matches.Length != 1)
        {
            works.Add([card]);
            return false;
        }
        var match = matches[0];
        match.Add(card);
        match[0].SearchSourceCount = match.Count;
        return true;
    }

    private void ClearRows()
    {
        foreach (var card in Rows) card.SearchSourceCount = 1;
        Rows.Clear();
        _visibleWorks.Clear();
    }

    private void UpdateProgress()
    {
        var remaining = _groups.Count(group => group.IsPending);
        IsSearching = remaining > 0;
        var hits = Rows.Count;
        var completed = _groups.Count - remaining;
        Status = IsSearching ? $"{completed} / {_groups.Count} 站 · {hits} 条结果" : $"{hits} 条结果 · {_groups.Count} 个站点";
    }

    partial void OnScopeChanged(SearchScope? value) => ApplyFilters();
    partial void OnSelectedTypeChanged(string value) => ApplyFilters();
    partial void OnYearChanged(string value) => ApplyFilters();
    partial void OnSortIndexChanged(int value) => ApplyFilters();

    partial void OnQueryChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) return;
        CancelPending();
        ShowsHistory = true;
    }

    private void ApplyFilters()
    {
        ClearRows();
        foreach (var group in _groups) UpdateGroupRows(group);
        if (_groups.Count > 0) UpdateProgress();
    }

    private async Task ReadHistoryAsync()
    {
        try
        {
            if (await services.Database.GetSettingAsync("search.history") is { } json)
                foreach (var word in (JsonSerializer.Deserialize<string[]>(json) ?? []).Where(word => !string.IsNullOrWhiteSpace(word)).Take(10))
                    RecentSearches.Add(word);
            HasHistory = RecentSearches.Count > 0;
        }
        catch (Exception ex) { Diagnostics.Log($"读取搜索设置失败: {ex.GetType().Name}"); }
    }

    private async Task RememberAsync(string query)
    {
        await (_readHistory ??= ReadHistoryAsync());
        var match = RecentSearches.FirstOrDefault(word => word.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (match is not null) RecentSearches.Remove(match);
        RecentSearches.Insert(0, query);
        if (RecentSearches.Count > 10) RecentSearches.RemoveAt(10);
        await SaveHistoryAsync();
    }

    [RelayCommand]
    private async Task RemoveHistoryAsync(string query)
    {
        if (RecentSearches.Remove(query)) await SaveHistoryAsync();
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        RecentSearches.Clear();
        await SaveHistoryAsync();
    }

    private async Task SaveHistoryAsync()
    {
        HasHistory = RecentSearches.Count > 0;
        try { await services.Database.SetSettingAsync("search.history", JsonSerializer.Serialize(RecentSearches)); }
        catch (Exception ex) { Diagnostics.Log($"保存搜索记录失败: {ex.GetType().Name}"); }
    }

    private void HarvestSessionTitles(IEnumerable<string> titles)
    {
        foreach (var raw in titles)
        {
            var title = raw?.Trim();
            if (string.IsNullOrEmpty(title) || !_sessionTitleSet.Add(title)) continue;
            _sessionTitles.Add(title);
            if (_sessionTitles.Count > 240)
            {
                var oldest = _sessionTitles[0];
                _sessionTitles.RemoveAt(0);
                _sessionTitleSet.Remove(oldest);
            }
        }
    }

    private string SuggestCacheKey(string query) => $"{Scope?.Site?.Key ?? "*"}|{query.Trim()}";

    internal bool HasFreshSuggestionCache(string query)
    {
        var key = SuggestCacheKey(query);
        return _suggestCache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Synchronous 0ms prediction pass combining search history, in-memory VOD/Library catalog,
    /// session-seen titles, exact TTL cache, and prefix-matched cached remote predictions.
    /// </summary>
    internal IReadOnlyList<string> GetInstantSuggestions(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return [];

        var remotePool = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var exactKey = SuggestCacheKey(trimmed);
        if (_suggestCache.TryGetValue(exactKey, out var exact) && exact.ExpiresAt > now)
        {
            remotePool.AddRange(exact.Titles);
        }
        else if (trimmed.Length >= 2)
        {
            // Prefix cache reuse: if user typed "流浪" then "流浪地", filter the cached "流浪" pool at 0ms.
            var scopePrefix = $"{Scope?.Site?.Key ?? "*"}|";
            for (var len = trimmed.Length - 1; len >= 2; len--)
            {
                var prefixKey = scopePrefix + trimmed[..len];
                if (_suggestCache.TryGetValue(prefixKey, out var cached) && cached.ExpiresAt > now)
                {
                    remotePool.AddRange(cached.Titles.Where(t => t.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase)));
                    if (remotePool.Count >= 8) break;
                }
            }
        }

        return MergeAndRankSuggestions(trimmed, remotePool);
    }

    /// <summary>
    /// Streams remote suggestions from Douban fast-suggest and up to 2 healthy sites in parallel,
    /// invoking <paramref name="onUpdate"/> immediately as EACH source returns instead of waiting for the slowest site.
    /// </summary>
    internal async Task StreamRemoteSuggestionsAsync(string query, Action<IReadOnlyList<string>> onUpdate, CancellationToken token)
    {
        var trimmed = query.Trim();
        if (trimmed.Length < 2) return;

        var now = DateTimeOffset.UtcNow;
        var cacheKey = SuggestCacheKey(trimmed);
        if (_suggestCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            onUpdate(MergeAndRankSuggestions(trimmed, cached.Titles));
            return;
        }

        var remoteTasks = new List<Task<IReadOnlyList<string>>>();

        // Source A: Douban lightweight JSON suggest (~80-180ms, skips immediately when douban.enabled=false)
        remoteTasks.Add(FetchDoubanSuggestionsAsync(trimmed, token));

        // Source B: Up to 2 healthy searchable sites (prioritizing fast native endpoints, skipping recently-timed-out sites)
        if (services.Config.Current is { } config)
        {
            var candidates = Scope?.Site is { } selected
                ? [selected]
                : config.Sites
                    .Where(site => site.IsSupported && site.Searchable != 0)
                    .Where(site => !_slowSuggestSites.TryGetValue(site.Key, out var until) || until <= now)
                    .OrderByDescending(site => site.Type is 1 or 4)
                    .ThenByDescending(site => site.Api.Equals("csp_BttwooGuard", StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToArray();

            foreach (var site in candidates)
                remoteTasks.Add(FetchSiteSuggestionsAsync(site, trimmed, token));
        }

        if (remoteTasks.Count == 0) return;

        var remoteAccumulated = new List<string>();
        var remoteSeen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var anyRemoteFinished = false;

        while (remoteTasks.Count > 0)
        {
            var finished = await Task.WhenAny(remoteTasks);
            remoteTasks.Remove(finished);
            if (token.IsCancellationRequested) return;

            IReadOnlyList<string> batch;
            try { batch = await finished; }
            catch { continue; }

            anyRemoteFinished = true;
            var added = false;
            foreach (var title in batch)
            {
                var clean = title.Trim();
                if (clean.Length > 0 && remoteSeen.Add(clean))
                {
                    remoteAccumulated.Add(clean);
                    added = true;
                }
            }
            if (added)
            {
                HarvestSessionTitles(remoteAccumulated);
                onUpdate(MergeAndRankSuggestions(trimmed, remoteAccumulated));
            }
        }

        if (token.IsCancellationRequested || !anyRemoteFinished) return;

        if (_suggestCache.Count >= 64)
        {
            var expired = _suggestCache.Where(kv => kv.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(kv => kv.Key).ToArray();
            foreach (var key in expired) _suggestCache.Remove(key);
            if (_suggestCache.Count >= 64) _suggestCache.Clear();
        }
        _suggestCache[cacheKey] = new CachedSuggestions(remoteAccumulated.ToArray(), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    internal async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken token)
    {
        var latest = GetInstantSuggestions(query);
        if (latest.Count >= 6 || query.Trim().Length < 2) return latest;
        await StreamRemoteSuggestionsAsync(query, updated => latest = updated, token);
        return latest;
    }

    private async Task<IReadOnlyList<string>> FetchDoubanSuggestionsAsync(string query, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(1600));
            return await services.Douban.SuggestTitlesAsync(query, deadline.Token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Diagnostics.Log($"豆瓣搜索建议跳过: {ex.GetType().Name}");
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> FetchSiteSuggestionsAsync(SourceSite site, string query, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(1800));
            var response = await services.Vod.SearchAsync(site, ServerKeyword(query), 1, deadline.Token);
            return response.List
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(6)
                .ToArray();
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            if (ex is OperationCanceledException or TimeoutException)
                _slowSuggestSites[site.Key] = DateTimeOffset.UtcNow.AddMinutes(3);
            Diagnostics.Log($"搜索建议不可用 ({site.Key}): {ex.GetType().Name}");
            return [];
        }
    }

    private IReadOnlyList<string> MergeAndRankSuggestions(string query, IEnumerable<string> remoteTitles)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var scored = new List<(string Title, int Tier, int Length)>(16);

        void Add(string? raw, int baseTier, bool requireMatch)
        {
            var title = raw?.Trim();
            if (string.IsNullOrEmpty(title)) return;
            var starts = title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase);
            var contains = starts || title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            if (requireMatch && !contains) return;
            if (!seen.Add(title)) return;
            var tier = baseTier + (starts ? 0 : contains ? 2 : 4);
            scored.Add((title, tier, title.Length));
        }

        // Priority: History (0) -> Local/Session catalog (1) -> Remote fast predictions (2)
        foreach (var word in RecentSearches) Add(word, 0, requireMatch: true);
        if (SuggestionCatalog is not null)
            foreach (var title in SuggestionCatalog()) Add(title, 1, requireMatch: true);
        foreach (var title in _sessionTitles) Add(title, 1, requireMatch: true);
        foreach (var title in remoteTitles) Add(title, 2, requireMatch: false);

        return scored
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.Length)
            .Select(item => item.Title)
            .Take(8)
            .ToArray();
    }

    private static string ServerKeyword(string query)
    {
        var compact = QualifierPattern().Replace(query, string.Empty).Trim();
        return compact.Length == 0 ? query : compact;
    }

    [GeneratedRegex(@"(?:19|20)\d{2}|电影|电视剧|动漫|动画|综艺", RegexOptions.CultureInvariant)]
    private static partial Regex QualifierPattern();
}
