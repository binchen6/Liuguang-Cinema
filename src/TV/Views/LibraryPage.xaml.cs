using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TV.Controls;
using TV.Models;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class LibraryPage : Page
{
    private async void OnOpenMagnetClick(object sender, RoutedEventArgs e) => await App.Window.OpenMagnetDialogAsync();

    private readonly NavigationRailController _segmentNav;

    public LibraryPage()
    {
        InitializeComponent();
        _segmentNav = NavigationMotion.AttachHorizontalList(
            SegmentList,
            SegmentTrackHost,
            SegmentGlidePill,
            HorizontalIndicatorMode.PillMatchWidth,
            SegmentScroller);
        MotionService.AttachTactile(LocalMediaButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(LibraryRefreshButton, 1.04f, 0.94f, -1f);
    }

    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MotionService.StaggerEntrance(offsetY: 12, stepMs: 35, HeaderGrid, SegmentTrack, ContentHost);
        if (SegmentList.SelectedIndex != ViewModel.SelectedTab)
            SegmentList.SelectedIndex = Math.Clamp(ViewModel.SelectedTab, 0, 3);
        _segmentNav.Sync(animate: false);
        UpdateVisibleCoverLayout();
        await ViewModel.LoadCommand.ExecuteAsync(null);
        UpdateVisibleCoverLayout();
    }

    private void OnCoverGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is GridView grid) UpdateCoverLayout(grid);
    }

    private void OnCoverGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is GridView grid) UpdateCoverLayout(grid);
    }

    private void UpdateVisibleCoverLayout()
    {
        var grid = ViewModel.SelectedTab switch
        {
            1 => FavoritesGrid,
            2 => DownloadsGrid,
            3 => LocalGrid,
            _ => HistoryGrid
        };
        UpdateCoverLayout(grid);
    }

    private static void UpdateCoverLayout(GridView? grid)
    {
        if (grid?.ItemsPanelRoot is not ItemsWrapGrid panel || grid.ActualWidth <= 20) return;
        var available = grid.ActualWidth - 12;
        var columns = Math.Clamp((int)((available + 12) / 177), 1, 8);
        var cellWidth = Math.Floor(available / columns);
        var posterWidth = Math.Max(90, cellWidth - 12);
        var cellHeight = Math.Round(posterWidth * 1.5) + 80;
        if (Math.Abs(panel.ItemWidth - cellWidth) > 0.5) panel.ItemWidth = cellWidth;
        if (Math.Abs(panel.ItemHeight - cellHeight) > 0.5) panel.ItemHeight = cellHeight;
    }

    private void OnSegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previous = ViewModel.SelectedTab;
        if (SegmentList.SelectedIndex < 0 || previous == SegmentList.SelectedIndex) return;
        ViewModel.SelectedTab = SegmentList.SelectedIndex;
        SetHistoryEditMode(false);
        MotionService.EnterSection(ContentHost, offsetY: 0, offsetX: SegmentList.SelectedIndex > previous ? 18 : -18);
        DispatcherQueue.TryEnqueue(UpdateVisibleCoverLayout);
    }

    private void OnManageHistoryClick(object sender, RoutedEventArgs e) =>
        SetHistoryEditMode(HistoryGrid.SelectionMode == ListViewSelectionMode.None);

    private void SetHistoryEditMode(bool enabled)
    {
        if (HistoryGrid is null) return;
        HistoryGrid.IsItemClickEnabled = !enabled;
        HistoryGrid.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        ManageHistoryButton.Content = enabled ? "完成" : "管理观看记录";
        SelectVisibleHistoryButton.Visibility = DeleteSelectedHistoryButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistorySelection();
    }

    private void OnSelectVisibleHistoryClick(object sender, RoutedEventArgs e) => HistoryGrid.SelectAll();
    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHistorySelection();

    private void UpdateHistorySelection()
    {
        var count = HistoryGrid?.SelectedItems.Count ?? 0;
        if (DeleteSelectedHistoryButton is null) return;
        DeleteSelectedHistoryButton.IsEnabled = count > 0;
        DeleteSelectedHistoryButton.Content = count == 0 ? "删除所选" : $"删除所选（{count}）";
        var editing = HistoryGrid?.SelectionMode == ListViewSelectionMode.Multiple;
        for (var index = 0; HistoryGrid is not null && index < HistoryGrid.Items.Count; index++)
            if (HistoryGrid.ContainerFromIndex(index) is GridViewItem item) UpdateHistoryCard(item, editing, item.IsSelected);
    }

    private void OnHistoryCardChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is GridViewItem item)
            UpdateHistoryCard(item, HistoryGrid.SelectionMode == ListViewSelectionMode.Multiple, HistoryGrid.SelectedItems.Contains(args.Item));
    }

    private static void UpdateHistoryCard(GridViewItem item, bool editing, bool selected)
    {
        if (item.ContentTemplateRoot is not FrameworkElement content) return;
        if (content.FindName("HistoryCardMenu") is Button menu) menu.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        if (content.FindName("HistoryCardSelected") is Border badge) badge.Visibility = editing && selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDeleteSelectedHistoryClick(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItems.OfType<HistoryEntry>().ToArray();
        if (selected.Length == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = $"删除 {selected.Length} 条观看记录？",
            Content = "这些记录的播放进度将一并清除。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && await ViewModel.DeleteHistoryEntriesAsync(selected))
            SetHistoryEditMode(false);
    }

    private void OnHistoryClick(object sender, ItemClickEventArgs e) => ViewModel.OpenHistoryCommand.Execute(e.ClickedItem as HistoryEntry);
    private void OnFavoriteClick(object sender, ItemClickEventArgs e) => ViewModel.OpenFavoriteCommand.Execute(e.ClickedItem as FavoriteEntry);
    private void OnDownloadClick(object sender, ItemClickEventArgs e) => ViewModel.OpenDownloadCommand.Execute(e.ClickedItem as DownloadEntry);
    private void OnLocalMediaClick(object sender, ItemClickEventArgs e) => ViewModel.OpenLocalMediaCommand.Execute(e.ClickedItem as LocalMediaEntry);
    private void OnBrowseVodClick(object sender, RoutedEventArgs e) => App.Window.Navigate("vod");

    private static void ShowMenu(Button button, params (string Label, Action Run)[] actions)
    {
        var menu = new MenuFlyout();
        foreach (var (label, run) in actions)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }

    private void OnHistoryMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryEntry entry } button || HistoryGrid.SelectionMode != ListViewSelectionMode.None) return;
        if (entry.Kind == "Vod")
            ShowMenu(button, ("打开节目详情", () => ViewModel.OpenHistoryDetail(entry)), ("删除记录", () => ViewModel.DeleteHistoryCommand.Execute(entry)));
        else ShowMenu(button, ("删除记录", () => ViewModel.DeleteHistoryCommand.Execute(entry)));
    }

    private void OnFavoriteMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FavoriteEntry entry } button)
            ShowMenu(button, ("移除收藏", () => ViewModel.RemoveFavoriteCommand.Execute(entry)));
    }

    private void OnDownloadMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DownloadEntry entry } button)
            ShowMenu(button, ("所在文件夹", () => _ = RevealDownloadAsync(entry)),
                ("移除下载记录", () => ViewModel.RemoveDownloadCommand.Execute(entry)));
    }

    private void OnLocalMediaMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LocalMediaEntry entry } button)
            ShowMenu(button, ("打开媒体详情", () => ViewModel.OpenLocalDetail(entry.Path, entry.Title)),
                ("从本地库移除", () => ViewModel.RemoveLocalMediaCommand.Execute(entry)));
    }

    private void OnRemoveLocalFolderClick(object sender, RoutedEventArgs e) => ViewModel.RemoveLocalFolderCommand.Execute((sender as Button)?.Tag as string);

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 760;
        RootLayout.Padding = e.NewSize.Width < 560 ? new Thickness(16, 8, 16, 20) : new Thickness(36, 10, 36, 28);
        HeaderSubtitle.Visibility = e.NewSize.Width < 560 ? Visibility.Collapsed : Visibility.Visible;
        HeaderGrid.RowSpacing = narrow ? 12 : 0;
        SearchBox.Width = narrow ? double.NaN : 240;
        SearchBox.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Grid.SetRow(SearchBox, narrow ? 1 : 0);
        Grid.SetColumn(SearchBox, narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, narrow ? 4 : 1);
        DispatcherQueue.TryEnqueue(() => UpdateVisibleCoverLayout());
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLocalTab) await ViewModel.RefreshLocalCommand.ExecuteAsync(null);
        else await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async Task RevealDownloadAsync(DownloadEntry entry)
    {
        if (Path.GetDirectoryName(entry.FilePath) is not { } path) return;
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception) { ViewModel.ReportError("文件夹已移动或删除。"); }
    }
}
