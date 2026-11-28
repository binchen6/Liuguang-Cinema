namespace TV.Models;

internal static class AudioMedia
{
    internal static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".ape", ".alac", ".dsf", ".dff"
    };

    internal static bool IsAudio(string pathOrUrl) =>
        Extensions.Contains(Path.GetExtension(Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri)
            ? uri.IsFile ? uri.LocalPath : uri.AbsolutePath : pathOrUrl));
}
