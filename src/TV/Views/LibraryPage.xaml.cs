using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using TV.Models;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryPage() => InitializeComponent();
    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        MotionService.Reveal(this);
        if (SegmentList.SelectedIndex != ViewModel.SelectedTab)
            SegmentList.SelectedIndex = Math.Clamp(ViewModel.SelectedTab, 0, 3);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>分段控件选中项同步到 ViewModel.SelectedTab（ListView 的 SelectedIndex 不可绑定）。</summary>
    private void OnSegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SegmentList.SelectedIndex >= 0) ViewModel.SelectedTab = SegmentList.SelectedIndex;
    }

    private void OnHistoryClick(object sender, ItemClickEventArgs e) => ViewModel.OpenHistoryCommand.Execute(e.ClickedItem as HistoryEntry);
    private void OnFavoriteClick(object sender, ItemClickEventArgs e) => ViewModel.OpenFavoriteCommand.Execute(e.ClickedItem as FavoriteEntry);
    private void OnDownloadClick(object sender, ItemClickEventArgs e) => ViewModel.OpenDownloadCommand.Execute(e.ClickedItem as DownloadEntry);
    private void OnDeleteHistoryClick(object sender, RoutedEventArgs e) => ViewModel.DeleteHistoryCommand.Execute((sender as Button)?.Tag as HistoryEntry);
    private void OnRemoveFavoriteClick(object sender, RoutedEventArgs e) => ViewModel.RemoveFavoriteCommand.Execute((sender as Button)?.Tag as FavoriteEntry);
    private void OnRemoveDownloadClick(object sender, RoutedEventArgs e) => ViewModel.RemoveDownloadCommand.Execute((sender as Button)?.Tag as DownloadEntry);
    private void OnBrowseVodClick(object sender, RoutedEventArgs e) => App.Window.Navigate("vod");
    private void OnLocalMediaClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LocalMediaEntry entry }) ViewModel.OpenLocalMediaCommand.Execute(entry);
    }
    private void OnRemoveLocalMediaClick(object sender, RoutedEventArgs e) => ViewModel.RemoveLocalMediaCommand.Execute((sender as Button)?.Tag as LocalMediaEntry);
    private void OnRemoveLocalFolderClick(object sender, RoutedEventArgs e) => ViewModel.RemoveLocalFolderCommand.Execute((sender as Button)?.Tag as string);

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 760;
        RootLayout.Padding = e.NewSize.Width < 560 ? new Thickness(16, 16, 16, 20) : new Thickness(32, 24, 32, 32);
        HeaderSubtitle.Visibility = e.NewSize.Width < 560 ? Visibility.Collapsed : Visibility.Visible;
        HeaderGrid.RowSpacing = narrow ? 12 : 0;
        SearchBox.Width = narrow ? double.NaN : 240;
        SearchBox.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Grid.SetRow(SearchBox, narrow ? 1 : 0);
        Grid.SetColumn(SearchBox, narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, narrow ? 4 : 1);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLocalTab) await ViewModel.RefreshLocalCommand.ExecuteAsync(null);
        else await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void OnRevealDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadEntry entry } || Path.GetDirectoryName(entry.FilePath) is not { } path) return;
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception) { ViewModel.ReportError("文件夹已移动或删除。"); }
    }
}
