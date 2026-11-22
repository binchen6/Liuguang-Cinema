using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TV.Services.AI;

/// <summary>
/// Parses and writes SRT, WebVTT, and ASS/SSA subtitle files, plus cinema-friendly line splitting.
/// </summary>
public static partial class SubtitleFileCodec
{
    public static List<SubtitleCue> ParseFile(string filePath)
    {
        var text = File.ReadAllText(filePath, Encoding.UTF8);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ass" or ".ssa" => ParseAss(text),
            ".vtt" => ParseVttOrSrt(text),
            _ => ParseVttOrSrt(text)
        };
    }

    public static List<SubtitleCue> ParseVttOrSrt(string raw)
    {
        var text = raw.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var cues = new List<SubtitleCue>(blocks.Length);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;
            var timeLineIndex = -1;
            for (var i = 0; i < Math.Min(lines.Length, 3); i++)
            {
                if (lines[i].Contains("-->", StringComparison.Ordinal))
                {
                    timeLineIndex = i;
                    break;
                }
            }
            if (timeLineIndex < 0 || timeLineIndex + 1 >= lines.Length) continue;

            var match = TimeRangePattern().Match(lines[timeLineIndex]);
            if (!match.Success) continue;

            var start = ParseTimestamp(match.Groups[1].Value);
            var end = ParseTimestamp(match.Groups[2].Value);
            if (end <= start) end = start + TimeSpan.FromSeconds(1.5);

            var content = string.Join('\n', lines.Skip(timeLineIndex + 1))
                .Replace("\0", string.Empty);
            content = HtmlTagPattern().Replace(content, string.Empty).Trim();
            if (content.Length == 0) continue;
            cues.Add(new SubtitleCue(start, end, content));
        }

        cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    public static List<SubtitleCue> ParseAss(string raw)
    {
        var text = raw.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = new List<SubtitleCue>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            // Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            var parts = line["Dialogue:".Length..].Split(',', 10);
            if (parts.Length < 10) continue;
            var start = ParseAssTime(parts[1].Trim());
            var end = ParseAssTime(parts[2].Trim());
            if (end <= start) continue;
            var body = AssTagPattern().Replace(parts[9], string.Empty)
                .Replace("\\N", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("\\h", " ", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (body.Length == 0) continue;
            cues.Add(new SubtitleCue(start, end, body));
        }
        cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    public static string WriteSrt(IReadOnlyList<SubtitleCue> cues, bool bilingual = false)
    {
        var sb = new StringBuilder(cues.Count * 96);
        var idx = 0;
        foreach (var cue in cues)
        {
            var body = FormatCueBody(cue, bilingual);
            if (string.IsNullOrWhiteSpace(body)) continue;
            sb.Append(++idx).Append('\n');
            sb.Append(FormatSrtTime(cue.Start)).Append(" --> ").Append(FormatSrtTime(cue.End)).Append('\n');
            sb.Append(body).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string WriteVtt(IReadOnlyList<SubtitleCue> cues, bool bilingual = false)
    {
        var sb = new StringBuilder(cues.Count * 96 + 16);
        sb.Append("WEBVTT\n\n");
        var idx = 0;
        foreach (var cue in cues)
        {
            var body = FormatCueBody(cue, bilingual);
            if (string.IsNullOrWhiteSpace(body)) continue;
            sb.Append(++idx).Append('\n');
            sb.Append(FormatVttTime(cue.Start)).Append(" --> ").Append(FormatVttTime(cue.End)).Append('\n');
            sb.Append(body).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits overly long ASR sentences into readable cinema subtitle segments using exact word-level timestamps
    /// when available (falling back to proportional interpolation), then aligns and normalizes the timeline
    /// against the video duration (fixing overlaps, micro-gaps, and too-short cues).
    /// </summary>
    public static List<SubtitleCue> SplitAndAlignCues(IReadOnlyList<SubtitleCue> input, TimeSpan? videoDuration = null)
    {
        var output = new List<SubtitleCue>(input.Count);
        foreach (var cue in input)
        {
            var text = cue.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0) continue;

            // Anchor sentence start/end tightly to word timestamps when available
            var anchoredStart = cue.Start;
            var anchoredEnd = cue.End;
            if (cue.Words is { Count: > 0 } words)
            {
                anchoredStart = words[0].Start;
                anchoredEnd = words[^1].End > anchoredStart ? words[^1].End : anchoredStart + TimeSpan.FromMilliseconds(900);
            }

            var maxLen = LooksMostlyCjk(text) ? 32 : 72;
            if (text.Length <= maxLen || (anchoredEnd - anchoredStart).TotalMilliseconds < 1500)
            {
                output.Add(cue with { Start = anchoredStart, End = anchoredEnd, Text = text });
                continue;
            }

            // Prefer exact word-level splitting when word timestamps are provided by the ASR model
            if (cue.Words is { Count: >= 2 } wordList)
            {
                var wordSegments = SplitByWords(wordList, maxLen);
                if (wordSegments.Count > 0)
                {
                    output.AddRange(wordSegments);
                    continue;
                }
            }

            var segments = SplitTextSmart(text, maxLen);
            if (segments.Count <= 1)
            {
                output.Add(cue with { Start = anchoredStart, End = anchoredEnd, Text = text });
                continue;
            }

            var totalChars = Math.Max(1, segments.Sum(s => s.Length));
            var totalMs = (anchoredEnd - anchoredStart).TotalMilliseconds;
            var cursorMs = anchoredStart.TotalMilliseconds;
            for (var i = 0; i < segments.Count; i++)
            {
                var shareMs = i == segments.Count - 1
                    ? anchoredEnd.TotalMilliseconds - cursorMs
                    : Math.Max(450, totalMs * segments[i].Length / totalChars);
                var segStart = TimeSpan.FromMilliseconds(cursorMs);
                var segEnd = TimeSpan.FromMilliseconds(Math.Min(anchoredEnd.TotalMilliseconds, cursorMs + shareMs));
                if (segEnd <= segStart) segEnd = segStart + TimeSpan.FromMilliseconds(450);
                output.Add(new SubtitleCue(segStart, segEnd, segments[i]));
                cursorMs = segEnd.TotalMilliseconds;
            }
        }

        return NormalizeTimeline(output, videoDuration);
    }

    public static List<SubtitleCue> SplitLongCues(IReadOnlyList<SubtitleCue> input) =>
        SplitAndAlignCues(input, videoDuration: null);

    /// <summary>
    /// Applies speed scaling and time offset to cues (used for player subtitle speed/shift adjustment).
    /// speedFactor > 1.0 plays subtitles faster (earlier over time); offsetMs &lt; 0 shifts earlier, &gt; 0 shifts later.
    /// </summary>
    public static List<SubtitleCue> RetimeCues(IReadOnlyList<SubtitleCue> cues, double speedFactor, long offsetMs)
    {
        var speed = double.IsFinite(speedFactor) ? Math.Clamp(speedFactor, 0.5, 2.0) : 1.0;
        var offset = TimeSpan.FromMilliseconds(Math.Clamp(offsetMs, -60_000, 60_000));
        var result = new List<SubtitleCue>(cues.Count);
        foreach (var cue in cues)
        {
            var start = TimeSpan.FromMilliseconds(cue.Start.TotalMilliseconds / speed) + offset;
            var end = TimeSpan.FromMilliseconds(cue.End.TotalMilliseconds / speed) + offset;
            if (end <= TimeSpan.Zero) continue;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (end <= start) end = start + TimeSpan.FromMilliseconds(500);
            result.Add(cue with { Start = start, End = end });
        }
        return NormalizeTimeline(result, videoDuration: null);
    }

    private static List<SubtitleCue> SplitByWords(IReadOnlyList<SubtitleWord> words, int maxLen)
    {
        var result = new List<SubtitleCue>();
        var currentText = new StringBuilder();
        var segStart = words[0].Start;
        var segEnd = words[0].End;
        var lastGoodBreakLen = -1;
        var lastGoodBreakEnd = segEnd;
        var lastGoodBreakNextIndex = -1;

        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (currentText.Length == 0)
            {
                segStart = w.Start;
                lastGoodBreakLen = -1;
            }
            else if (NeedsSpaceBetween(currentText[^1], w.Text))
            {
                currentText.Append(' ');
            }

            currentText.Append(w.Text);
            segEnd = w.End > segStart ? w.End : segEnd;

            // Track punctuation or long pause (> 550ms) as natural cinema line break points
            var endsPunct = w.Text.Length > 0 && IsBreakPunctuation(w.Text[^1]);
            var pauseAfter = i + 1 < words.Count && (words[i + 1].Start - w.End).TotalMilliseconds >= 550;
            if (endsPunct || pauseAfter)
            {
                lastGoodBreakLen = currentText.Length;
                lastGoodBreakEnd = segEnd;
                lastGoodBreakNextIndex = i + 1;
            }

            if ((pauseAfter && currentText.Length >= maxLen / 2) || currentText.Length >= maxLen)
            {
                if (lastGoodBreakLen > 0 && lastGoodBreakNextIndex > 0 && lastGoodBreakLen < currentText.Length)
                {
                    var head = currentText.ToString(0, lastGoodBreakLen).Trim();
                    if (head.Length > 0)
                        result.Add(new SubtitleCue(segStart, lastGoodBreakEnd, head));
                    currentText.Clear();
                    i = lastGoodBreakNextIndex - 1;
                    lastGoodBreakLen = -1;
                }
                else
                {
                    var text = currentText.ToString().Trim();
                    if (text.Length > 0)
                        result.Add(new SubtitleCue(segStart, segEnd, text));
                    currentText.Clear();
                    lastGoodBreakLen = -1;
                }
            }
        }

        if (currentText.Length > 0)
        {
            var tail = currentText.ToString().Trim();
            if (tail.Length > 0)
                result.Add(new SubtitleCue(segStart, segEnd > segStart ? segEnd : segStart + TimeSpan.FromMilliseconds(900), tail));
        }

        return result;
    }

    private static List<SubtitleCue> NormalizeTimeline(List<SubtitleCue> cues, TimeSpan? videoDuration)
    {
        if (cues.Count == 0) return cues;
        cues.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var maxEnd = videoDuration is { TotalMilliseconds: > 1000 } d ? d : TimeSpan.MaxValue;
        var minDuration = TimeSpan.FromMilliseconds(850);
        var tinyGap = TimeSpan.FromMilliseconds(140);

        for (var i = 0; i < cues.Count; i++)
        {
            var start = cues[i].Start < TimeSpan.Zero ? TimeSpan.Zero : cues[i].Start;
            var end = cues[i].End;
            if (start >= maxEnd)
            {
                cues.RemoveRange(i, cues.Count - i);
                break;
            }
            if (end < start + minDuration) end = start + minDuration;
            if (end > maxEnd) end = maxEnd;

            if (i + 1 < cues.Count)
            {
                var nextStart = cues[i + 1].Start;
                // Resolve overlap cleanly without colliding with next spoken onset
                if (end > nextStart)
                    end = nextStart > start + TimeSpan.FromMilliseconds(250) ? nextStart - TimeSpan.FromMilliseconds(20) : nextStart;
                // Bridge tiny unreadable flicker gaps (< 140ms) smoothly
                else if (nextStart - end <= tinyGap && nextStart > end)
                    end = nextStart - TimeSpan.FromMilliseconds(20);
            }

            if (end <= start) end = start + TimeSpan.FromMilliseconds(250);
            cues[i] = cues[i] with { Start = start, End = end };
        }

        return cues;
    }

    private static bool IsBreakPunctuation(char ch) =>
        ch is '。' or '！' or '？' or '，' or '；' or '.' or '!' or '?' or ',' or ';';

    private static bool NeedsSpaceBetween(char prev, string next) =>
        next.Length > 0 && char.IsAsciiLetterOrDigit(prev) && char.IsAsciiLetterOrDigit(next[0]);

    private static List<string> SplitTextSmart(string text, int maxLen)
    {
        var parts = new List<string>();
        var remaining = text.AsSpan().Trim();
        while (remaining.Length > maxLen)
        {
            var splitAt = -1;
            var minSplit = Math.Max(6, maxLen / 3);
            // Prefer splitting at strong/weak punctuation near maxLen
            for (var i = Math.Min(maxLen, remaining.Length - 1); i >= minSplit; i--)
            {
                var ch = remaining[i];
                if (ch is '。' or '！' or '？' or '，' or '；' or '.' or '!' or '?' or ',' or ';')
                {
                    splitAt = i + 1;
                    break;
                }
            }
            if (splitAt < 0)
            {
                // Fallback to whitespace for Latin text
                for (var i = Math.Min(maxLen, remaining.Length - 1); i >= minSplit; i--)
                {
                    if (char.IsWhiteSpace(remaining[i]))
                    {
                        splitAt = i;
                        break;
                    }
                }
            }
            if (splitAt <= 0) splitAt = maxLen;
            var head = remaining[..splitAt].Trim().ToString();
            if (head.Length > 0) parts.Add(head);
            remaining = remaining[splitAt..].Trim();
        }
        if (remaining.Length > 0) parts.Add(remaining.ToString());
        return parts;
    }

    private static bool LooksMostlyCjk(string text)
    {
        var cjk = 0;
        foreach (var ch in text)
            if (ch is >= '\u4E00' and <= '\u9FFF' or >= '\u3040' and <= '\u30FF' or >= '\uAC00' and <= '\uD7AF') cjk++;
        return cjk * 3 >= text.Length;
    }

    private static string FormatCueBody(SubtitleCue cue, bool bilingual)
    {
        if (string.IsNullOrWhiteSpace(cue.TranslatedText)) return cue.Text.Trim();
        if (!bilingual) return cue.TranslatedText.Trim();
        if (string.Equals(cue.TranslatedText.Trim(), cue.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            return cue.TranslatedText.Trim();
        return $"{cue.TranslatedText.Trim()}\n{cue.Text.Trim()}";
    }

    private static string FormatSrtTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
    }

    private static string FormatVttTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
    }

    private static TimeSpan ParseTimestamp(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length == 2) normalized = "00:" + normalized;
        return TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var ts) ? ts : TimeSpan.Zero;
    }

    private static TimeSpan ParseAssTime(string value)
    {
        // h:mm:ss.cc
        return TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var ts) ? ts : TimeSpan.Zero;
    }

    [GeneratedRegex(@"((?:\d{1,2}:)?\d{2}:\d{2}[.,]\d{1,3})\s*-->\s*((?:\d{1,2}:)?\d{2}:\d{2}[.,]\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRangePattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex AssTagPattern();
}
