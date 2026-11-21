using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TV.Services.Persistence;

namespace TV.Services.Playback;

internal sealed record SniffedMedia(string Url, string ContentType, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// WebView2 嗅探器：当爬虫返回 parse=1 的播放页（HTML 播放器）时，
/// 用隐藏的 WebView2 真实加载页面，从网络请求与 video 元素里抓出真实媒体地址。
/// </summary>
public sealed partial class SniffService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(22);
    private readonly DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Panel? _host;
    private WebView2? _webView;
    private Task<bool>? _ready;
    private CachedMedia? _recent;
    private sealed record CachedMedia(string Page, string? UserAgent, SniffedMedia Media, DateTimeOffset ExpiresAt);

    public SniffService(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Attach(Panel host) => _host = host;

    public void Detach()
    {
        var host = _host;
        _host = null;
        if (_webView is not { } webView) return;
        _webView = null;
        webView.Close();
        host?.Children.Remove(webView);
    }

    private WebView2 CreateView(Panel host)
    {
        var view = new WebView2
        {
            Width = 8,
            Height = 8,
            Opacity = 0.01,
            IsHitTestVisible = false,
            DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)
        };
        host.Children.Add(view);
        return view;
    }

    internal async Task<SniffedMedia?> SniffAsync(string pageUrl, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recent is { } last && last.Page == pageUrl && last.UserAgent == userAgent && last.ExpiresAt > DateTimeOffset.UtcNow)
                return last.Media;
            if (!await EnsureReadyAsync().WaitAsync(cancellationToken).ConfigureAwait(false)) return null;
            var result = await RunOnUi(token => SniffCoreAsync(pageUrl, userAgent, token), cancellationToken).ConfigureAwait(false);
            if (result is not null) _recent = new CachedMedia(pageUrl, userAgent, result, DateTimeOffset.UtcNow.AddSeconds(45));
            Diagnostics.Log(result is null ? "嗅探未找到媒体" : "嗅探已找到媒体");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log("Sniff failed: " + ex.Message);
            return null;
        }
        finally { _gate.Release(); }
    }

    private Task<bool> EnsureReadyAsync() => _ready ??= RunOnUi<bool>(async _ =>
    {
        try
        {
            if (_host is not { } host) return false;
            var webView = _webView ??= CreateView(host);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppPaths.Root, "webview2"), new CoreWebView2EnvironmentOptions());
            if (_host is null) return false;
            await webView.EnsureCoreWebView2Async(environment);
            if (_host is null) return false;
            webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            return true;
        }
        catch (Exception ex)
        {
            if (_host is null) return false;
            Diagnostics.Log("Sniffer init failed: " + ex.Message);
            _ready = null;
            // 目标机器没有 WebView2 运行时：尝试用安装包内置的引导程序静默安装一次
            if (await TryInstallWebView2RuntimeAsync().ConfigureAwait(false)) return await EnsureReadyAsync().ConfigureAwait(false);
            return false;
        }
    }, CancellationToken.None);

    private bool _installAttempted;

    private async Task<bool> TryInstallWebView2RuntimeAsync()
    {
        if (_installAttempted) return false;
        _installAttempted = true;
        try
        {
            var bootstrapper = Path.Combine(AppContext.BaseDirectory, "runtime", "MicrosoftEdgeWebView2Setup.exe");
            if (!File.Exists(bootstrapper)) return false;
            Diagnostics.Log("WebView2 runtime missing, installing bundled bootstrapper...");
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bootstrapper,
                Arguments = "/silent /install",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            Diagnostics.Log("WebView2 bootstrapper exited: " + process.ExitCode);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("WebView2 bootstrapper failed: " + ex.Message);
            return false;
        }
    }

    private async Task<SniffedMedia?> SniffCoreAsync(string pageUrl, string? userAgent, CancellationToken cancellationToken)
    {
        if (_webView?.CoreWebView2 is not { } core) return null;
        var found = new TaskCompletionSource<SniffedMedia?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnResponse(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            try
            {
                if (found.Task.IsCompleted || args.Response.StatusCode is not (200 or 206)) return;
                var url = args.Request.Uri;
                var responseHeaders = args.Response.Headers;
                var type = responseHeaders.Contains("Content-Type")
                    ? responseHeaders.GetHeader("Content-Type").Split(';', 2)[0].Trim().ToLowerInvariant() : null;
                if (HlsManifest.RecognizeResponse(url, type) is not { } contentType) return;
                var requestHeaders = args.Request.Headers;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "Referer", "Origin", "User-Agent" })
                {
                    if (requestHeaders.Contains(name)) headers[name] = requestHeaders.GetHeader(name);
                }
                found.TrySetResult(new SniffedMedia(url, contentType, headers));
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        var previousUserAgent = core.Settings.UserAgent;
        core.WebResourceResponseReceived += OnResponse;
        using var registration = cancellationToken.Register(() => found.TrySetCanceled(cancellationToken));
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var timeoutRegistration = timeout.Token.Register(() => found.TrySetResult(null));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(userAgent)) core.Settings.UserAgent = userAgent;
            core.Navigate(pageUrl);
            while (!found.Task.IsCompleted)
            {
                if (await Task.WhenAny(found.Task, Task.Delay(900, cancellationToken)) == found.Task) break;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var script = """
                        (() => {
                          document.querySelectorAll('video').forEach(v => {
                            v.muted = true;
                            if (v.paused) v.play().catch(() => {});
                          });
                          if (!window.__tvPlaybackStarted) {
                            const button = [...document.querySelectorAll('.dplayer-play-icon,.xgplayer-start,.vjs-big-play-button,button[aria-label]')]
                              .find(e => e.getClientRects().length && (!e.matches('button[aria-label]') ||
                                /^(play|播放|开始播放)$/i.test(e.getAttribute('aria-label').trim())));
                            if (button) { window.__tvPlaybackStarted = true; button.click(); }
                          }
                        })()
                        """;
                    var scriptTask = core.ExecuteScriptAsync(script).AsTask();
                    if (await Task.WhenAny(found.Task, scriptTask) == found.Task) break;
                    await scriptTask;
                }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            return await found.Task;
        }
        finally
        {
            try
            {
                core.WebResourceResponseReceived -= OnResponse;
                core.Stop();
                if (!string.IsNullOrWhiteSpace(userAgent)) core.Settings.UserAgent = previousUserAgent;
                core.Navigate("about:blank");
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    private Task<T> RunOnUi<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await work(cancellationToken)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            completion.TrySetException(new InvalidOperationException("界面线程不可用。"));
        return completion.Task;
    }
}
