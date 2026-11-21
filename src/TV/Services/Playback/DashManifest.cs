using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TV.Models;

namespace TV.Services.Playback;

internal sealed record DashSelection(IReadOnlyList<QualityOption> Options, string? Manifest, QualityOption? SelectedQuality);

internal static class DashManifest
{
    internal const string ContentType = "application/dash+xml";

    internal static bool IsPlaylist(string url, string? contentType) =>
        string.Equals(contentType, ContentType, StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);

    internal static DashSelection? SelectQualities(string manifest, Uri address, QualityOption quality)
    {
        XElement? root;
        try
        {
            using var text = new StringReader(manifest.TrimStart('\uFEFF'));
            using var xml = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            root = XDocument.Load(xml).Root;
        }
        catch (XmlException) { return null; }
        if (root?.Name.LocalName != "MPD" || (string?)root.Attribute("type") == "dynamic") return null;
        var ns = root.Name.Namespace;
        if (root.Descendants(ns + "ContentProtection").Any() || root.DescendantsAndSelf().Attributes().Any(attribute =>
            attribute.Name == XName.Get("href", "http://www.w3.org/1999/xlink"))) return null;
        var periods = root.Elements(ns + "Period").Select(period => period.Elements(ns + "AdaptationSet")
            .SelectMany(set => set.Elements(ns + "Representation").Select(rep => ReadVideo(rep, set)))
            .OfType<Representation>().ToArray()).Where(period => period.Length > 0).ToArray();
        if (periods.Length == 0) return null;

        var shared = periods[0].Select(item => item.Format).ToHashSet();
        foreach (var period in periods.Skip(1)) shared.IntersectWith(period.Select(item => item.Format));
        var options = VideoFormat.ToOptions(shared);
        if (!quality.IsSpecific && !quality.RequestsOriginal) return new DashSelection(options, null, null);
        var selected = options.FirstOrDefault(option => new VideoFormat(option.Height, option.FrameRate ?? 0).Matches(quality));
        if (selected is null) return new DashSelection(options, null, null);

        var baseUrls = root.Elements(ns + "BaseURL").ToArray();
        if (baseUrls.Length == 0) root.AddFirst(new XElement(ns + "BaseURL", new Uri(address, ".").AbsoluteUri));
        foreach (var baseUrl in baseUrls)
        {
            if (!Uri.TryCreate(address, baseUrl.Value.Trim(), out var origin) || origin.Scheme is not ("http" or "https") ||
                origin.UserInfo.Length > 0) return null;
            baseUrl.Value = origin.AbsoluteUri;
        }
        foreach (var period in periods)
        {
            var chosen = period.Where(item => item.Format.Matches(selected)).MaxBy(item => item.Bandwidth)!;
            foreach (var other in period.Where(item => item != chosen)) other.Element.Remove();
            foreach (var set in period.Select(item => item.Set).Distinct())
                if (!set.Elements(ns + "Representation").Any()) set.Remove();
        }
        return new DashSelection(options, root.ToString(SaveOptions.DisableFormatting), selected);
    }

    private static Representation? ReadVideo(XElement element, XElement set)
    {
        string? Attribute(string name) => (string?)element.Attribute(name) ?? (string?)set.Attribute(name);
        var type = Attribute("contentType");
        var mime = Attribute("mimeType");
        if (type is not (null or "video") || mime is not null && !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return null;
        _ = int.TryParse(Attribute("height"), NumberStyles.None, CultureInfo.InvariantCulture, out var height);
        if (type is null && mime is null && height == 0) return null;
        _ = long.TryParse(Attribute("bandwidth"), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth);
        return new Representation(element, set, new VideoFormat(height, VideoFormat.ReadFrameRate(Attribute("frameRate"))), bandwidth);
    }

    private sealed record Representation(XElement Element, XElement Set, VideoFormat Format, long Bandwidth);
}
