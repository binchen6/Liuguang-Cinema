using System.Globalization;
using System.Text.RegularExpressions;

namespace TV.Services.Playback;

public sealed record MediaIdentity(
    string RawTitle,
    string CleanTitle,
    string BaseTitle,
    IReadOnlyList<string> Aliases,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? SpecialTag,
    int? Year,
    bool IsMovie)
{
    public string CanonicalKey =>
        $"{SearchScoringNormalized(BaseTitle)}|s{SeasonNumber ?? 1}|{(SpecialTag ?? string.Empty).ToUpperInvariant()}|e{EpisodeNumber?.ToString(CultureInfo.InvariantCulture) ?? (IsMovie ? "movie" : "none")}";

    private static string SearchScoringNormalized(string text) =>
        TV.Services.Vod.SearchScoring.Normalize(text);
}

public static partial class MediaMatching
{
    private static readonly Dictionary<char, int> ChineseDigits = new()
    {
        ['零'] = 0, ['〇'] = 0,
        ['一'] = 1, ['壹'] = 1,
        ['二'] = 2, ['两'] = 2, ['兩'] = 2, ['贰'] = 2, ['貳'] = 2,
        ['三'] = 3, ['叁'] = 3, ['參'] = 3,
        ['四'] = 4, ['肆'] = 4,
        ['五'] = 5, ['伍'] = 5,
        ['六'] = 6, ['陆'] = 6, ['陸'] = 6,
        ['七'] = 7, ['柒'] = 7,
        ['八'] = 8, ['捌'] = 8,
        ['九'] = 9, ['玖'] = 9
    };

