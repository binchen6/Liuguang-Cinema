using System.Globalization;
using System.Text.Json;
using System.Xml;
using TV.Services.Http;

namespace TV.Services.Playback;

public sealed record DanmakuEntry(double Seconds, string Text, uint Rgb);

public sealed class DanmakuService(SourceHttpClient http)
{
    public async Task<IReadOnlyList<DanmakuEntry>> LoadAsync(Uri uri, CancellationToken token)
    {
        byte[] bytes;
        if (uri.IsFile)
        {
            var file = new FileInfo(uri.LocalPath);
            if (!file.Exists || file.Length > 4 * 1024 * 1024) throw new InvalidDataException("本地弹幕不存在或超过大小限制。");
            bytes = await File.ReadAllBytesAsync(file.FullName, token).ConfigureAwait(false);
        }
        else bytes = (await http.GetBytesAsync(uri.AbsoluteUri, maxBytes: 4 * 1024 * 1024, cancellationToken: token).ConfigureAwait(false)).Content;
        var content = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', '\r', '\n', ' ');
        var comments = new List<DanmakuEntry>();
        if (content.StartsWith('<'))
        {
            using var input = new StringReader(content);
            using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 4 * 1024 * 1024 });
            while (!xml.EOF && comments.Count < 30_000)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "d")
                {
                    var fields = (xml.GetAttribute("p") ?? "").Split(',');
                    Add(comments, fields.ElementAtOrDefault(0), xml.ReadElementContentAsString(), fields.ElementAtOrDefault(3));
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
                    var fields = Read(item, "p").Split(',');
                    var time = Read(item, "time");
                    var text = Read(item, "text");
                    if (text.Length == 0) text = Read(item, "m");
                    if (text.Length == 0) text = Read(item, "content");
                    Add(comments, time.Length > 0 ? time : fields[0], text, fields.ElementAtOrDefault(3) ?? fields.ElementAtOrDefault(2) ?? Read(item, "color"));
                }
                else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 5)
                    Add(comments, item[0].ToString(), item[4].ToString(), item[2].ToString());
            }
        }
        return comments.OrderBy(comment => comment.Seconds).ToArray();
    }

    private static string Read(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) ? value.ToString() : string.Empty;

    private static void Add(List<DanmakuEntry> target, string? time, string text, string? color)
    {
        if (!double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > 24 * 60 * 60 || string.IsNullOrWhiteSpace(text)) return;
        var rgb = color?.StartsWith('#') == true
            ? (uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0xFFFFFF)
            : (uint.TryParse(color, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0xFFFFFF);
        target.Add(new DanmakuEntry(seconds, text[..Math.Min(100, text.Length)], rgb & 0xFFFFFF));
    }
}
