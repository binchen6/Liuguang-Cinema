using System.Text.RegularExpressions;
using TV.Models;

namespace TV.Services.Vod;

public static partial class SearchScoring
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    public static double Score(string query, VodItem item)
    {
        var expected = Normalize(query);
        var title = Normalize(PlaybackParsing.CleanTitle(item.Name));
        if (expected.Length == 0 || title.Length == 0) return 0;
        double score;
        if (title == expected) score = 1400;
        else if (title.StartsWith(expected, StringComparison.Ordinal)) score = 1120 - Math.Min(120, (title.Length - expected.Length) * 8);
        else if (title.Contains(expected, StringComparison.Ordinal)) score = 920 - Math.Min(120, title.IndexOf(expected, StringComparison.Ordinal) * 12);
        else if (IsSubsequence(expected, title)) score = 620 - Math.Min(180, title.Length - expected.Length) * 5;
        else
        {
            var distance = Levenshtein(expected, title);
            var similarity = 1d - (double)distance / Math.Max(expected.Length, title.Length);
            score = similarity >= 0.42 ? similarity * 660 : 0;
        }

        var queryYear = YearRegex().Match(query);
        if (queryYear.Success)
        {
            if (item.Year.StartsWith(queryYear.Value, StringComparison.Ordinal)) score += 160;
            else score -= 120;
        }
        if (query.Contains("动漫") || query.Contains("动画")) score += item.TypeName.Contains("动漫") || item.TypeName.Contains("动画") ? 90 : -50;
        if (query.Contains("电影")) score += item.TypeName.Contains("电影") ? 90 : -30;
        if (query.Contains("电视剧") || query.Contains("剧集")) score += item.TypeName.Contains("剧") ? 90 : -30;
        if (item.PlayUrl.Length > 0) score += 12;
        return score;
    }

    public static bool MatchesType(VodItem item, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == "全部") return true;
        var value = $"{item.TypeName} {item.Remarks}";
        return filter switch
        {
            "电影" => value.Contains("电影") || item.TypeId is "1" or "6" or "20",
            "电视剧" => value.Contains("电视剧") || value.Contains("剧集") || item.TypeId is "2" or "13" or "21",
            "动漫" => value.Contains("动漫") || value.Contains("动画") || item.TypeId is "4" or "24",
            "综艺" => value.Contains("综艺") || item.TypeId is "3" or "23",
            _ => value.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        };
    }

    private static bool IsSubsequence(string expected, string title)
    {
        var index = 0;
        foreach (var character in title)
        {
            if (character == expected[index]) index++;
            if (index == expected.Length) return true;
        }
        return false;
    }

    private static int Levenshtein(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 12) return Math.Max(left.Length, right.Length);
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    [GeneratedRegex(@"(?:19|20)\d{2}", RegexOptions.Compiled)]
    private static partial Regex YearRegex();
}
