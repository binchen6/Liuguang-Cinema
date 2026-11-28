using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TV.Models;
using TV.Services.Http;
using TV.Services.Persistence;
using TV.Services.Vod;

namespace TV.Services.Spider;

/// <summary>
/// Universal TVBox Python (.py) spider runner.
/// Auto-detects system Python 3, caches downloaded .py scripts, provides a lightweight MIT
/// base.spider.Spider shim, and invokes standard TVBox spider methods via clean JSON stdio framing.
/// </summary>
public sealed class PySpiderClient
{
    private static readonly TimeSpan ScriptCacheTtl = TimeSpan.FromHours(6);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly SourceHttpClient _http;
    private readonly Lazy<Task<(string Exe, string? Prefix)?>> _python = new(() => Task.Run(FindPython));
    private readonly Lazy<string> _runtimeDirectory = new(EnsureRuntimeFiles);

    public PySpiderClient(SourceHttpClient http) => _http = http;

    public static bool Supports(SourceSite site)
    {
        if (site.Type != 3) return false;
        var api = site.Api.Trim();
        var cleanApi = api.Split('?', 2)[0];
        if (cleanApi.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return true;
        if (api.StartsWith("py_", StringComparison.OrdinalIgnoreCase) &&
            site.Ext.ValueKind == JsonValueKind.String &&
            (site.Ext.GetString() ?? "").Split('?', 2)[0].EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public async Task<CmsResponse> GetHomeAsync(SourceSite site, CancellationToken token = default)
    {
        var raw = await InvokeAsync(site, "homeContent", [], token).ConfigureAwait(false);
        var response = ParseCmsResponse(raw, site);
        if (response.List.Count == 0)
        {
            try
            {
                var extra = await InvokeAsync(site, "homeVideoContent", [], token).ConfigureAwait(false);
                var parsed = ParseCmsResponse(extra, site);
                if (parsed.List.Count > 0) response.List = parsed.List;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return response;
    }

    public async Task<CmsResponse> GetCategoryAsync(SourceSite site, string tid, int page,
        IReadOnlyDictionary<string, string>? filters, CancellationToken token = default)
    {
        var extendJson = filters is { Count: > 0 } ? JsonSerializer.Serialize(filters) : "{}";
        var raw = await InvokeAsync(site, "categoryContent",
            [tid, Math.Max(1, page).ToString(CultureInfo.InvariantCulture), extendJson], token).ConfigureAwait(false);
        return ParseCmsResponse(raw, site);
    }

    public async Task<CmsResponse> SearchAsync(SourceSite site, string keyword, int page = 1, CancellationToken token = default)
    {
        var raw = await InvokeAsync(site, "searchContent",
            [keyword, Math.Max(1, page).ToString(CultureInfo.InvariantCulture)], token).ConfigureAwait(false);
        return ParseCmsResponse(raw, site);
    }

    public async Task<VodItem> GetDetailAsync(SourceSite site, string id, CancellationToken token = default)
    {
        var raw = await InvokeAsync(site, "detailContent", [JsonSerializer.Serialize(new[] { id })], token).ConfigureAwait(false);
        var response = ParseCmsResponse(raw, site);
        return response.List.FirstOrDefault() ?? throw new InvalidDataException("Python 爬虫未返回视频详情。");
    }

    public async Task<SpiderPlayResult> ResolvePlayAsync(SourceSite site, string flag, string id, CancellationToken token = default)
    {
        var raw = await InvokeAsync(site, "playerContent", [flag, id, "[]"], token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        return SpiderVodClient.ReadPlayResult(doc.RootElement);
    }

    private async Task<string> InvokeAsync(SourceSite site, string method, string[] args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var python = await _python.Value.WaitAsync(token).ConfigureAwait(false) ??
            throw new NotSupportedException("该站点需要 Python 3。请使用带 Python 运行时的流光影院安装包，或指定本机 Python 解释器。");
        var runtimeDir = _runtimeDirectory.Value;
        var scriptPath = await EnsureScriptFileAsync(site, token).ConfigureAwait(false);
        var ext = site.Ext.ValueKind switch
        {
            JsonValueKind.String => site.Ext.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => site.Ext.GetRawText(),
            _ => string.Empty
        };

        var payload = JsonSerializer.Serialize(new
        {
            script = scriptPath,
            method,
            ext,
            args
        });

        var start = new ProcessStartInfo
        {
            FileName = python.Exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = runtimeDir
        };
        if (python.Prefix is { } prefix)
            start.ArgumentList.Add(prefix);
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(Path.Combine(runtimeDir, "py_runner.py"));
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Python 爬虫进程。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 8, 45)));

        var outTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        var stdout = (await outTask.ConfigureAwait(false)).Trim();
        var stderr = (await errTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || string.IsNullOrEmpty(stdout))
        {
            var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Python 爬虫执行失败";
            throw new InvalidOperationException(reason);
        }
        using var envelope = JsonDocument.Parse(stdout);
        var root = envelope.RootElement;
        if (!root.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
        {
            var errMsg = root.TryGetProperty("error", out var eEl) ? eEl.GetString() : "Python 爬虫返回错误";
            throw new InvalidOperationException(errMsg ?? "Python 爬虫返回错误");
        }
        return root.TryGetProperty("data", out var dataEl)
            ? (dataEl.ValueKind == JsonValueKind.String ? dataEl.GetString() ?? "" : dataEl.GetRawText())
            : string.Empty;
    }

    private async Task<string> EnsureScriptFileAsync(SourceSite site, CancellationToken token)
    {
        var rawTarget = site.Api.Trim().Split('?', 2)[0].EndsWith(".py", StringComparison.OrdinalIgnoreCase)
            ? site.Api.Trim()
            : site.Ext.ValueKind == JsonValueKind.String ? (site.Ext.GetString()?.Trim() ?? "") : "";
        if (rawTarget.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return new Uri(rawTarget).LocalPath;
        if (!Uri.TryCreate(rawTarget, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException($"不支持的 Python 爬虫地址：{rawTarget}");

        var dir = Path.Combine(AppPaths.Root, "spider", "py-origin-cache");
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..24].ToLowerInvariant();
        var cachePath = Path.Combine(dir, key + ".py");
        if (File.Exists(cachePath) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) < ScriptCacheTtl)
            return cachePath;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = "okhttp/4.12.0" };
        try
        {
            // Python runs with the app user's permissions. Download executable scripts from their configured origin.
            var payload = await _http.GetBytesAsync(uri.AbsoluteUri, headers, maxBytes: 4 * 1024 * 1024,
                cancellationToken: token, scopeRedirectHeaders: true).ConfigureAwait(false);
            var content = SourceHttpClient.DecodeText(payload.Content);
            if (string.IsNullOrWhiteSpace(content) || content.TrimStart().StartsWith('<'))
                throw new InvalidDataException("Python 爬虫地址返回了网页内容。");
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, content, Utf8WithoutBom, token).ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
            return cachePath;
        }
        catch (Exception) when (!token.IsCancellationRequested && File.Exists(cachePath))
        {
            return cachePath;
        }
    }

    private static (string Exe, string? Prefix)? FindPython()
    {
        var candidates = new List<(string Exe, string? Prefix)>();
        var configured = Environment.GetEnvironmentVariable("TV_PYTHON_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
            candidates.Add((configured, null));
        // Preserve environments with extra Python packages. Packaged Python supplies a clean fallback.
        candidates.AddRange([("py", "-3"), ("python", null), ("python3", null)]);
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        if (File.Exists(bundled)) candidates.Add((bundled, null));

        foreach (var (exe, prefixArg) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = prefixArg is null ? "--version" : $"{prefixArg} --version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.Environment["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "0";
                using var p = Process.Start(psi);
                if (p is null) continue;
                if (!p.WaitForExit(2000))
                {
                    p.Kill(entireProcessTree: true);
                    continue;
                }
                if (p.ExitCode == 0)
                {
                    var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                    if (output.StartsWith("Python 3.", StringComparison.OrdinalIgnoreCase))
                    {
                        return (exe, prefixArg);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static string EnsureRuntimeFiles()
    {
        var dir = Path.Combine(AppPaths.Root, "spider", "py-runtime");
        var baseDir = Path.Combine(dir, "base");
        Directory.CreateDirectory(baseDir);

        var initPy = Path.Combine(baseDir, "__init__.py");
        if (!File.Exists(initPy)) File.WriteAllText(initPy, "", Encoding.UTF8);

        var spiderPy = Path.Combine(baseDir, "spider.py");
        File.WriteAllText(spiderPy, BaseSpiderShim, Encoding.UTF8);

        var runnerPy = Path.Combine(dir, "py_runner.py");
        File.WriteAllText(runnerPy, RunnerScript, Encoding.UTF8);
        return dir;
    }

    private static CmsResponse ParseCmsResponse(string raw, SourceSite site)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CmsResponse();
        var response = JsonSerializer.Deserialize<CmsResponse>(raw, JsonUtil.Options) ?? new CmsResponse();
        response.Categories ??= [];
        response.List ??= [];
        response.List.RemoveAll(static item => item is null);
        response.Categories.RemoveAll(static item => item is null);
        foreach (var item in response.List)
        {
            item.SiteKey = site.Key;
            item.SiteName = site.Name;
            item.Content = PlaybackParsing.StripHtml(item.Content);
            item.Blurb = PlaybackParsing.StripHtml(item.Blurb);
        }
        return response;
    }

    private const string BaseSpiderShim = """
        import json
        import re
        import urllib.request
        import urllib.parse

        class Spider:
            def init(self, extend=""):
                pass
            def getName(self):
                return ""
            def isVideoFormat(self, url):
                return False
            def manualVideoCheck(self):
                return False
            def homeContent(self, filter):
                return {}
            def homeVideoContent(self):
                return {}
            def categoryContent(self, tid, pg, filter, extend):
                return {}
            def detailContent(self, ids):
                return {}
            def searchContent(self, key, quick, pg="1"):
                return {}
            def playerContent(self, flag, id, vipFlags):
                return {}
            def localProxy(self, param):
                return []
            def log(self, msg):
                import sys
                sys.stderr.write(str(msg) + "\n")
            def fetch(self, url, params=None, headers=None, timeout=15):
                try:
                    import requests
                    return requests.get(url, params=params, headers=headers, timeout=timeout)
                except ImportError:
                    if params:
                        url = url + ("&" if "?" in url else "?") + urllib.parse.urlencode(params)
                    req = urllib.request.Request(url, headers=headers or {})
                    with urllib.request.urlopen(req, timeout=timeout) as resp:
                        text = resp.read().decode("utf-8", errors="replace")
                        return type("Resp", (), {"text": text, "status_code": resp.status, "json": lambda self=None: json.loads(text)})()
            def post(self, url, params=None, data=None, json_data=None, headers=None, timeout=15):
                try:
                    import requests
                    return requests.post(url, params=params, data=data, json=json_data, headers=headers, timeout=timeout)
                except ImportError:
                    if params:
                        url = url + ("&" if "?" in url else "?") + urllib.parse.urlencode(params)
                    hdrs = dict(headers or {})
                    if json_data is not None:
                        raw = json.dumps(json_data).encode("utf-8")
                        hdrs.setdefault("Content-Type", "application/json")
                    elif isinstance(data, dict):
                        raw = urllib.parse.urlencode(data).encode("utf-8")
                        hdrs.setdefault("Content-Type", "application/x-www-form-urlencoded")
                    else:
                        raw = (str(data or "")).encode("utf-8")
                    req = urllib.request.Request(url, data=raw, headers=hdrs, method="POST")
                    with urllib.request.urlopen(req, timeout=timeout) as resp:
                        text = resp.read().decode("utf-8", errors="replace")
                        return type("Resp", (), {"text": text, "status_code": resp.status, "json": lambda self=None: json.loads(text)})()
            _cache = {}
            def getCache(self, key):
                return self._cache.get(key)
            def setCache(self, key, value):
                self._cache[key] = value
            def getProxyUrl(self, local=True):
                return "http://127.0.0.1:9978/proxy"
            def regStr(self, reg, src, group=1):
                m = re.search(reg, src)
                return m.group(group) if m else ""
            def cleanText(self, src):
                return re.sub(r"<[^>]+>", "", str(src or "")).strip()
        """;

    private const string RunnerScript = """
        import inspect
        import json
        import os
        import sys

        # The Windows embeddable distribution uses a ._pth file and omits the script directory.
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

        real_out = sys.stdout
        sys.stdout = sys.stderr

        def invoke_variants(sp, name, variants):
            fn = getattr(sp, name, None)
            if fn is None:
                raise AttributeError("Method not found: " + name)
            sig = inspect.signature(fn)
            for args in variants:
                try:
                    sig.bind(*args)
                except TypeError:
                    continue
                return fn(*args)
            return fn(*variants[0])

        def main():
            req = json.loads(sys.stdin.readline())
            script = os.path.abspath(req["script"])
            method = req["method"]
            ext = req.get("ext", "")
            args = req.get("args") or []
            script_dir = os.path.dirname(script)
            if script_dir not in sys.path:
                sys.path.insert(0, script_dir)
            ns = {"__file__": script, "__name__": "tv_py_spider"}
            with open(script, "r", encoding="utf-8-sig") as f:
                exec(compile(f.read(), script, "exec"), ns)
            cls = ns.get("Spider")
            if cls is None:
                raise RuntimeError("Python script does not define Spider class")
            sp = cls()
            try:
                sp.init(ext)
            except TypeError:
                try:
                    sp.init()
                except Exception:
                    pass
            if method == "homeContent":
                result = invoke_variants(sp, "homeContent", [(True,), ()])
            elif method == "homeVideoContent":
                result = sp.homeVideoContent()
            elif method == "categoryContent":
                tid = args[0] if len(args) > 0 else ""
                pg = args[1] if len(args) > 1 else "1"
                extend = json.loads(args[2]) if len(args) > 2 and args[2] else {}
                result = invoke_variants(sp, "categoryContent", [(tid, pg, True, extend), (tid, pg, True)])
            elif method == "detailContent":
                ids = json.loads(args[0]) if args else []
                result = sp.detailContent(ids)
            elif method == "searchContent":
                kw = args[0] if len(args) > 0 else ""
                pg = args[1] if len(args) > 1 else "1"
                result = invoke_variants(sp, "searchContent", [(kw, False, pg), (kw, False), (kw,)])
            elif method == "playerContent":
                flag = args[0] if len(args) > 0 else ""
                pid = args[1] if len(args) > 1 else ""
                vip = json.loads(args[2]) if len(args) > 2 and args[2] else []
                result = sp.playerContent(flag, pid, vip)
            else:
                raise ValueError("Unknown method: " + method)
            real_out.write(json.dumps({"ok": True, "data": result}, ensure_ascii=False) + "\n")
            real_out.flush()

        if __name__ == "__main__":
            try:
                main()
            except Exception as err:
                real_out.write(json.dumps({"ok": False, "error": f"{type(err).__name__}: {err}"}, ensure_ascii=False) + "\n")
                real_out.flush()
        """;
}
