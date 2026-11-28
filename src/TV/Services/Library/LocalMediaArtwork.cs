using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TV.Models;
using TV.Services.Imaging;
using TV.Services.Metadata;
using TV.Services.Persistence;
using TV.Services.Playback;

namespace TV.Services.Library;

internal sealed class LocalMediaArtwork(DoubanService metadata)
{
    internal const int MaxPictureBytes = 6 * 1024 * 1024;
    internal sealed record Description(string Title, string Year, string Summary, string Picture, string? SubjectId = null);

    internal async Task<Description> ReadAsync(string path, string? titleHint, CancellationToken token, bool coverOnly = false)
    {
        var local = await Task.Run(() => ReadLocal(path, titleHint, token), token).ConfigureAwait(false);
        if (AudioMedia.IsAudio(path) || (local.Picture.Length > 0 && (coverOnly || local.Summary.Length > 0))) return local;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var match = await metadata.GetExactLocalMetadataAsync(MediaMatching.Parse(local.Title, yearHint: local.Year), deadline.Token)
                .ConfigureAwait(false);
            return match is null ? local : local with
            {
                Summary = local.Summary.Length > 0 ? local.Summary : match.Summary,
                Picture = local.Picture.Length > 0 ? local.Picture : match.Picture,
                SubjectId = match.SubjectId
            };
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            Diagnostics.Log("媒体库在线资料: " + ex.GetType().Name);
            return local;
        }
    }

    internal static Description ReadLocal(string path, string? titleHint, CancellationToken token)
    {
        var identity = MediaMatching.Parse(string.IsNullOrWhiteSpace(titleHint) ? Path.GetFileNameWithoutExtension(path) : titleHint);
        var rawTitle = identity.CleanTitle.Length > 1 ? identity.CleanTitle : Path.GetFileNameWithoutExtension(path);
        var description = new Description(rawTitle, identity.Year?.ToString(CultureInfo.InvariantCulture) ?? "", "", "");
        if (!Path.IsPathFullyQualified(path) || new Uri(path).IsUnc || !File.Exists(path)) return description;

        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var video = new FileInfo(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(path + "\n" + video.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + video.Length.ToString(CultureInfo.InvariantCulture)));
        byte[]? art = null;
        try
        {
            using var tags = TagLib.File.Create(path, TagLib.ReadStyle.None);
            var tag = tags.Tag;
            var cover = tag.Pictures.FirstOrDefault(picture => picture.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures.FirstOrDefault();
            if (cover?.Data.Count is > 12 and <= MaxPictureBytes && ImageCacheService.LooksLikeImage(cover.Data.Data)) art = cover.Data.Data;
            description = description with
            {
                Title = CleanLocalText(tag.Title, 160) is { Length: > 0 } title ? title : description.Title,
                Year = tag.Year is >= 1888 and <= 2100 ? tag.Year.ToString(CultureInfo.InvariantCulture) : description.Year,
                Summary = CleanLocalText(tag.Comment, 2400)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.UnsupportedFormatException or TagLib.CorruptFileException) { }
        token.ThrowIfCancellationRequested();
        try
        {
            var nfo = new FileInfo(stem + ".nfo");
            if (nfo.Exists && nfo.Length is > 0 and <= 256 * 1024)
            {
                using var reader = XmlReader.Create(nfo.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 256 * 1024 });
                if (XDocument.Load(reader).Root is { } root)
                    description = description with
                    {
                        Title = CleanLocalText(root.Element("title")?.Value, 160) is { Length: > 0 } title ? title : description.Title,
                        Year = int.TryParse(root.Element("year")?.Value, out var year) && year is >= 1888 and <= 2100 ? year.ToString(CultureInfo.InvariantCulture) : description.Year,
                        Summary = CleanLocalText(root.Element("plot")?.Value, 2400) is { Length: > 0 } plot ? plot : description.Summary
                    };
            }
            if (art is null)
            {
                foreach (var suffix in new[] { ".jpg", ".png", ".webp", ".jpeg", "-poster.jpg", "-poster.png", "-poster.webp" })
                {
                    var sidecar = new FileInfo(stem + suffix);
                    if (!sidecar.Exists || sidecar.Length is <= 12 or > MaxPictureBytes) continue;
                    var bytes = File.ReadAllBytes(sidecar.FullName);
                    if (bytes.Length <= MaxPictureBytes && ImageCacheService.LooksLikeImage(bytes)) { art = bytes; break; }
                }
            }
            if (art is not null)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(art);
                var key = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                Directory.CreateDirectory(AppPaths.Images);
                var target = ImagePath(key);
                if (!File.Exists(target))
                {
                    var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllBytes(temp, art); File.Move(temp, target, overwrite: true); }
                    finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
                }
                description = description with { Picture = "tv-artwork://local/" + key };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
        return description;
    }

    internal static string ImagePath(string key) => Path.Combine(AppPaths.Images, "local-" + key + ".img");

    private static string CleanLocalText(string? value, int limit)
    {
        var text = PlaybackParsing.StripHtml(value ?? "").Trim();
        return text.Length <= limit ? text : text[..limit];
    }
}