    public static MediaIdentity Parse(
        string? title,
        string? episode = null,
        int episodeIndex = -1,
        string? yearHint = null,
        string? typeHint = null)
    {
        var rawTitle = (title ?? string.Empty).Trim();
        var rawEpisode = (episode ?? string.Empty).Trim();

        // Extract year from explicit metadata first, then title / episode filename
        var year = ParseYear(yearHint) ?? ExtractYear(rawTitle) ?? ExtractYear(rawEpisode);

        // Extract season & episode from episode name first (e.g. "Show.S02E05.2160p.mp4" has authoritative S02E05),
        // then fall back to title for season and episode cues.
        var specialTag = ExtractSpecialTag(rawEpisode) ?? ExtractSpecialTag(rawTitle);
        var episodeSeason = ExtractSeasonNumber(rawEpisode, allowTrailingDigit: false);
        var epNumber = ExtractEpisodeNumberCore(rawEpisode, rawTitle);

        // Clean title without ever truncating dots inside show names like "2.5次元的诱惑" or "Dr.STONE".
        var strippedTitle = StripMediaPathAndExtension(rawTitle);
        var aliasParts = SplitAliases(strippedTitle);
        var cleanedAliases = new List<string>();
        int? titleSeason = null;

        foreach (var part in aliasParts)
        {
            var cleaned = CleanTitleSegment(part);
            if (cleaned.Length == 0) continue;
            var (basePart, seasonInPart) = ExtractBaseAndSeason(cleaned);
            if (!titleSeason.HasValue && seasonInPart.HasValue) titleSeason = seasonInPart;
            if (basePart.Length > 0 && !cleanedAliases.Any(a => a.Equals(basePart, StringComparison.OrdinalIgnoreCase)))
                cleanedAliases.Add(basePart);
            if (cleaned.Length > 0 && !cleanedAliases.Any(a => a.Equals(cleaned, StringComparison.OrdinalIgnoreCase)))
                cleanedAliases.Add(cleaned);
        }

        // If rawTitle was empty or purely generic (e.g. "我的云盘") while rawEpisode looks like a full filename
        // ("葬送的芙莉莲.S01E05.2160p.mp4"), derive title from rawEpisode as fallback.
        if (cleanedAliases.Count == 0 && rawEpisode.Length > 0)
        {
            var epAsTitle = CleanTitleSegment(StripMediaPathAndExtension(rawEpisode));
            var (baseFromEp, seasonFromEp) = ExtractBaseAndSeason(epAsTitle);
            if (!titleSeason.HasValue && seasonFromEp.HasValue) titleSeason = seasonFromEp;
            if (baseFromEp.Length > 0) cleanedAliases.Add(baseFromEp);
        }

        var primaryClean = aliasParts.Count > 0 ? CleanTitleSegment(aliasParts[0]) : string.Empty;
        var (primaryBase, primarySeason) = ExtractBaseAndSeason(primaryClean);
        var season = episodeSeason ?? primarySeason ?? titleSeason;
        var baseTitle = primaryBase.Length > 0 ? primaryBase : (cleanedAliases.FirstOrDefault() ?? primaryClean);
        // Prefer a CJK alias as canonical BaseTitle when a bilingual title puts Romaji/English first
        // (e.g. "Sousou no Frieren / 葬送的芙莉莲" -> BaseTitle = "葬送的芙莉莲", Aliases retains both).
        if (!ContainsCjk(baseTitle))
        {
            var cjkAlias = cleanedAliases.FirstOrDefault(a => ContainsCjk(a) && a.Length >= 2);
            if (cjkAlias is not null) baseTitle = cjkAlias;
        }
        if (baseTitle.Length == 0) baseTitle = StripMediaPathAndExtension(rawTitle);

        var cleanTitle = season is >= 2 && !HasSeasonCue(baseTitle)
            ? $"{baseTitle} 第{season.Value}季"
            : baseTitle;

        // If episode number wasn't found in rawEpisode, check rawTitle (e.g. local file "庆余年.S02E05.mp4" where episode is empty)
        if (!epNumber.HasValue && rawEpisode.Length == 0)
            epNumber = ExtractEpisodeNumberCore(rawTitle, string.Empty);

        var isMovie = DetermineIsMovie(rawTitle, rawEpisode, typeHint, epNumber, specialTag, episodeIndex);
        if (isMovie && !epNumber.HasValue) epNumber = null;

        // Build deduplicated ordered alias list (base titles first)
        var aliases = new List<string>();
        void AddAlias(string candidate)
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length == 0) return;
            if (!aliases.Any(x => x.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                aliases.Add(trimmed);
        }
        AddAlias(baseTitle);
        foreach (var item in cleanedAliases) AddAlias(item);

        return new MediaIdentity(
            RawTitle: rawTitle,
            CleanTitle: cleanTitle,
            BaseTitle: baseTitle,
            Aliases: aliases,
            SeasonNumber: season,
            EpisodeNumber: epNumber,
            SpecialTag: specialTag,
            Year: year,
            IsMovie: isMovie);
    }

    public static string Title(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var stripped = StripMediaPathAndExtension(value.Trim());
        var firstAlias = SplitAliases(stripped).FirstOrDefault() ?? stripped;
        var cleaned = CleanTitleSegment(firstAlias);
        return cleaned.Length > 0 ? cleaned : stripped;
    }

    public static int? EpisodeNumber(string? value) =>
        ExtractEpisodeNumberCore(value ?? string.Empty, string.Empty);

