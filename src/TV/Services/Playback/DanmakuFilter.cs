using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TV.Services.Playback;

internal sealed partial class DanmakuFilter
{
    internal const int MaxPatternTextLength = 4096;
    private const int MaxRules = 32;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MatchBudget = TimeSpan.FromSeconds(1);

    private readonly bool _basicFilter;
    private readonly string[] _words;
    private readonly (int Line, Regex Pattern)[] _rules;

    private DanmakuFilter(bool basicFilter, string[] words, (int Line, Regex Pattern)[] rules)
    {
        _basicFilter = basicFilter;
        _words = words;
        _rules = rules;
    }

    internal static DanmakuFilter Default { get; } = new(true, [], []);
    internal bool HasRules => _basicFilter || _words.Length > 0 || _rules.Length > 0;

    internal DanmakuFilter WithBasicFilter(bool enabled) =>
        enabled == _basicFilter ? this : new(enabled, _words, _rules);

    internal static bool TryCreate(DanmakuPreferences preferences, [NotNullWhen(true)] out DanmakuFilter? filter, out string error)
    {
        filter = null;
        var patterns = preferences.BlockedPatterns ?? string.Empty;
        if (patterns.Length > MaxPatternTextLength)
        {
            error = $"正则表达式最多可输入 {MaxPatternTextLength} 个字符。";
            return false;
        }

        using var reader = new StringReader(patterns);
        var rules = new List<(int, Regex)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var line = 1; reader.ReadLine() is { } pattern; line++)
        {
            if (string.IsNullOrWhiteSpace(pattern) || !seen.Add(pattern)) continue;
            if (rules.Count == MaxRules)
            {
                error = $"正则表达式最多可输入 {MaxRules} 条。";
                return false;
            }
            try
            {
                rules.Add((line, new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout)));
            }
            catch (ArgumentException)
            {
                error = $"第 {line} 行正则表达式语法有误，请检查后再应用。";
                return false;
            }
        }

        var words = (preferences.BlockedWords ?? string.Empty)
            .Split([' ', '，', ',', '\n', '、'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        filter = new DanmakuFilter(preferences.BasicFilter, words, rules.ToArray());
        error = string.Empty;
        return true;
    }

    internal readonly record struct Result(DanmakuEntry[] Comments, string? Warning = null);

    internal Result Apply(DanmakuEntry[] comments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (comments.Length == 0 || !HasRules) return new(comments);

        var blocked = new bool[comments.Length];
        var blockedCount = 0;
        for (var i = 0; i < comments.Length; i++)
        {
            if ((i & 127) == 0) token.ThrowIfCancellationRequested();
            var comment = comments[i].Text;
            if (!(_basicFilter && IsBasicMatch(comment)) && !ContainsBlockedWord(comment)) continue;
            blocked[i] = true;
            blockedCount++;
        }

        // Only commit a rule's matches once it has finished, so a slow rule cannot partially hide the list.
        string? warning = null;
        var started = Stopwatch.GetTimestamp();
        var matches = new List<int>();
        foreach (var (line, pattern) in _rules)
        {
            if (blockedCount == comments.Length) break;
            matches.Clear();
            try
            {
                for (var i = 0; i < comments.Length; i++)
                {
                    if ((i & 15) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Stopwatch.GetElapsedTime(started) >= MatchBudget)
                            return new(CollectVisible(comments, blocked, blockedCount), "正则匹配耗时过长，本次已跳过未完成的规则。");
                    }
                    if (!blocked[i] && pattern.IsMatch(comments[i].Text)) matches.Add(i);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                warning = warning is null
                    ? $"第 {line} 行正则匹配超时，本次已跳过该规则。"
                    : "多条正则匹配超时，本次已跳过超时规则。";
                continue;
            }

            foreach (var index in matches) blocked[index] = true;
            blockedCount += matches.Count;
        }

        return new(CollectVisible(comments, blocked, blockedCount), warning);
    }

    private bool ContainsBlockedWord(string comment)
    {
        foreach (var word in _words)
            if (comment.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static DanmakuEntry[] CollectVisible(DanmakuEntry[] comments, bool[] blocked, int blockedCount)
    {
        if (blockedCount == 0) return comments;
        var visible = new DanmakuEntry[comments.Length - blockedCount];
        for (int i = 0, next = 0; i < comments.Length; i++)
            if (!blocked[i]) visible[next++] = comments[i];
        return visible;
    }

    private static bool IsBasicMatch(string comment) =>
        comment.Length <= 64 && BasicAdPattern().IsMatch(comment);

    // Match complete solicitations so the same words can still appear in a normal sentence.
    [GeneratedRegex("""
        \A \s* (?:
            (?: (?:请|欢迎)? 加(?:微信|微|vx) | (?:广告|商务)合作 \s* [:：]? \s* (?:微信|微|vx) )
                \s* [:：]? \s* (?: [a-z][a-z0-9_-]{5,19} | 1[3-9][0-9]{9} )
          | (?: (?:请|欢迎)? (?:加|进)(?:qq)?群 | qq群 | 群号 ) \s* [:：]? \s* [1-9][0-9]{4,11}
          | (?:全网)? 低价 (?:代充|代刷) (?:点券|会员|游戏币)?
        ) \s* [！!。.]? \s* \z
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BasicAdPattern();
}
