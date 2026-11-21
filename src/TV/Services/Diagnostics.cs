namespace TV.Services;

public static class Diagnostics
{
    public static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(Persistence.AppPaths.Root, "diagnostic.log"), $"[{DateTimeOffset.Now:O}] {message}\n"); }
        catch { }
    }
}
