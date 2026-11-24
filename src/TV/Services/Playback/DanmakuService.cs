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

public sealed record DanmakuPreferences(
    bool Enabled = false,
    int Size = 22,
    int Color = 0,
    bool BasicFilter = true,
    string BlockedWords = "",
    string Source = "dandanplay",
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
    double TimeOffsetSeconds = 0.0)
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
        Source = NormalizeSource(Source)
    };

    public static string NormalizeSource(string? source)
    {
        if (string.Equals(source, "dandanplay", StringComparison.OrdinalIgnoreCase)) return "dandanplay";
        if (string.Equals(source, "bilibili", StringComparison.OrdinalIgnoreCase)) return "bilibili";
        return "origin";
    }
}

public sealed class DanmakuService(SourceHttpClient http)
{
    public async Task<IReadOnlyList<DanmakuEntry>> LoadAsync(Uri uri, CancellationToken token)
    {
        byte[] bytes;
        if (uri.IsFile)
        {
            var file = new FileInfo(uri.LocalPath);
            if (!file.Exists || file.Length > 8 * 1024 * 1024) throw new InvalidDataException("本地弹幕不存在或超过大小限制。");
            bytes = await File.ReadAllBytesAsync(file.FullName, token).ConfigureAwait(false);
        }
        else
        {
            try
            {
                bytes = (await http.GetBytesAsync(uri.AbsoluteUri, maxBytes: 8 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false)).Content;
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("弹幕文件下载超时，请稍后重试。", ex);
            }
        }
        return ParseContent(System.Text.Encoding.UTF8.GetString(bytes));
    }

    public static DanmakuEntry[] ParseContent(string raw)
    {
        var content = raw.TrimStart('\uFEFF', '\r', '\n', ' ');
        var comments = new List<DanmakuEntry>();
        if (content.StartsWith('<'))
        {
            using var input = new StringReader(content);
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 8 * 1024 * 1024 });
            while (!xml.EOF && comments.Count < 30_000)
            {
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
            foreach (var item in root.EnumerateArray().Take(30_000))
            {
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
        target.Add(new DanmakuEntry(seconds, clean[..Math.Min(100, clean.Length)], rgb & 0xFFFFFF, parsedMode));
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
