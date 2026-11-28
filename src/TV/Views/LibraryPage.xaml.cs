using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TV.Controls;
using TV.Models;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class LibraryPage : Page
{
    private readonly NavigationRailController _segmentNav;
    private readonly CardHoverTracker _watchCards = new("WatchArt");

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
        UpdateHistoryGridLayout();
        await ViewModel.LoadCommand.ExecuteAsync(null);
        UpdateHistoryGridLayout();
    }

    private void OnHistoryGridLoaded(object sender, RoutedEventArgs e) => UpdateHistoryGridLayout();

    private void OnHistoryViewportChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryGridLayout(e.NewSize.Width);

    private void UpdateHistoryGridLayout(double explicitWidth = 0)
    {
        if (HistoryGrid?.ItemsPanelRoot is not ItemsWrapGrid wrap) return;
        var viewportWidth = explicitWidth > 0 ? explicitWidth : HistoryGrid.ActualWidth;
        if (viewportWidth <= 0) return;

        // Subtract right scrollbar clearance + minor rounding margin so items never wrap early and leave zero right-edge gap
        const double cellGap = 16;
        const double minCardWidth = 264;
        const double idealCardWidth = 318;
        var usableWidth = Math.Max(0, viewportWidth - 14);
        if (usableWidth < 200) return;

        var columns = Math.Clamp((int)Math.Round(usableWidth / idealCardWidth), 1, 6);
        while (columns > 1 && (usableWidth / columns) - cellGap < minCardWidth)
            columns--;

        var cellWidth = Math.Floor(usableWidth / columns);
        var cardWidth = Math.Max(180, cellWidth - cellGap);
        var aspect = columns == 1 ? 0.45 : (columns == 2 ? 0.545 : 0.568);
        var cardHeight = Math.Clamp(Math.Round(cardWidth * aspect), 164, 246);
        var cellHeight = cardHeight + cellGap;

        if (Math.Abs(wrap.ItemWidth - cellWidth) > 0.5) wrap.ItemWidth = cellWidth;
        if (Math.Abs(wrap.ItemHeight - cellHeight) > 0.5) wrap.ItemHeight = cellHeight;
    }

    private void OnSegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previous = ViewModel.SelectedTab;
        if (SegmentList.SelectedIndex >= 0 && ViewModel.SelectedTab != SegmentList.SelectedIndex)
        {
            ViewModel.SelectedTab = SegmentList.SelectedIndex;
            var dir = SegmentList.SelectedIndex > previous ? 18 : -18;
            MotionService.EnterSection(ContentHost, offsetY: 0, offsetX: dir);
            if (ViewModel.IsHistoryTab)
                DispatcherQueue.TryEnqueue(() => UpdateHistoryGridLayout());
        }
    }

    private void OnWatchContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.ContentTemplateRoot is FrameworkElement card)
            _watchCards.Reset(card);
    }

    private void OnWatchCardIn(object sender, PointerRoutedEventArgs e) => _watchCards.SetHovered(sender, true);
    private void OnWatchCardOut(object sender, PointerRoutedEventArgs e) => _watchCards.SetHovered(sender, false);
    private void OnWatchCardPressed(object sender, PointerRoutedEventArgs e) => CardHoverTracker.SetPressed(sender, true);
    private void OnWatchCardReleased(object sender, PointerRoutedEventArgs e) => CardHoverTracker.SetPressed(sender, false);

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
        RootLayout.Padding = e.NewSize.Width < 560 ? new Thickness(16, 8, 16, 20) : new Thickness(36, 10, 36, 28);
        HeaderSubtitle.Visibility = e.NewSize.Width < 560 ? Visibility.Collapsed : Visibility.Visible;
        HeaderGrid.RowSpacing = narrow ? 12 : 0;
        SearchBox.Width = narrow ? double.NaN : 240;
        SearchBox.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Grid.SetRow(SearchBox, narrow ? 1 : 0);
        Grid.SetColumn(SearchBox, narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, narrow ? 4 : 1);
        DispatcherQueue.TryEnqueue(() => UpdateHistoryGridLayout());
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
