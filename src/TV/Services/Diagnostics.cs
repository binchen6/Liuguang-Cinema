using System.Text;

namespace TV.Services;

public static class Diagnostics
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static long _size;
    private static bool _closed;

    public static void Log(string message)
    {
        lock (Sync)
        {
            if (_closed) return;
            try
            {
                var path = Path.Combine(Persistence.AppPaths.Root, "diagnostic.log");
                Directory.CreateDirectory(Persistence.AppPaths.Root);
                if (_writer is null)
                {
                    _size = File.Exists(path) ? new FileInfo(path).Length : 0;
                    if (_size >= MaximumLogBytes) RotateCore(path);
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                }
                var line = $"[{DateTimeOffset.Now:O}] {message}";
                _writer.WriteLine(line);
                _size += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_size >= MaximumLogBytes)
                {
                    _writer.Dispose();
                    _writer = null;
                }
            }
            catch { }
        }
    }

    private static void RotateCore(string path)
    {
        if (!File.Exists(path)) return;
        File.Move(path, path + ".1", true);
        _size = 0;
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_closed) return;
            _closed = true;
            try { _writer?.Dispose(); }
            catch { }
            _writer = null;
        }
    }
}
