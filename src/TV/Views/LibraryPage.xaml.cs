using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TV.Models;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class LibraryPage : Page
{
    private bool _hasAnimatedPill;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, FrameworkElement?> _watchArts = new();

    public LibraryPage()
    {
        InitializeComponent();
        MotionService.AttachTactile(LocalMediaButton, 1.02f, 0.96f, -1f);
        MotionService.AttachTactile(LibraryRefreshButton, 1.04f, 0.94f, -1f);
        SegmentList.Loaded += (_, _) => ScheduleSegmentPillGlide(animate: false);
    }

    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MotionService.StaggerEntrance(offsetY: 12, stepMs: 35, HeaderGrid, SegmentTrack, ContentHost);
        if (SegmentList.SelectedIndex != ViewModel.SelectedTab)
            SegmentList.SelectedIndex = Math.Clamp(ViewModel.SelectedTab, 0, 3);
        ScheduleSegmentPillGlide(animate: false);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnSegmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previous = ViewModel.SelectedTab;
        if (SegmentList.SelectedIndex >= 0 && ViewModel.SelectedTab != SegmentList.SelectedIndex)
        {
            ViewModel.SelectedTab = SegmentList.SelectedIndex;
            var dir = SegmentList.SelectedIndex > previous ? 18 : -18;
            MotionService.EnterSection(ContentHost, offsetY: 0, offsetX: dir);
        }
        ScheduleSegmentPillGlide(animate: _hasAnimatedPill);
        _hasAnimatedPill = true;
    }

    private void OnSegmentListSizeChanged(object sender, SizeChangedEventArgs e) =>
        ScheduleSegmentPillGlide(animate: false);

    private void ScheduleSegmentPillGlide(bool animate, int retry = 0)
    {
        var priority = retry == 0 ? Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal : Microsoft.UI.Dispatching.DispatcherQueuePriority.Low;
        DispatcherQueue.TryEnqueue(priority, () =>
        {
            if (!IsLoaded) return;
            var selected = SegmentList.SelectedItem as FrameworkElement;
            if (selected is null && SegmentList.SelectedIndex >= 0)
                selected = SegmentList.ContainerFromIndex(SegmentList.SelectedIndex) as FrameworkElement;
            if (selected is null || selected.ActualWidth <= 0)
            {
                if (retry < 3) ScheduleSegmentPillGlide(animate, retry + 1);
                return;
            }
            try
            {
                var pt = selected.TransformToVisual(SegmentTrackHost).TransformPoint(new Windows.Foundation.Point(0, 0));
                SegmentGlidePill.Opacity = 1;
                MotionService.GlideHorizontalPill(SegmentGlidePill, pt.X, selected.ActualWidth, animate);
            }
            catch { }
        });
    }

    private FrameworkElement? GetWatchArt(FrameworkElement card)
    {
        if (_watchArts.TryGetValue(card, out var cached) && cached is not null) return cached;
        var found = FindDescendant(card, "WatchArt");
        if (found is not null) _watchArts.AddOrUpdate(card, found);
        return found;
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return fe;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindDescendant(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        return null;
    }

    private void OnWatchContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.ContentTemplateRoot is FrameworkElement card)
            MotionService.ResetCard(card, artworkZoom: GetWatchArt(card));
    }

    private void OnWatchCardIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) MotionService.SetCardHover(fe, null, null, GetWatchArt(fe), true);
    }

    private void OnWatchCardOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) MotionService.SetCardHover(fe, null, null, GetWatchArt(fe), false);
    }

    private void OnWatchCardPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) MotionService.SetCardPressed(fe, true);
    }

    private void OnWatchCardReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) MotionService.SetCardPressed(fe, false);
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
        RootLayout.Padding = e.NewSize.Width < 560 ? new Thickness(16, 8, 16, 20) : new Thickness(36, 10, 36, 28);
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
