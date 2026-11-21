using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TV.Services.Imaging;
using TV.Services.Vod.Native;

namespace TV.Controls;

public sealed partial class BilibiliLoginDialog : ContentDialog, IDisposable
{
    private readonly BilibiliAccountService _account;
    private CancellationTokenSource? _operation;
    private string? _accountName;

    internal BilibiliLoginDialog(BilibiliAccountService account, XamlRoot root)
    {
        InitializeComponent();
        _account = account;
        XamlRoot = root;
    }

    internal bool AccountChanged { get; private set; }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        var token = BeginOperation();
        ScanButton.IsEnabled = false;
        AccountStatus.Text = "正在检查登录状态…";
        try
        {
            var name = await _account.GetAccountNameAsync(token);
            if (token.IsCancellationRequested) return;
            SetAccount(name);
            if (name is null) await ShowQrAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
            {
                AccountStatus.Text = "暂时无法检查 B 站账号。";
                ScanStatus.Text = "可以扫码重新登录。";
            }
        }
        finally { if (!token.IsCancellationRequested) ScanButton.IsEnabled = true; }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await ShowQrAsync();

    private async Task ShowQrAsync()
    {
        var token = BeginOperation();
        ScanButton.IsEnabled = false;
        QrImage.Source = null;
        QrFrame.Visibility = Visibility.Visible;
        QrProgress.IsActive = true;
        ScanStatus.Text = "正在向 B 站获取二维码…";
        try
        {
            var login = await _account.CreateQrAsync(token);
            var image = await BitmapImages.FromBytesAsync(login.Image, token);
            token.ThrowIfCancellationRequested();
            QrImage.Source = image;
            QrProgress.IsActive = false;
            ScanButton.Content = "刷新二维码";
            ScanButton.IsEnabled = true;
            ScanStatus.Text = "请用哔哩哔哩 App 扫码。";
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1800, token);
                var result = await _account.PollAsync(login, token);
                token.ThrowIfCancellationRequested();
                if (result.State == BilibiliLoginState.WaitingForScan) continue;
                if (result.State == BilibiliLoginState.WaitingForConfirmation)
                {
                    ScanStatus.Text = "已扫码，请在手机确认。";
                    continue;
                }
                if (result.State == BilibiliLoginState.SignedIn)
                {
                    AccountChanged = true;
                    SetAccount(result.AccountName);
                    return;
                }
                QrImage.Source = null;
                QrFrame.Visibility = Visibility.Collapsed;
                ScanStatus.Text = "二维码已过期，请刷新后扫码。";
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
            {
                QrImage.Source = null;
                QrFrame.Visibility = Visibility.Collapsed;
                ScanStatus.Text = "B 站扫码登录未完成，请刷新二维码重试。";
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                ScanButton.IsEnabled = true;
                QrProgress.IsActive = false;
            }
        }
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var token = BeginOperation();
        ScanButton.IsEnabled = SignOutButton.IsEnabled = false;
        try
        {
            await _account.SignOutAsync(token);
            token.ThrowIfCancellationRequested();
            AccountChanged = true;
            SetAccount(null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested) ScanStatus.Text = "退出登录失败，请重试。"; }
        finally { if (!token.IsCancellationRequested) ScanButton.IsEnabled = SignOutButton.IsEnabled = true; }
    }

    private void SetAccount(string? name)
    {
        _accountName = name;
        AccountStatus.Text = _accountName is null ? "未登录 B 站" : $"已登录 B 站 · {_accountName}";
        ScanButton.Content = _accountName is null ? "扫码登录" : "切换账号";
        SignOutButton.Visibility = _accountName is null ? Visibility.Collapsed : Visibility.Visible;
        QrFrame.Visibility = Visibility.Collapsed;
        QrImage.Source = null;
        ScanStatus.Text = "可用画质由视频和账号权限决定。";
    }

    private CancellationToken BeginOperation()
    {
        CancelOperation();
        _operation = new CancellationTokenSource();
        return _operation.Token;
    }

    private void CancelOperation()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        CancelOperation();
        QrImage.Source = null;
    }

    public void Dispose() => CancelOperation();
}
