using System.Globalization;
using System.Text.Json;
using System.Xml;
using TV.Services.Http;

namespace TV.Services.Playback;

public enum DanmakuMode
{
    Scroll = 1,
    Bottom = 4,
    Top = 5
}

public sealed record DanmakuEntry(double Seconds, string Text, uint Rgb, DanmakuMode Mode = DanmakuMode.Scroll);

internal sealed class DanmakuNotMatchedException(string message) : Exception(message);

public sealed record DanmakuPreferences(
    bool Enabled = false,
    int Size = 22,
    int Color = 0,
    bool BasicFilter = true,
    string BlockedWords = "",
    string Source = "auto",
    bool AutoMatch = true,
    double Opacity = 0.85,
    double Speed = 1.0,
    double DisplayArea = 0.50,
    int Density = 1,
    bool TextOutline = true,
    bool Bold = true,
    bool ShowScroll = true,
    bool ShowTop = true,
    bool ShowBottom = true,
    bool ShowColor = true,
    double TimeOffsetSeconds = 0.0,
    string BlockedPatterns = "")
{
    public DanmakuPreferences Normalize() => this with
    {
        Size = Math.Clamp(Size, 14, 36),
        Color = Math.Clamp(Color, 0, 3),
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.15, 1.0) : 0.85,
        Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, 0.5, 2.0) : 1.0,
        DisplayArea = double.IsFinite(DisplayArea) ? Math.Clamp(DisplayArea, 0.20, 1.0) : 0.50,
        Density = Math.Clamp(Density, 0, 3),
        TimeOffsetSeconds = double.IsFinite(TimeOffsetSeconds) ? Math.Clamp(TimeOffsetSeconds, -15.0, 15.0) : 0.0,
        BlockedWords = BlockedWords ?? string.Empty,
        BlockedPatterns = BlockedPatterns ?? string.Empty,
        Source = NormalizeSource(Source)
    };

    public static string NormalizeSource(string? source)
    {
        if (string.Equals(source, "dandanplay", StringComparison.OrdinalIgnoreCase)) return "dandanplay";
        if (string.Equals(source, "bilibili", StringComparison.OrdinalIgnoreCase)) return "bilibili";
        if (string.Equals(source, "aggregate", StringComparison.OrdinalIgnoreCase)) return "aggregate";
        if (string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        // 旧版本默认为 dandanplay；新默认改为智能匹配，未知值按智能匹配处理
        return "auto";
    }
}

/// <summary>智能弹幕匹配顺序（B站 / 其他聚合 / 弹弹Play 三类来源的尝试先后），持久化于 playback.danmakuOrder。</summary>
public static class DanmakuSourceOrder
{
    public const string SettingKey = "playback.danmakuOrder";
    public const string DefaultOrder = "bilibili,aggregate,dandanplay";
    public static readonly string DefaultLabel = Describe(DefaultOrder);

    public static readonly string[] Categories = ["bilibili", "aggregate", "dandanplay"];
    public static readonly string[] CategoryLabels = ["哔哩哔哩", "其他聚合", "弹弹play"];

    /// <summary>六种全排列预设，供设置页下拉选择。</summary>
    public static readonly (string Order, string Label)[] Presets =
    [
        ("bilibili,aggregate,dandanplay", "哔哩哔哩 → 其他聚合 → 弹弹play（推荐）"),
        ("bilibili,dandanplay,aggregate", "哔哩哔哩 → 弹弹play → 其他聚合"),
        ("aggregate,bilibili,dandanplay", "其他聚合 → 哔哩哔哩 → 弹弹play"),
        ("aggregate,dandanplay,bilibili", "其他聚合 → 弹弹play → 哔哩哔哩"),
        ("dandanplay,bilibili,aggregate", "弹弹play → 哔哩哔哩 → 其他聚合"),
        ("dandanplay,aggregate,bilibili", "弹弹play → 其他聚合 → 哔哩哔哩")
    ];

    public static string LabelOf(string category) => Array.FindIndex(Categories, c => c == category) is var i && i >= 0
        ? CategoryLabels[i] : category;

    public static string Describe(string order) => string.Join(" → ",
        Parse(order).Select(LabelOf));

    /// <summary>解析持久化顺序，补齐缺失类别并去重；无法识别时返回默认顺序。</summary>
    public static IReadOnlyList<string> Parse(string? value)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(value))
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = part.Trim().ToLowerInvariant();
                if (Categories.Contains(normalized) && !result.Contains(normalized)) result.Add(normalized);
            }
        }
        foreach (var category in Categories)
            if (!result.Contains(category)) result.Add(category);
        return result;
    }

    public static int IndexOfPreset(string? value) => Array.FindIndex(Presets, preset =>
        string.Equals(preset.Order, value, StringComparison.OrdinalIgnoreCase));
}

