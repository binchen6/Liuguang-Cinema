using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TV.Models;
using TV.Services.Persistence;

namespace TV.Services.Spider;

public sealed class SpiderRuntime : IDisposable
{
    private sealed record PendingRequest(Process Process, TaskCompletionSource<JsonElement> Completion);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly DatabaseService _database;
    private Process? _process;
    private long _requestId;
    private bool _disposed;
    private static string? _javaPath;

    public SpiderRuntime(DatabaseService database) => _database = database;

    public async Task<JsonElement> SendAsync(SourceSite site, string action, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["api"] = site.Api,
            ["key"] = site.Key,
            ["action"] = action,
            ["jar"] = site.EffectiveJar,
            ["ext"] = site.Ext.ValueKind switch
            {
                JsonValueKind.String => site.Ext.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => site.Ext.GetRawText(),
                _ => string.Empty
            }
        };
        request["ext"] = await ApplyCloudCredentialsAsync(site, request["ext"] as string ?? string.Empty).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(site.EffectiveJar))
            await SyncExternalSpiderCloudFilesAsync().ConfigureAwait(false);
        var id = Interlocked.Increment(ref _requestId);
        request["requestId"] = id;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process process;
        process = await Task.Run(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureStarted();
                var started = _process!;
                _pending[id] = new PendingRequest(started, completion);
                return started;
            }
        }, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 5, 60)));
        try
        {
            var json = JsonSerializer.Serialize(request, JsonUtil.Options);
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Restart(process);
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadResponsesAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 16 * 1024 * 1024) throw new InvalidDataException("爬虫响应超过大小限制。");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("requestId", out var idElement)) continue;
                    long id;
                    if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out id)) { }
                    else if (idElement.ValueKind == JsonValueKind.String && long.TryParse(idElement.GetString(), out id)) { }
                    else continue;
                    if (!_pending.TryGetValue(id, out var pending) || !ReferenceEquals(pending.Process, process) ||
                        !_pending.TryRemove(id, out _)) continue;
                    if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                        pending.Completion.TrySetResult(root.TryGetProperty("data", out var data) ? data.Clone() : default);
                    else
                    {
                        var detail = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(detail))
                            Diagnostics.Log($"Spider error (req {id}): {detail}");
                        pending.Completion.TrySetException(new InvalidOperationException("站点适配器请求失败。"));
                    }
                }
            }
            throw new InvalidOperationException("爬虫进程已退出。");
        }
        catch (Exception ex)
        {
            Restart(process);
            foreach (var pair in _pending.ToArray())
                if (ReferenceEquals(pair.Value.Process, process) && _pending.TryRemove(pair.Key, out var pending))
                    pending.Completion.TrySetException(new InvalidOperationException("爬虫进程连接中断，请重试。", ex));
        }
    }

    private async Task<string> ApplyCloudCredentialsAsync(SourceSite site, string currentExt)
    {
        var api = site.Api;
        if (api.Contains("Quark", StringComparison.OrdinalIgnoreCase))
        {
            var cookie = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.quarkCookie"));
            return cookie.Length > 0 ? JsonSerializer.Serialize(new { cookie }) : currentExt;
        }
        if (api.Equals("csp_UC", StringComparison.OrdinalIgnoreCase))
        {
            var cookie = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.ucCookie"));
            return cookie.Length > 0 ? JsonSerializer.Serialize(new { cookie }) : currentExt;
        }
        if (api.Contains("Ali", StringComparison.OrdinalIgnoreCase) || api.Contains("Wogg", StringComparison.OrdinalIgnoreCase))
        {
            var token = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.aliToken"));
            return token.Length > 0 ? JsonSerializer.Serialize(new { token }) : currentExt;
        }
        if (api.Contains("Cloud", StringComparison.OrdinalIgnoreCase))
        {
            var token = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.aliToken"));
            var cookie = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.quarkCookie"));
            return JsonSerializer.Serialize(new { token, cookie });
        }
        return currentExt;
    }

    private long _lastCloudSyncTick;

    private async Task SyncExternalSpiderCloudFilesAsync()
    {
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastCloudSyncTick) < 30_000) return;
        Volatile.Write(ref _lastCloudSyncTick, now);
        try
        {
            var quark = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.quarkCookie").ConfigureAwait(false));
            var uc = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.ucCookie").ConfigureAwait(false));
            var ali = SecretStore.Unprotect(await _database.GetSettingAsync("cloud.aliToken").ConfigureAwait(false));
            var dirs = new[]
            {
                Path.Combine(AppPaths.Root, "spider", "TV"),
                Path.Combine(AppPaths.Root, "spider", "sdcard", "TV")
            };
            foreach (var dir in dirs)
            {
                Directory.CreateDirectory(dir);
                if (quark.Length > 0) await File.WriteAllTextAsync(Path.Combine(dir, ".quarkcookie"), quark, Encoding.UTF8).ConfigureAwait(false);
                if (uc.Length > 0) await File.WriteAllTextAsync(Path.Combine(dir, ".uccookie"), uc, Encoding.UTF8).ConfigureAwait(false);
                if (ali.Length > 0) await File.WriteAllTextAsync(Path.Combine(dir, ".alitoken"), ali, Encoding.UTF8).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        _process = null;
        AppPaths.EnsureCreated();
        var jar = FindJar();
        var start = new ProcessStartInfo
        {
            FileName = FindJava(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.Root
        };
        // Strip host-global JVM option overrides that could break the bundled Java 17 runtime on other PCs.
        start.Environment.Remove("_JAVA_OPTIONS");
        start.Environment.Remove("JAVA_TOOL_OPTIONS");
        start.Environment.Remove("JDK_JAVA_OPTIONS");
        var spiderDataDir = Path.Combine(AppPaths.Root, "spider");
        Directory.CreateDirectory(spiderDataDir);
        foreach (var argument in new[] { "-Xms16m", "-Xmx384m", "-XX:MaxMetaspaceSize=160m", "-XX:+UseSerialGC", "-XX:TieredStopAtLevel=1", "-Dfile.encoding=UTF-8", $"-Dtv.spider.dataDir={spiderDataDir}", "-jar", jar }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动爬虫运行时。");
        process.BeginErrorReadLine();
        _process = process;
        _ = ReadResponsesAsync(process);
    }

    private const int MinJavaMajor = 17;

    private static string FindJava()
    {
        if (_javaPath is not null && File.Exists(_javaPath)) return _javaPath;
        var candidates = new List<string>();
        var packaged = Path.Combine(AppContext.BaseDirectory, "runtime", "java", "bin", "java.exe");
        if (File.Exists(packaged)) candidates.Add(packaged);
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var fromHome = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(fromHome)) candidates.Add(fromHome);
        }
        var jdks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks");
        if (Directory.Exists(jdks)) candidates.AddRange(Directory.GetDirectories(jdks).Select(dir => Path.Combine(dir, "bin", "java.exe")).Where(File.Exists));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            foreach (var vendor in new[] { "Microsoft", "Eclipse Adoptium", "Java" })
            {
                var vendorDir = Path.Combine(programFiles, vendor);
                if (Directory.Exists(vendorDir))
                    candidates.AddRange(Directory.GetDirectories(vendorDir).Select(dir => Path.Combine(dir, "bin", "java.exe")).Where(File.Exists));
            }
        }
        candidates.Add("java");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var major = ProbeJavaMajor(candidate);
            if (major >= MinJavaMajor)
            {
                Diagnostics.Log($"Spider java: {candidate} (major {major})");
                return _javaPath = candidate;
            }
        }
        throw new NotSupportedException("该站点需要 Java 17 或更高版本。请使用带 Java 运行时的流光影院安装包。");
    }

    private static int ProbeJavaMajor(string javaExe)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            start.Environment.Remove("_JAVA_OPTIONS");
            start.Environment.Remove("JAVA_TOOL_OPTIONS");
            start.Environment.Remove("JDK_JAVA_OPTIONS");
            using var process = Process.Start(start);
            if (process is null) return -1;
            var errTask = process.StandardError.ReadToEndAsync();
            var outTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { } return -1; }
            var text = (errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty) +
                       (outTask.IsCompletedSuccessfully ? outTask.Result : string.Empty);
            var match = Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?");
            if (!match.Success) return -1;
            var major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (major == 1 && match.Groups[2].Success) major = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return major;
        }
        catch { return -1; }
    }

    private static string FindJar()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "runtime", "catvod-engine.jar");
        if (File.Exists(packaged)) return packaged;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent!)
        {
            var candidate = Path.Combine(directory.FullName, "engine", "catvod", "target", "catvod-engine-1.0.0.jar");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到 CatVod 运行时 JAR。", packaged);
    }

    private void Restart(Process? expected = null)
    {
        lock (_sync)
        {
            if (expected is not null && !ReferenceEquals(_process, expected)) return;
            try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Restart();
        }
        foreach (var pair in _pending.ToArray())
            if (_pending.TryRemove(pair.Key, out var pending)) pending.Completion.TrySetException(new ObjectDisposedException(nameof(SpiderRuntime)));
    }
}
