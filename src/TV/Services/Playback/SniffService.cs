using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using TV.Services.Http;

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
    private Canvas? _host;
    private WebView2? _webView;
    private Task<bool>? _ready;
    private CachedMedia? _recent;
    internal string? RuntimeIssue { get; private set; }
    private sealed record CachedMedia(string Page, string? UserAgent, Func<Uri, bool>? Filter,
        bool AudioOnly, SniffedMedia Media, DateTimeOffset ExpiresAt);

    public SniffService(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Attach(Canvas host) => _host = host;

    public void Detach()
    {
        var host = _host;
        _host = null;
        _ready = null;
        _recent = null;
        RuntimeIssue = null;
        if (_webView is not { } webView) return;
        _webView = null;
        webView.Close();
        host?.Children.Remove(webView);
    }

    private static WebView2 CreateView(Canvas host)
    {
        var view = new WebView2
        {
            Width = 640,
            Height = 360,
            Opacity = 0.01,
            IsHitTestVisible = false,
            DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)
        };
        // WebView2 uses its own HWND, so a parent's opacity does not reliably hide it.
        Canvas.SetLeft(view, -1024);
        host.Children.Add(view);
        return view;
    }

    internal async Task<SniffedMedia?> SniffAsync(string pageUrl, string? userAgent = null, CancellationToken cancellationToken = default,
        Func<Uri, bool>? allowMedia = null, bool audioOnly = false)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !MediaRequestHeaders.IsHttpAddress(page)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recent is { } last && last.Page == pageUrl && last.UserAgent == userAgent && last.Filter == allowMedia &&
                last.AudioOnly == audioOnly && last.ExpiresAt > DateTimeOffset.UtcNow)
                return last.Media;
            if (!await EnsureReadyAsync().WaitAsync(cancellationToken).ConfigureAwait(false)) return null;
            var result = await RunOnUi(token => SniffCoreAsync(page, userAgent, allowMedia, audioOnly, token), cancellationToken).ConfigureAwait(false);
            if (result is not null) _recent = new CachedMedia(pageUrl, userAgent, allowMedia, audioOnly, result, DateTimeOffset.UtcNow.AddSeconds(45));
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

    internal async Task<string?> ReadPageHtmlAsync(Uri page, string readySelector, CancellationToken cancellationToken)
    {
        if (!MediaRequestHeaders.IsHttpAddress(page)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (!await EnsureReadyAsync().WaitAsync(deadline.Token).ConfigureAwait(false)) return null;
                return await RunOnUi(token => ReadRenderedHtmlAsync(page, readySelector, token), deadline.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<string?> ReadRenderedHtmlAsync(Uri page, string selector, CancellationToken token)
    {
        if (_webView?.CoreWebView2 is not { } core) return null;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(CoreWebView2 _, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) || !MediaRequestHeaders.SameOrigin(page, destination))
                args.Cancel = true;
            else
                navigationId = args.NavigationId;
        }
        void Loaded(CoreWebView2 _, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (navigationId == args.NavigationId) loaded.TrySetResult();
        }
        core.NavigationStarting += Starting;
        core.DOMContentLoaded += Loaded;
        try
        {
            token.ThrowIfCancellationRequested();
            core.Navigate(page.AbsoluteUri);
            await loaded.Task.WaitAsync(token);
            var script = $$"""
                (() => {
                    if (!document.querySelector({{JsonSerializer.Serialize(selector)}})) return null;
                    const page = document.documentElement.cloneNode(true);
                    page.querySelectorAll('script,style,noscript').forEach(element => element.remove());
                    const html = page.outerHTML;
                    return html.length <= 2097152 ? html : null;
                })()
                """;
            while (true)
            {
                var result = await core.ExecuteScriptAsync(script).AsTask().WaitAsync(token);
                if (result != "null") return JsonSerializer.Deserialize<string>(result);
                await Task.Delay(180, token);
            }
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.DOMContentLoaded -= Loaded;
            try { core.Stop(); core.Navigate("about:blank"); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    private Task<bool> EnsureReadyAsync()
    {
        if (_ready is { IsCompletedSuccessfully: true, Result: false }) _ready = null;
        return _ready ??= RunOnUi<bool>(async _ =>
        {
            if (_host is not { } host) return false;
            var webView = _webView ??= CreateView(host);
            try
            {
                var environment = await WebViewRuntime.CreateEnvironmentAsync();
                if (!ReferenceEquals(_webView, webView)) return false;
                await webView.EnsureCoreWebView2Async(environment);
                if (!ReferenceEquals(_webView, webView)) return false;
                var core = webView.CoreWebView2;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.IsMuted = true;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
                RuntimeIssue = null;
                return true;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_webView, webView))
                {
                    RuntimeIssue = ex is WebViewRuntimeUnavailableException ? ex.Message : null;
                    Diagnostics.Log($"Sniffer init failed: {ex.GetType().Name}");
                    _webView = null;
                    webView.Close();
                    host.Children.Remove(webView);
                }
                return false;
            }
        }, CancellationToken.None);
    }

    private async Task<SniffedMedia?> SniffCoreAsync(Uri page, string? userAgent, Func<Uri, bool>? allowMedia,
        bool audioOnly, CancellationToken cancellationToken)
    {
        if (_webView?.CoreWebView2 is not { } core) return null;
        var found = new TaskCompletionSource<SniffedMedia?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnResponse(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            try
            {
                if (found.Task.IsCompleted || args.Response.StatusCode is not (200 or 206)) return;
                var url = args.Request.Uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || !MediaRequestHeaders.IsHttpAddress(address) ||
                    allowMedia is not null && !allowMedia(address)) return;
                var responseHeaders = args.Response.Headers;
                var type = responseHeaders.Contains("Content-Type")
                    ? responseHeaders.GetHeader("Content-Type").Split(';', 2)[0].Trim().ToLowerInvariant() : null;
                if (HlsManifest.RecognizeResponse(url, type) is not { } contentType) return;
                if (!audioOnly && contentType.StartsWith("audio/", StringComparison.Ordinal)) return;
                if (audioOnly && !contentType.StartsWith("audio/", StringComparison.Ordinal) &&
                    !HlsManifest.IsPlaylist(url, contentType)) return;
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

        void OnNavigation(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) || !MediaRequestHeaders.IsHttpAddress(destination) ||
                audioOnly && !MediaRequestHeaders.SameOrigin(page, destination))
                args.Cancel = true;
        }

        var previousUserAgent = core.Settings.UserAgent;
        core.WebResourceResponseReceived += OnResponse;
        core.NavigationStarting += OnNavigation;
        using var registration = cancellationToken.Register(() => found.TrySetCanceled(cancellationToken));
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var timeoutRegistration = timeout.Token.Register(() => found.TrySetResult(null));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(userAgent)) core.Settings.UserAgent = userAgent;
            core.Navigate(page.AbsoluteUri);
            while (!found.Task.IsCompleted)
            {
                if (await Task.WhenAny(found.Task, Task.Delay(900, cancellationToken)) == found.Task) break;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var script = audioOnly ? """
                        (() => {
                          const a = document.querySelector('audio');
                          if (!a) return;
                          a.muted = true;
                          if (a.currentSrc || a.getAttribute('src')) {
                            if (a.paused) a.play().catch(() => {});
                          } else if (window.jQuery && !window.__tvPlaybackStarted) {
                            const toggle = document.querySelector('#toggle.icon-playbar-play');
                            if (toggle && toggle.getClientRects().length && window.jQuery._data &&
                                window.jQuery._data(toggle, 'events')?.click?.length) {
                              window.__tvPlaybackStarted = true;
                              toggle.click();
                            }
                          }
                        })()
                        """ : """
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
                core.NavigationStarting -= OnNavigation;
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
