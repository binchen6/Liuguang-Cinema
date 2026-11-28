using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using TV.Services;
using TV.Services.Http;
using TV.Services.Playback;
using TV.Services.Persistence;
using Windows.Storage.Streams;
using Windows.System;

namespace TV.Controls;

/// <summary>
/// Visible in-app verification dialog allowing the user to complete a site's image CAPTCHA
/// or interactive browser security check and persisting the resulting same-origin session cookies.
/// </summary>
public sealed class SiteVerificationDialog
{
    private readonly AppServices _services;
    private readonly SiteVerificationRequiredException _challenge;
    private readonly XamlRoot _xamlRoot;

    private string _activeCookies = string.Empty;
    private WebView2? _webView;
    private ContentDialog? _dialog;
    private TextBlock? _statusText;
    private bool _completing;

    public SiteVerificationDialog(AppServices services, SiteVerificationRequiredException challenge, XamlRoot xamlRoot)
    {
        _services = services;
        _challenge = challenge;
        _xamlRoot = xamlRoot;
    }

    public bool Verified { get; private set; }

    public async Task<bool> ShowAsync()
    {
        if (_challenge.Kind == SiteVerificationKind.ImageCaptcha && _challenge.ImageChallenge is { } imgChallenge)
            return await ShowImageCaptchaAsync(imgChallenge);
        return await ShowBrowserChallengeAsync();
    }

