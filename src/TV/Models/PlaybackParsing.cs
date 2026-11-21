using System.Text.RegularExpressions;

namespace TV.Models;

public static partial class PlaybackParsing
{
    public static List<PlaybackLine> ParseLines(VodItem item)
    {
        var from = item.PlayFrom.Split("$$$", StringSplitOptions.TrimEntries);
        var urls = item.PlayUrl.Split("$$$", StringSplitOptions.TrimEntries);
        var count = Math.Max(from.Length, urls.Length);
        var result = new List<PlaybackLine>();
        for (var i = 0; i < count; i++)
        {
            var line = new PlaybackLine
            {
                Name = i < from.Length && !string.IsNullOrWhiteSpace(from[i]) ? from[i] : $"线路 {i + 1}"
            };
            if (i < urls.Length)
            {
                foreach (var value in urls[i].Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var separator = value.IndexOf('$');
                    var name = separator > 0 ? value[..separator] : $"第 {line.Episodes.Count + 1} 集";
                    var url = separator > 0 ? value[(separator + 1)..] : value;
                    if (!string.IsNullOrWhiteSpace(url)) line.Episodes.Add(new Episode { Name = name, Url = url });
                }
            }
            if (line.Episodes.Count > 0)
            {
                // 电影类常把唯一的"集"命名为清晰度（高清/蓝光…），集数列表里应显示"正片"
                if (line.Episodes.Count == 1 && QualityNames.Contains(line.Episodes[0].Name))
                    line.Episodes[0].Name = "正片";
                result.Add(line);
            }
        }
        return result;
    }

    private static readonly HashSet<string> QualityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "高清", "超清", "蓝光", "标清", "流畅", "极速", "HD", "BD", "4K", "1080P", "1080p", "720P", "720p", "DVD", "TS", "TC", "抢先版", "完整版"
    };

    public static string CleanTitle(string title)
    {
        var value = HtmlRegex().Replace(title, string.Empty);
        value = SuffixRegex().Replace(value, string.Empty);
        return value.Trim();
    }

    public static string StripHtml(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : System.Net.WebUtility.HtmlDecode(HtmlRegex().Replace(value, string.Empty)).Trim();

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlRegex();

    [GeneratedRegex(@"(?:第[0-9一二三四五六七八九十百]+[季部]|[（(](?:国语|粤语|英语|中字|高清|未删减|完整版)[）)])$", RegexOptions.Compiled)]
    private static partial Regex SuffixRegex();
}
