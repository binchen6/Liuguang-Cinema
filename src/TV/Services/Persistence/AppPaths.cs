namespace TV.Services.Persistence;

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("TV_DATA_ROOT") is { Length: > 0 } profile
        ? Path.GetFullPath(profile)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TV");
    public static string Database { get; } = Path.Combine(Root, "tv.db");
    public static string Config { get; } = Path.Combine(Root, "config", "last-good.json");
    public static string Images { get; } = Path.Combine(Root, "cache", "images");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        Directory.CreateDirectory(Images);
    }
}
