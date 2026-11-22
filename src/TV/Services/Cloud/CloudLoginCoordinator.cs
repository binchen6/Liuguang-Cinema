using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using TV.Models;
using TV.Services.Imaging;

namespace TV.Services.Cloud;

public sealed partial class CloudLoginCoordinator : ObservableObject, IDisposable
{
    private readonly CloudDriveService _cloud;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _lifetime;
    private CloudLoginSession? _session;

    public CloudLoginCoordinator(CloudDriveService cloud, DispatcherQueue dispatcher)
    {
        _cloud = cloud;
        _dispatcher = dispatcher;
    }

    [ObservableProperty] private CloudProviderKind _provider;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isWebLogin;
    [ObservableProperty] private BitmapImage? _qrImage;
    [ObservableProperty] private string _webLoginUrl = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    public event EventHandler<CloudProviderKind>? Succeeded;

    public async Task StartAsync(CloudProviderKind provider)
    {
        CancelSession();
        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;
        Provider = provider;
        IsActive = true;
        IsBusy = true;
        IsWebLogin = false;
        QrImage = null;
        WebLoginUrl = string.Empty;
        Status = "正在生成二维码…";
        try
        {
            var session = await _cloud.Login.StartAsync(provider, token);
            token.ThrowIfCancellationRequested();
            _session = session;
            if (!session.IsWebLogin)
            {
                QrImage = await BitmapImages.FromBytesAsync(session.QrPng, token);
                Status = session.Status;
                _ = PollAsync(session, token);
            }
            IsBusy = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Status = ex.Message;
                IsBusy = false;
            }
        }
    }

    private async Task PollAsync(CloudLoginSession session, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && session.Status != "登录成功" && session.Status != "二维码已过期")
            {
                await Task.Delay(1800, token);
                await _cloud.Login.PollAsync(session.Id, token);
                token.ThrowIfCancellationRequested();
                Status = session.Status;
                if (session.Status == "登录成功")
                {
                    IsBusy = false;
                    NotifySucceeded(session.Provider);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Status = ex.Message;
                IsBusy = false;
            }
        }
        finally
        {
            if (session.Status == "登录成功" || session.Status == "二维码已过期") _cloud.Login.Cancel(session.Id);
        }
    }

    public async Task<bool> CompleteWebLoginAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (Provider != CloudProviderKind.Baidu || !IsActive) return false;
        IsBusy = true;
        Status = "正在验证百度登录…";
        var lifetime = _lifetime;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime?.Token ?? CancellationToken.None);
        var token = linked.Token;
        try
        {
            if (!await _cloud.Login.VerifyBaiduCookieAsync(credential, token))
            {
                Status = "尚未检测到有效登录，请扫码确认后重试";
                return false;
            }
            await _cloud.SaveCredentialAsync(CloudProviderKind.Baidu, credential, token);
            Status = "登录成功";
            NotifySucceeded(Provider);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Status = "登录验证失败：" + ex.Message;
            return false;
        }
        finally { if (ReferenceEquals(_lifetime, lifetime)) IsBusy = false; }
    }

    private void NotifySucceeded(CloudProviderKind provider)
    {
        if (_dispatcher.HasThreadAccess) Succeeded?.Invoke(this, provider);
        else _dispatcher.TryEnqueue(() => Succeeded?.Invoke(this, provider));
    }

    public void Close()
    {
        CancelSession();
        IsActive = false;
        IsBusy = false;
        QrImage = null;
        WebLoginUrl = string.Empty;
    }

    private void CancelSession()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is not null)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
        if (_session is not null) _cloud.Login.Cancel(_session.Id);
        _session = null;
    }

    public void Dispose() => CancelSession();
}