public sealed class DanmakuService(SourceHttpClient http)
{
    private const int MaxContentBytes = 8 * 1024 * 1024;
    private const int MaxCommentCount = 30_000;
    private const int MaxCommentLength = 100;

    public async Task<IReadOnlyList<DanmakuEntry>> LoadAsync(Uri uri, CancellationToken token)
    {
        byte[] bytes;
        if (uri.IsFile)
        {
            using var file = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = file.Length;
            if (length > MaxContentBytes) throw new InvalidDataException("本地弹幕超过 8 MB。");
            bytes = new byte[(int)length];
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        else
        {
            try
            {
                bytes = (await http.GetBytesAsync(uri.AbsoluteUri, maxBytes: MaxContentBytes, cancellationToken: token).ConfigureAwait(false)).Content;
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("弹幕文件下载超时，请稍后重试。", ex);
            }
        }
        return ParseContent(System.Text.Encoding.UTF8.GetString(bytes), token);
    }

    public static DanmakuEntry[] ParseContent(string raw) => ParseContent(raw, CancellationToken.None);

    internal static DanmakuEntry[] ParseContent(string raw, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var content = raw.TrimStart('\uFEFF', '\r', '\n', ' ', '\t');
        var comments = new List<DanmakuEntry>();
        if (content.StartsWith('<'))
        {
            using var input = new StringReader(content);
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = MaxContentBytes });
            while (!xml.EOF && comments.Count < MaxCommentCount)
            {
                token.ThrowIfCancellationRequested();
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "d")
                {
                    var fields = (xml.GetAttribute("p") ?? "").Split(',');
                    Add(comments, fields.ElementAtOrDefault(0), xml.ReadElementContentAsString(),
                        fields.ElementAtOrDefault(3), fields.ElementAtOrDefault(1));
                }
                else if (!xml.Read()) break;
            }
        }
        else
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                root = root.TryGetProperty("comments", out var items) || root.TryGetProperty("data", out items) ? items : default;
            if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("无法识别弹幕格式。");
            foreach (var item in root.EnumerateArray().Take(MaxCommentCount))
            {
                token.ThrowIfCancellationRequested();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var pAttr = Read(item, "p");
                    var fields = pAttr.Length > 0 ? pAttr.Split(',') : [];
                    var time = Read(item, "time");
                    var text = Read(item, "text");
                    if (text.Length == 0) text = Read(item, "m");
                    if (text.Length == 0) text = Read(item, "content");
                    var mode = Read(item, "mode");
                    if (mode.Length == 0) mode = Read(item, "type");
                    if (mode.Length == 0) mode = fields.ElementAtOrDefault(1) ?? string.Empty;
                    // DanDanPlay JSON p format has 4 fields: "time,mode,color,userId" (color at index 2).
                    // Bilibili-like p format has 5-8 fields: "time,mode,size,color,..." (color at index 3).
                    var color = Read(item, "color");
                    if (color.Length == 0)
                        color = fields.Length >= 5 ? fields[3] : fields.ElementAtOrDefault(2) ?? string.Empty;
                    Add(comments, time.Length > 0 ? time : fields.ElementAtOrDefault(0), text, color, mode);
                }
                else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 5)
                    Add(comments, item[0].ToString(), item[4].ToString(), item[2].ToString(), item[1].ToString());
            }
        }
        comments.Sort(static (a, b) => a.Seconds.CompareTo(b.Seconds));
        return comments.ToArray();
    }

    private static string Read(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) ? value.ToString() : string.Empty;

    private static void Add(List<DanmakuEntry> target, string? time, string text, string? color, string? mode)
    {
        if (!double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > 24 * 60 * 60 || string.IsNullOrWhiteSpace(text)) return;
        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0) return;
        var rgb = color?.StartsWith('#') == true
            ? (uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0xFFFFFF)
            : (uint.TryParse(color, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0xFFFFFF);
        var parsedMode = ParseMode(mode);
        if (clean.Length > MaxCommentLength)
        {
            var length = char.IsSurrogatePair(clean, MaxCommentLength - 1) ? MaxCommentLength - 1 : MaxCommentLength;
            clean = clean[..length];
        }
        target.Add(new DanmakuEntry(seconds, clean, rgb & 0xFFFFFF, parsedMode));
    }

    private static DanmakuMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return DanmakuMode.Scroll;
        if (int.TryParse(mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            return num switch { 4 => DanmakuMode.Bottom, 5 => DanmakuMode.Top, _ => DanmakuMode.Scroll };
        if (mode.Equals("top", StringComparison.OrdinalIgnoreCase)) return DanmakuMode.Top;
        if (mode.Equals("bottom", StringComparison.OrdinalIgnoreCase)) return DanmakuMode.Bottom;
        return DanmakuMode.Scroll;
    }
}
