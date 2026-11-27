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
    private static readonly string[] GitHubMirrors = ["https://ghfast.top/", "https://gh-proxy.com/"];
    private static readonly TimeSpan ScriptCacheTtl = TimeSpan.FromHours(6);
    private readonly SourceHttpClient _http;
    private string? _pythonExe;
    private bool _pythonChecked;

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
        var python = FindPython();
        if (python is null)
            throw new NotSupportedException("该站点是 Python (.py) 爬虫，需要本机安装 Python 3（并在 PATH 中可用）才能运行。");

        var runtimeDir = EnsureRuntimeFiles();
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
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = runtimeDir
        };
        start.ArgumentList.Add(Path.Combine(runtimeDir, "py_runner.py"));
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Python 爬虫进程。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(site.Timeout, 8, 45)));

        await process.StandardInput.WriteLineAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);
        process.StandardInput.Close();

        var outTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
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

        var dir = Path.Combine(AppPaths.Root, "spider", "py-cache");
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..24].ToLowerInvariant();
        var cachePath = Path.Combine(dir, key + ".py");
        if (File.Exists(cachePath) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) < ScriptCacheTtl)
            return cachePath;

        var candidates = new List<string> { uri.AbsoluteUri };
        if (uri.AbsoluteUri.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsoluteUri.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mirror in GitHubMirrors) candidates.Add(mirror + uri.AbsoluteUri);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = "okhttp/4.12.0" };
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var content = await _http.GetStringAsync(candidate, headers, token).ConfigureAwait(false);
                if (content.TrimStart().StartsWith('<'))
                    throw new InvalidDataException("Python 爬虫地址返回了网页内容。");
                await File.WriteAllTextAsync(cachePath, content, Encoding.UTF8, token).ConfigureAwait(false);
                return cachePath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        if (File.Exists(cachePath)) return cachePath;
        throw last ?? new HttpRequestException("Python 爬虫脚本下载失败。");
    }

    private string? FindPython()
    {
        if (_pythonChecked) return _pythonExe;
        _pythonChecked = true;
        foreach (var candidate in new[] { "python", "python3" })
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (p is not null && p.WaitForExit(2000) && p.ExitCode == 0)
                {
                    _pythonExe = candidate;
                    return _pythonExe;
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
                import requests
                return requests.post(url, params=params, data=data, json=json_data, headers=headers, timeout=timeout)
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