    private async Task<bool> ShowImageCaptchaAsync(ImageCaptchaChallenge challenge)
    {
        _activeCookies = challenge.SeedCookies;

        var hint = new TextBlock
        {
            Text = $"{_challenge.SiteName} 需要完成图形验证码后继续访问，验证通过后将自动记住本次会话。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
        };
        var captchaImage = new Image
        {
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Left,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };
        var imageBorder = new Border
        {
            MinWidth = 128,
            Height = 52,
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            Child = captchaImage
        };
        var refreshBtn = new Button { Content = "换一张", VerticalAlignment = VerticalAlignment.Center };
        var imageRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { imageBorder, refreshBtn }
        };
        var codeBox = new TextBox
        {
            Header = "验证码",
            PlaceholderText = "请输入图中字符",
            MaxLength = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var status = new TextBlock
        {
            Text = "正在加载验证码图片…",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        _statusText = status;

        var panel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 360,
            Children = { hint, imageRow, codeBox, status }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = $"{_challenge.SiteName} · 安全验证",
            Content = panel,
            PrimaryButtonText = "提交验证",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        _dialog = dialog;

        async Task LoadCaptchaImageAsync()
        {
            status.Text = "正在加载验证码图片…";
            refreshBtn.IsEnabled = false;
            try
            {
                var sep = challenge.CaptchaImageUri.Query.Length > 0 ? "&" : "?";
                var imgUrl = $"{challenge.CaptchaImageUri.AbsoluteUri}{sep}_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = challenge.UserAgent,
                    ["Referer"] = challenge.RefererUri.AbsoluteUri,
                    ["Accept"] = "image/avif,image/webp,image/apng,image/*,*/*;q=0.8"
                };
                if (!string.IsNullOrWhiteSpace(_activeCookies)) headers["Cookie"] = _activeCookies;
                var exchange = await _services.Http.GetExchangeAsync(imgUrl, headers, maxBytes: 1024 * 1024);
                if (exchange.SetCookies.Count > 0)
                    _activeCookies = SiteSessionStore.MergeCookies(_activeCookies, exchange.SetCookies);
                if (exchange.Payload.Content.Length < 16)
                {
                    status.Text = "验证码图片加载失败，请点击“换一张”重试。";
                    return;
                }
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(exchange.Payload.Content.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                captchaImage.Source = bitmap;
                status.Text = "请输入图片中的验证码后点击“提交验证”。";
            }
            catch (Exception ex)
            {
                status.Text = "验证码图片加载失败：" + ex.Message;
            }
            finally
            {
                refreshBtn.IsEnabled = true;
            }
        }

        async Task<bool> TrySubmitAsync()
        {
            var code = codeBox.Text.Trim();
            if (code.Length == 0)
            {
                status.Text = "请先输入验证码。";
                return false;
            }
            status.Text = "正在验证…";
            try
            {
                var fields = new List<KeyValuePair<string, string>>(challenge.HiddenFields)
                {
                    new(challenge.CodeFieldName, code)
                };
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = challenge.UserAgent,
                    ["Referer"] = challenge.RefererUri.AbsoluteUri,
                    ["Origin"] = challenge.RefererUri.GetLeftPart(UriPartial.Authority),
                    ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
                };
                if (!string.IsNullOrWhiteSpace(_activeCookies)) headers["Cookie"] = _activeCookies;

                var exchange = await _services.Http.PostFormAsync(challenge.FormActionUri.AbsoluteUri, fields, headers);
                var merged = SiteSessionStore.MergeCookies(_activeCookies, exchange.SetCookies);
                if (!string.IsNullOrWhiteSpace(merged)) _activeCookies = merged;

                var html = SourceHttpClient.DecodeText(exchange.Payload.Content);
                var stillCaptcha = html.Contains("vdimgck", StringComparison.OrdinalIgnoreCase) ||
                    html.Contains("系统安全验证", StringComparison.Ordinal) ||
                    html.Contains("验证码不正确", StringComparison.Ordinal) ||
                    html.Contains("验证码错误", StringComparison.Ordinal);

                if (stillCaptcha || (int)exchange.StatusCode >= 400)
                {
                    status.Text = "验证码不正确或已过期，请重新输入。";
                    codeBox.Text = string.Empty;
                    await LoadCaptchaImageAsync();
                    return false;
                }

                if (string.IsNullOrWhiteSpace(_activeCookies))
                {
                    status.Text = "站点未返回会话凭据，请重试。";
                    return false;
                }

                await _services.SiteSessions.SaveAsync(challenge.RefererUri, _activeCookies, challenge.UserAgent);
                Verified = true;
                return true;
            }
            catch (Exception ex)
            {
                status.Text = "验证提交失败：" + ex.Message;
                return false;
            }
        }

        refreshBtn.Click += async (_, _) => await LoadCaptchaImageAsync();
        codeBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                _ = SubmitAndCloseAsync();
            }
        };

        async Task SubmitAndCloseAsync()
        {
            if (_completing) return;
            _completing = true;
            try
            {
                if (await TrySubmitAsync()) dialog.Hide();
            }
            finally { _completing = false; }
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (!await TrySubmitAsync()) args.Cancel = true;
            }
            finally { deferral.Complete(); }
        };

        dialog.Opened += async (_, _) =>
        {
            codeBox.Focus(FocusState.Programmatic);
            await LoadCaptchaImageAsync();
        };

        await dialog.ShowAsync();
        return Verified;
    }

    private async Task<bool> ShowBrowserChallengeAsync()
    {
        var hint = new TextBlock
        {
            Text = $"请在下方页面中完成 {_challenge.SiteName} 的安全验证；验证通过后将自动保存会话并恢复访问。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 540
        };
        var status = new TextBlock
        {
            Text = "正在加载验证页面…",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        _statusText = status;

        var webView = new WebView2
        {
            Width = 540,
            Height = 420
        };
        _webView = webView;

        var panel = new StackPanel
        {
            Spacing = 10,
            Children = { hint, webView, status }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = $"{_challenge.SiteName} · 安全验证",
            Content = panel,
            PrimaryButtonText = "我已完成验证",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        _dialog = dialog;

        async Task<bool> CaptureCookiesAndSaveAsync()
        {
            if (_webView?.CoreWebView2 is not { } core) return false;
            var origin = _challenge.TargetUri.GetLeftPart(UriPartial.Authority);
            var cookies = await core.CookieManager.GetCookiesAsync(origin);
            if (cookies.Count == 0)
                cookies = await core.CookieManager.GetCookiesAsync(_challenge.TargetUri.AbsoluteUri);
            if (cookies.Count == 0)
            {
                status.Text = "尚未检测到验证通过的会话 Cookie，请等待页面加载完成或继续验证。";
                return false;
            }
            var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            var ua = core.Settings.UserAgent;
            await _services.SiteSessions.SaveAsync(_challenge.TargetUri, header, ua);
            Verified = true;
            return true;
        }

        dialog.Opened += async (_, _) =>
        {
            try
            {
                var env = await WebViewRuntime.CreateEnvironmentAsync();
                await webView.EnsureCoreWebView2Async(env);
                var core = webView.CoreWebView2;
                core.Settings.AreDefaultScriptDialogsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationCompleted += async (sender, args) =>
                {
                    try
                    {
                        if (!args.IsSuccess || _completing || Verified) return;
                        var title = sender.DocumentTitle ?? string.Empty;
                        status.Text = string.IsNullOrWhiteSpace(title) ? "页面已加载，请完成安全验证。" : $"当前页面：{title}";
                        var isChallengeTitle =
                            title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("请稍候", StringComparison.Ordinal) ||
                            title.Contains("正在验证", StringComparison.Ordinal) ||
                            title.Contains("安全验证", StringComparison.Ordinal);
                        if (!isChallengeTitle && Uri.TryCreate(sender.Source, UriKind.Absolute, out var current) &&
                            MediaRequestHeaders.SameOrigin(current, _challenge.TargetUri))
                        {
                            var cookies = await sender.CookieManager.GetCookiesAsync(_challenge.TargetUri.GetLeftPart(UriPartial.Authority));
                            if (cookies.Count > 0)
                            {
                                _completing = true;
                                try
                                {
                                    if (await CaptureCookiesAndSaveAsync())
                                    {
                                        status.Text = "验证已通过，正在返回…";
                                        dialog.Hide();
                                    }
                                }
                                finally { _completing = false; }
                            }
                        }
                    }
                    catch (Exception ex) { Diagnostics.Log($"WebView 验证检测异常: {ex.Message}"); }
                };
                core.Navigate(_challenge.TargetUri.AbsoluteUri);
            }
            catch (Exception ex)
            {
                status.Text = "无法打开内置验证视图：" + ex.Message;
            }
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (!await CaptureCookiesAndSaveAsync()) args.Cancel = true;
            }
            catch (Exception ex)
            {
                status.Text = "读取验证会话失败：" + ex.Message;
                args.Cancel = true;
            }
            finally { deferral.Complete(); }
        };

        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            try { _webView?.Close(); } catch { }
            _webView = null;
        }
        return Verified;
    }
}
