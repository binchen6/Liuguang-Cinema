using System.Text;
using System.Text.RegularExpressions;

namespace TV.Services;

public static partial class Diagnostics
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static long _size;
    private static bool _closed;
    private static bool _dirCreated;

    public static void Log(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var safe = Redact(message);
        lock (Sync)
        {
            if (_closed) return;
            try
            {
                if (!_dirCreated)
                {
                    Directory.CreateDirectory(Persistence.AppPaths.Root);
                    _dirCreated = true;
                }
                var path = Path.Combine(Persistence.AppPaths.Root, "diagnostic.log");
                if (_writer is null)
                {
                    _size = File.Exists(path) ? new FileInfo(path).Length : 0;
                    if (_size >= MaximumLogBytes) RotateCore(path);
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                }
                var line = $"[{DateTimeOffset.Now:O}] {safe}";
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

    /// <summary>
    /// Redacts API keys, bearer tokens, signatures, and cookie/token query params so secrets never reach logs.
    /// </summary>
    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var text = BailianKeyPattern().Replace(input, "sk-***");
        text = BearerPattern().Replace(text, "Bearer ***");
        text = SecretHeaderPattern().Replace(text, "$1=***");
        return text;
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

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{12,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex BailianKeyPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(X-Signature|AppSecret|Cookie|refresh_token|access_token|apikey)\s*[:=]\s*[^\s;,&""']+")]
    private static partial Regex SecretHeaderPattern();
}
