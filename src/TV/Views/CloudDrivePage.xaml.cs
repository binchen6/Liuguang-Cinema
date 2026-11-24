using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using TV.Controls;
using TV.Models;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class CloudDrivePage : Page
{
    private ContentDialog? _loginDialog;

    public CloudDrivePage()
    {
        InitializeComponent();
        MotionService.AttachTactile(CloudRefreshButton, 1.04f, 0.94f, -1f);
    }
    private CloudDriveViewModel ViewModel => (CloudDriveViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.InitializeAsync(); }
        catch (Exception ex) { ViewModel.StatusMessage = ex.Message; }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_loginDialog is not null)
        {
            App.Services.CloudLogin.Succeeded -= OnCloudLoginSucceeded;
            App.Services.CloudLogin.Close();
            _loginDialog.Hide();
            _loginDialog = null;
        }
    }

    private void OnEntryClick(object sender, ItemClickEventArgs e) => ViewModel.OpenEntryCommand.Execute(e.ClickedItem as CloudDriveItem);

    private void OnAccountContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue && e.Item is CloudAccountInfo account)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e.ItemContainer, account.Name);
    }

    private void OnSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        ViewModel.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        CloudRoot.Padding = width < 760
            ? (Thickness)Application.Current.Resources["PagePaddingNarrow"]
            : width < 1080
                ? (Thickness)Application.Current.Resources["PagePaddingStandard"]
                : (Thickness)Application.Current.Resources["PagePaddingWide"];
        SearchBox.Width = width < 760 ? 164 : width < 1000 ? 196 : 232;
    }

    private void OnCloudLoginClick(object sender, RoutedEventArgs e) => _ = ShowCloudLoginAsync();

    private async Task ShowCloudLoginAsync()
    {
        if (_loginDialog is not null || ViewModel.RequiredLoginProvider is not { } provider || XamlRoot is null) return;
        var coordinator = App.Services.CloudLogin;
        var panel = new CloudLoginPanel { DataContext = coordinator };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"{CloudAccountInfo.DisplayName(provider)}登录", Content = panel,
            CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close
        };
        _loginDialog = dialog;
        coordinator.Succeeded += OnCloudLoginSucceeded;
        panel.CloseRequested += (_, _) => dialog.Hide();
        try
        {
            var showing = dialog.ShowAsync();
            await coordinator.StartAsync(provider);
            await showing;
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = "登录面板无法打开：" + ex.Message;
        }
        finally
        {
            coordinator.Succeeded -= OnCloudLoginSucceeded;
            coordinator.Close();
            _loginDialog = null;
        }
    }

    private async void OnCloudLoginSucceeded(object? sender, CloudProviderKind provider)
    {
        if (ViewModel.RequiredLoginProvider != provider) return;
        _loginDialog?.Hide();
        ViewModel.RequiredLoginProvider = null;
        await ViewModel.RefreshAccountsAsync();
        await ViewModel.LoadFolderCommand.ExecuteAsync(null);
    }
}
