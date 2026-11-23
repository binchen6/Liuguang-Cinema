using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class LivePage : Page
{
    private bool _narrow;
    private bool _ready;
    private int _stage;

    public LivePage()
    {
        InitializeComponent();
        MotionService.AttachTactile(LiveReloadButton, 1.04f, 0.94f, -1f);
        MotionService.AttachTactile(LiveStreamButton, 1.04f, 0.94f, -1f);
        MotionService.AttachTactile(PlayChannelButton, 1.02f, 0.96f, -1f);
    }

    private LiveViewModel ViewModel => (LiveViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MotionService.Changed -= OnMotionPreferenceChanged;
        MotionService.Changed += OnMotionPreferenceChanged;
        MotionService.SetBeaconPulsing(LiveBeacon, true);
        ViewModel.Groups.CollectionChanged -= OnGroupsChanged;
        ViewModel.Groups.CollectionChanged += OnGroupsChanged;
        UpdateGroupSection();
        try { await ViewModel.ReloadCommand.ExecuteAsync(null); }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = "加载失败：" + ex.Message;
            ViewModel.HasError = true;
        }
        _ready = true;
        UpdateGroupSection();
        ApplyLayout(ActualWidth);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MotionService.Changed -= OnMotionPreferenceChanged;
        MotionService.SetBeaconPulsing(LiveBeacon, false);
        if (DataContext is LiveViewModel model) model.Groups.CollectionChanged -= OnGroupsChanged;
    }

    private void OnMotionPreferenceChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) MotionService.SetBeaconPulsing(LiveBeacon, true);
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateGroupSection();

    private void UpdateGroupSection() =>
        GroupSection.Visibility = ViewModel.Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    private void ApplyLayout(double width)
    {
        _narrow = width > 0 && width < 940;
        if (_narrow)
        {
            SourcesColumn.Width = new GridLength(1, GridUnitType.Star);
            ChannelsColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
            BodyGrid.ColumnSpacing = 0;
            Grid.SetColumn(LeftPanel, 0);
            Grid.SetColumn(ChannelsPanel, 0);
            Grid.SetColumn(DetailPanel, 0);
            NarrowNav.Visibility = Visibility.Visible;
            ShowStage(_stage, animate: false);
            return;
        }

        SourcesColumn.Width = new GridLength(272);
        ChannelsColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(360);
        BodyGrid.ColumnSpacing = 24;
        Grid.SetColumn(LeftPanel, 0);
        Grid.SetColumn(ChannelsPanel, 1);
        Grid.SetColumn(DetailPanel, 2);
        LeftPanel.Visibility = Visibility.Visible;
        ChannelsPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Visible;
        NarrowNav.Visibility = Visibility.Collapsed;
        _stage = 0;
    }

    private void ShowStage(int stage, bool animate = true)
    {
        var prev = _stage;
        _stage = Math.Clamp(stage, 0, 2);
        if (!_narrow)
        {
            LeftPanel.Visibility = Visibility.Visible;
            ChannelsPanel.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Visible;
            return;
        }

        LeftPanel.Visibility = _stage == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelsPanel.Visibility = _stage == 1 ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _stage == 2 ? Visibility.Visible : Visibility.Collapsed;
        NarrowBackButton.Visibility = _stage > 0 ? Visibility.Visible : Visibility.Collapsed;
        NarrowStageText.Text = _stage switch
        {
            0 => "直播源与分组",
            1 => "频道",
            _ => "频道详情"
        };

        if (animate && _stage != prev)
        {
            var dir = _stage > prev ? 22 : -22;
            FrameworkElement active = _stage switch { 0 => LeftPanel, 1 => ChannelsPanel, _ => DetailPanel };
            MotionService.EnterSection(active, offsetY: 0, offsetX: dir);
        }
    }

    private void OnNarrowBackClick(object sender, RoutedEventArgs e)
    {
        if (_narrow && _stage > 0) ShowStage(_stage - 1);
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !_narrow || _stage != 0 || e.AddedItems.Count == 0) return;
        ShowStage(1);
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (_narrow && _stage == 1) ShowStage(2);
        else if (!_narrow) MotionService.EnterSection(ChannelDetailContent, offsetY: 8);
    }

    private void OnChannelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item) ViewModel.PlayChannelCommand.Execute(item.Primary.Channel);
        else ViewModel.PlayChannelCommand.Execute(ViewModel.SelectedChannel);
    }

    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LiveChannelLine line) ViewModel.PlayChannelCommand.Execute(line.Channel);
    }
}