    public static bool IsSpecialEpisodeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return SpecialEpisodeCue().IsMatch(title);
    }

    public static string StripMediaPathAndExtension(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return string.Empty;

        // Strip Windows file path directory ONLY when it looks like a real local/UNC/cloud path,
        // NOT multi-language titles separated by " / " like "庆余年 / Joy of Life".
        if (text.Contains('\\') || (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':'))
        {
            var slash = text.LastIndexOfAny(['\\', '/']);
            if (slash >= 0 && slash < text.Length - 1)
                text = text[(slash + 1)..];
        }
        else if (text.StartsWith('/'))
        {
            var slash = text.LastIndexOf('/');
            if (slash >= 0 && slash < text.Length - 1)
                text = text[(slash + 1)..];
        }

        // Only strip genuine video/media extensions (NEVER chop ".5次元的诱惑" or "Dr.STONE"!)
        text = MediaExtension().Replace(text, string.Empty);
        return text.Trim();
    }

    private static List<string> SplitAliases(string text)
    {
        // Split on " / ", " | ", "｜" (multi-language show titles like "葬送的芙莉莲 / 葬送のフリーレン")
        var parts = AliasSeparator().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count > 0 ? parts : [text];
    }

    private static string CleanTitleSegment(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return string.Empty;

        // Remove leading fansub / site group brackets like "[ANi]", "【4K】", "[GM-Team][国漫]"
        // while keeping the title if brackets wrap the ENTIRE string (e.g. "《庆余年》").
        text = BookQuoteWrap().Replace(text, "$1");
        while (LeadingBracketTag().Match(text) is { Success: true } lead)
        {
            var remainder = text[lead.Length..].Trim();
            if (remainder.Length == 0) break;
            // Only strip if it looks like a group/quality/category tag OR there's still a real title after it
            var inner = FirstGroup(lead);
            if (IsNoiseTag(inner) || remainder.Length >= 2)
                text = remainder;
            else
                break;
        }

        // Remove any bracketed noise tags anywhere: [1080P], 【国语中字】, (2024), （更新至12集）, [HEVC]
        text = AnyBracketTag().Replace(text, m => IsNoiseTag(FirstGroup(m)) ? " " : m.Value);

        // Strip trailing episode/season-episode markers (S01E05..., EP05..., 第05集..., 全36集, 更新至20集)
        text = CountOrProgressSuffix().Replace(text, " ");
        text = EpisodeTailSuffix().Replace(text, string.Empty);
        text = TechnicalSuffix().Replace(text, string.Empty);

        // Replace underscores and filename-style dots (dot surrounded by letters/digits/spaces,
        // while PRESERVING decimal numbers like "2.5" and initials like "Dr.STONE" / "S.W.A.T.")
        text = text.Replace('_', ' ');
        text = FilenameSeparatorDot().Replace(text, " ");

        // Remove standalone trailing release year (e.g. "庆余年 2024") unless the whole title IS the year (e.g. "1917", "2012")
        var withoutYear = TrailingYear().Replace(text, string.Empty).Trim();
        if (withoutYear.Length >= 2) text = withoutYear;

        // Remove trailing standalone quality/language words
        text = TrailingNoiseWords().Replace(text, string.Empty);
        text = MultiSpace().Replace(text, " ");
        return text.Trim(' ', '-', '–', '—', '_', '.', ':', '：', '[', ']', '【', '】', '(', ')', '（', '）');
    }

    private static bool IsNoiseTag(string inner)
    {
        var t = inner.Trim();
        if (t.Length == 0) return true;
        if (PureYearRegex().IsMatch(t)) return true;
        if (NoiseTagContent().IsMatch(t)) return true;
        // Fansub group tags like "ANi", "LoliHouse", "Lilith-Raws", "GM-Team", "VCB-Studio", "字幕组"
        if (FansubGroupTag().IsMatch(t)) return true;
        return false;
    }

    public static (string BaseTitle, int? SeasonNumber) ExtractBaseAndSeason(string cleanTitle)
    {
        var text = cleanTitle.Trim();
        if (text.Length == 0) return (string.Empty, null);

        // 1. Explicit Chinese season: "庆余年 第二季" / "庆余年第2部" / "庆余年 第3季"
        var zh = ChineseSeason().Match(text);
        if (zh.Success && ParseChineseOrArabicNumber(zh.Groups[1].Value) is { } zhSeason and >= 1 and <= 50)
        {
            var basePart = (text[..zh.Index] + " " + text[(zh.Index + zh.Length)..]).Trim(' ', '-', '–', ':', '：');
            basePart = MultiSpace().Replace(basePart, " ");
            return (basePart.Length >= 1 ? basePart : text, zhSeason);
        }

        // 2. Explicit English season: "Season 2", "S02", "S2", "Part 2"
        var en = EnglishSeason().Match(text);
        if (en.Success)
        {
            var numStr = FirstGroup(en);
            if (int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enSeason) && enSeason is >= 1 and <= 50)
            {
                var basePart = (text[..en.Index] + " " + text[(en.Index + en.Length)..]).Trim(' ', '-', '–', ':', '：');
                basePart = MultiSpace().Replace(basePart, " ");
                return (basePart.Length >= 1 ? basePart : text, enSeason);
            }
        }

        // 3. Trailing Roman numeral season/sequel (e.g. "进击的巨人 II", "Overlord III")
        var roman = TrailingRomanSeason().Match(text);
        if (roman.Success)
        {
            var num = roman.Groups[1].Value.ToUpperInvariant() switch { "II" => 2, "III" => 3, "IV" => 4, "V" => 5, _ => 0 };
            if (num >= 2)
            {
                var basePart = text[..roman.Index].Trim(' ', '-', '–', ':', '：');
                if (basePart.Length >= 2) return (basePart, num);
            }
        }

        // 4. Trailing single/small sequel digit after CJK title (e.g. "庆余年2", "流浪地球2", "鬼灭之刃 3")
        // Careful: require >= 2 CJK chars before it so "9号秘事" / "2.5次元的诱惑" / "1917" / "007" are untouched.
        var trailing = TrailingCjkSeasonDigit().Match(text);
        if (trailing.Success && int.TryParse(trailing.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var digit) && digit is >= 2 and <= 15)
        {
            var basePart = trailing.Groups[1].Value.Trim(' ', '-', '–', ':', '：');
            if (basePart.Length >= 2) return (basePart, digit);
        }

        return (text, null);
    }

    private static int? ExtractSeasonNumber(string text, bool allowTrailingDigit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var se = SeasonEpisodeRegex().Match(text);
        if (se.Success && int.TryParse(se.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is >= 1 and <= 50)
            return s;
        var zh = ChineseSeason().Match(text);
        if (zh.Success && ParseChineseOrArabicNumber(zh.Groups[1].Value) is { } zs and >= 1 and <= 50)
            return zs;
        var en = EnglishSeason().Match(text);
        if (en.Success)
        {
            var numStr = FirstGroup(en);
            if (int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var es) && es is >= 1 and <= 50)
                return es;
        }
        if (allowTrailingDigit)
        {
            var (_, parsed) = ExtractBaseAndSeason(text);
            return parsed;
        }
        return null;
    }

    private static bool HasSeasonCue(string text) =>
        ChineseSeason().IsMatch(text) || EnglishSeason().IsMatch(text);

    private static string? ExtractSpecialTag(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SpecialEpisodeCue().Match(text);
        if (!m.Success) return null;
        var raw = FirstGroup(m).Trim().ToUpperInvariant();
        return raw switch
        {
            "番外" or "番外篇" or "特别篇" or "特典" or "SP" => "SP",
            "OVA" => "OVA",
            "OAD" => "OAD",
            "PV" or "预告" or "预告片" or "先导" or "先导片" => "PV",
            "总集篇" => "RECAP",
            _ => raw
        };
    }

    private static string FirstGroup(Match m)
    {
        for (var i = 1; i < m.Groups.Count; i++)
            if (m.Groups[i].Success && m.Groups[i].Value.Length > 0)
                return m.Groups[i].Value;
        return string.Empty;
    }

    private static int? ExtractEpisodeNumberCore(string rawEpisode, string contextTitle)
    {
        if (string.IsNullOrWhiteSpace(rawEpisode)) return null;
        var text = StripMediaPathAndExtension(rawEpisode);
        if (text.Length == 0) return null;

        // Mask out total-count expressions ("全36集", "共24话", "更新至12集"), technical tokens ("1080p", "2160p",
        // "H.265", "x264", "10bit", "60fps", "5.1"), and 4-digit years ("1999", "2024") / 8-digit dates ("20251123")
        // so they can NEVER be mistaken for episode numbers.
        var masked = MaskNonEpisodeNumbers(text);

        // 1. S01E05 / S2E12 (highest precision)
        var se = SeasonEpisodeRegex().Match(masked);
        if (se.Success && TryValidEpisode(se.Groups[2].Value, out var seNum)) return seNum;

        // 2. Explicit Chinese "第05集" / "第十二话" / "第5期" / "第3回"
        var zh = ChineseEpisode().Match(masked);
        if (zh.Success && ParseChineseOrArabicNumber(zh.Groups[1].Value) is { } zhNum && zhNum is >= 1 and <= 2000)
            return zhNum;

        // 3. Explicit English "EP05" / "E05" / "Episode 5"
        var en = EnglishEpisode().Match(masked);
        if (en.Success && TryValidEpisode(en.Groups[1].Value, out var enNum)) return enNum;

        // 4. Fansub dash pattern: " - 05 " / " - 05v2 [" / " - 125("
        var dash = FansubDashEpisode().Match(masked);
        if (dash.Success && TryValidEpisode(dash.Groups[1].Value, out var dashNum)) return dashNum;

        // 5. Bracketed numeric episode: "[05]" / "[125]" / "【05】" (like "[GM-Team][国漫][凡人修仙传][125][HEVC]")
        foreach (Match bm in BracketEpisode().Matches(masked))
        {
            if (TryValidEpisode(bm.Groups[1].Value, out var brNum)) return brNum;
        }

        // 6. Pure integer string (e.g. "05" or "5" after stripping ".mp4" and noise tags)
        var cleanedForPlain = AnyBracketTag().Replace(masked, " ");
        cleanedForPlain = TrailingNoiseWords().Replace(cleanedForPlain, " ").Trim(' ', '-', '–', '_', '.', '[', ']', '【', '】');
        if (PureEpisodeDigits().IsMatch(cleanedForPlain) && TryValidEpisode(cleanedForPlain, out var plainNum))
            return plainNum;

        // 7. Trailing delimited number after show name: "庆余年2-05" or "葬送的芙莉莲 05" or "Episode_05"
        // Exclude numbers that are literally part of the show's own title (like "庆余年2" -> "2" is the season).
        var tail = TrailingDelimitedEpisode().Match(cleanedForPlain);
        if (tail.Success && TryValidEpisode(tail.Groups[1].Value, out var tailNum))
        {
            var titleBase = contextTitle.Length > 0 ? StripMediaPathAndExtension(contextTitle) : string.Empty;
            if (titleBase.Length == 0 || !titleBase.EndsWith(tail.Groups[1].Value, StringComparison.Ordinal))
                return tailNum;
        }

        return null;
    }

    private static string MaskNonEpisodeNumbers(string text)
    {
        var masked = CountOrProgressSuffix().Replace(text, " ");
        masked = TechnicalNumberToken().Replace(masked, " ");
        masked = DateNumberToken().Replace(masked, " ");
        masked = YearToken().Replace(masked, " ");
        return masked;
    }

    private static bool TryValidEpisode(string value, out int number)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number is >= 1 and <= 2000)
            return true;
        number = 0;
        return false;
    }

    private static bool DetermineIsMovie(string rawTitle, string rawEpisode, string? typeHint, int? epNumber, string? specialTag, int episodeIndex)
    {
        if (epNumber.HasValue || specialTag is not null) return false;
        if (!string.IsNullOrWhiteSpace(typeHint) && MovieTypeHint().IsMatch(typeHint)) return true;
        if (MovieEpisodeLabel().IsMatch(rawEpisode)) return true;
        if (episodeIndex <= 0 && rawEpisode.Length == 0 && MovieTitleHint().IsMatch(rawTitle)) return true;
        return false;
    }

    private static int? ParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = YearToken().Match(text);
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2099
            ? y : null;
    }

    private static int? ExtractYear(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = BracketedOrDelimitedYear().Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2099
            ? y : null;
    }

    public static int? ParseChineseOrArabicNumber(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arabic)) return arabic;

        var total = 0;
        var current = 0;
        var any = false;
        foreach (var ch in text)
        {
            if (ChineseDigits.TryGetValue(ch, out var digit))
            {
                current = digit;
                any = true;
            }
            else if (ch is '十' or '拾')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
                any = true;
            }
            else if (ch is '百' or '佰')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
                any = true;
            }
            else
            {
                return null;
            }
        }
        return any ? total + current : null;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF' or >= '\u3040' and <= '\u30FF')
                return true;
        }
        return false;
    }

    [GeneratedRegex(@"\.(?:mp4|mkv|avi|mov|flv|ts|m2ts|mts|wmv|webm|rmvb|rm|m4v|mpg|mpeg|vob|3gp|ogv|strm)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaExtension();

    [GeneratedRegex(@"\s*(?:/|\||｜)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex AliasSeparator();

    [GeneratedRegex(@"^《([^》]+)》$", RegexOptions.CultureInvariant)]
    private static partial Regex BookQuoteWrap();

    [GeneratedRegex(@"^(?:\[([^\]]{1,48})\]|【([^】]{1,48})】|\(([^)]{1,32})\)|（([^）]{1,32})）)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBracketTag();

    [GeneratedRegex(@"\[([^\]]{1,64})\]|【([^】]{1,64})】|\(([^)]{1,48})\)|（([^）]{1,48})）", RegexOptions.CultureInvariant)]
    private static partial Regex AnyBracketTag();

    [GeneratedRegex(@"^(?:19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex PureYearRegex();

    [GeneratedRegex(@"(?:2160p|1080p|1080i|720p|480p|4k|8k|uhd|fhd|hdr\d*\+?|dv|dolby|x26[45]|h\.?26[45]|hevc|avc|av1|aac|flac|dts|ddp|ac3|atmos|truehd|bluray|bdrip|remux|web-?dl|webrip|dvdrip|10bit|8bit|60fps|120fps|国语|粤语|日语|英语|韩语|双语|中字|简日|繁日|简中|繁中|简繁|字幕|内封|内嵌|外挂|chs|cht|gb|big5|jpsc|jptc|baha|b-global|netflix|国漫|日漫|番剧|高码|臻彩|杜比|蓝光|原盘|超清|高清|标清|未删减|无删减|全\s*\d+\s*[集话期]|更新至|更至|完结|连载|无水印|纯净版|正片|tc|ts|cam|抢先|枪版)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseTagContent();

    [GeneratedRegex(@"^(?:ANi|LoliHouse|Lilith-Raws|SweetSub|Sakurato|NC-Raws|GM-Team|VCB-Studio|SubPig|Nekomoe|BeanSub|KTXP|DMG|FLsnow|Airota|MMSUB|Skytree|HYSUB|MingY|XKsub|KitaujiSub|.*字幕组|.*压制组|.*搬运)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FansubGroupTag();

    [GeneratedRegex(@"(?:全\s*\d{1,4}\s*(?:集|话|話|期)|共\s*\d{1,4}\s*(?:集|话|話|期)|更(?:新)?至\s*(?:第)?\s*\d{1,4}\s*(?:集|话|話|期)?)", RegexOptions.CultureInvariant)]
    private static partial Regex CountOrProgressSuffix();

    [GeneratedRegex(@"(?:\bS\d{1,2}E\d{1,4}\b.*|\bEP\d{1,4}\b.*|(?<![A-Za-z])E\d{1,4}\b.*|第\s*(?:\d{1,4}|[一二三四五六七八九十百]+)\s*(?:集|话|話|期).*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeTailSuffix();

    [GeneratedRegex(@"(?:^|[\s._-])(?:2160p|1080p|1080i|720p|480p|4k|8k|uhd|x26[45]|h\.?26[45]|hevc|avc|av1|aac|flac|dts|ddp|ac3|bluray|bdrip|remux|web-?dl|webrip|hdr10\+?|hdr|10bit|8bit|60fps|120fps)(?:$|[\s._-].*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalSuffix();

    [GeneratedRegex(@"(?<!\d)\.(?!\d)(?![A-Z]\.)", RegexOptions.CultureInvariant)]
    private static partial Regex FilenameSeparatorDot();

    [GeneratedRegex(@"[\s._-]+(?:19\d{2}|20\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"(?:[\s._-]+(?:4k|8k|hd|bd|uhd|fhd|高码率?|臻彩|杜比视界?|蓝光|原盘|超清|高清|标清|国语|粤语|双语|中字|未删减|无删减|完结|正片|抢先版?|枪版|修复版|重制版|导演剪辑版|加长版))+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNoiseWords();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"第\s*(\d{1,2}|[一二三四五六七八九十]+)\s*(?:季|部)", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseSeason();

    [GeneratedRegex(@"(?:\bSeason\s*(\d{1,2})\b|\bS0?(\d{1,2})\b(?!E\d)|\bPart\s*(\d{1,2})\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishSeason();

    [GeneratedRegex(@"\s+(II|III|IV|V)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRomanSeason();

    [GeneratedRegex(@"^([\u4e00-\u9fff]{2,20})\s*(\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingCjkSeasonDigit();

    [GeneratedRegex(@"\bS(\d{1,2})E(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"第\s*(\d{1,4}|[一二三四五六七八九十百]+)\s*(?:集|话|話|回|章)", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseEpisode();

    [GeneratedRegex(@"(?:\bEP|(?<![A-Za-z])E|Episode\s*)[\s._-]*(\d{1,4})(?:v\d)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishEpisode();

    [GeneratedRegex(@"\s+[-–—]\s+(\d{1,4})(?:v\d)?(?:\s|\[|【|\(|（|$)", RegexOptions.CultureInvariant)]
    private static partial Regex FansubDashEpisode();

    [GeneratedRegex(@"[\[【]\s*(\d{1,4})(?:v\d)?\s*[\]】]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketEpisode();

    [GeneratedRegex(@"^\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex PureEpisodeDigits();

    [GeneratedRegex(@"(?:[\s_-])(\d{1,4})(?:v\d)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDelimitedEpisode();

    [GeneratedRegex(@"\b(?:2160p|1080p|1080i|720p|480p|4k|8k|x26[45]|h\.?26[45]|10bit|8bit|60fps|120fps|5\.1|7\.1|2\.0|ddp\d\.\d|aac\d\.\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalNumberToken();

    [GeneratedRegex(@"\b(?:19|20)\d{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateNumberToken();

    [GeneratedRegex(@"(?<!\d)(?:19\d{2}|20\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex YearToken();

    [GeneratedRegex(@"[\(\[（【\s._-]((?:19|20)\d{2})[\)\]）】\s._-]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedOrDelimitedYear();

    [GeneratedRegex(@"(?:\b(SP|OVA|OAD|PV|CM|NCOP|NCED)\b|(番外篇?|特别篇|特典|预告片?|先导片?|总集篇))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpecialEpisodeCue();

    [GeneratedRegex(@"(?:电影|剧场版|movie|film)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovieTypeHint();

    [GeneratedRegex(@"^(?:HD|BD|4K|1080P|2160P|TC|TS|正片|高清|超清|蓝光|原画|抢先版?|国语|粤语|中字|未删减|完整版)[\s\w\u4e00-\u9fff]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovieEpisodeLabel();

    [GeneratedRegex(@"(?:剧场版|大电影|电影版)", RegexOptions.CultureInvariant)]
    private static partial Regex MovieTitleHint();
}
