using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class DetailPage : Page
{
    private VodViewModel? _observed;

    public DetailPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach(DataContext as VodViewModel);
        if (_observed is null)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (IsLoaded) Attach(DataContext as VodViewModel);
            });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Attach(null);

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var compact = width < 900;
        var stackedHero = width < 680;
        var coverWidth = stackedHero ? 160 : e.NewSize.Height < 560 ? 176 : e.NewSize.Height < 720 ? 208 : 260;
        CoverFrame.Width = coverWidth;
        CoverFrame.Height = coverWidth * 1.5;
        CoverFrame.HorizontalAlignment = HorizontalAlignment.Left;
        CoverColumn.Width = stackedHero ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        InfoColumn.Width = stackedHero ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeroGrid.ColumnSpacing = stackedHero ? 0 : 32;
        HeroGrid.RowSpacing = stackedHero ? 20 : 0;
        Grid.SetColumn(DetailInfo, stackedHero ? 0 : 1);
        Grid.SetRow(DetailInfo, stackedHero ? 1 : 0);

        ActionSecondRow.Height = compact ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(DownloadAction, compact ? 0 : 2);
        Grid.SetRow(DownloadAction, compact ? 1 : 0);
        Grid.SetColumn(CastAction, compact ? 1 : 3);
        Grid.SetRow(CastAction, compact ? 1 : 0);

        var stackedContent = width < 840;
        SourceColumn.Width = stackedContent ? new GridLength(1, GridUnitType.Star) : new GridLength(2, GridUnitType.Star);
        EpisodeColumn.Width = stackedContent ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        ContentColumns.ColumnSpacing = stackedContent ? 0 : 32;
        ContentColumns.RowSpacing = stackedContent ? 28 : 0;
        Grid.SetColumn(EpisodePane, stackedContent ? 0 : 1);
        Grid.SetRow(EpisodePane, stackedContent ? 1 : 0);
    }

    private void Attach(VodViewModel? model)
    {
        if (_observed is not null)
        {
            _observed.Items.CollectionChanged -= OnRelatedChanged;
            _observed.Episodes.CollectionChanged -= OnEpisodesChanged;
            _observed.PropertyChanged -= OnObservedPropertyChanged;
        }
        _observed = model;
        if (_observed is not null)
        {
            _observed.Items.CollectionChanged += OnRelatedChanged;
            _observed.Episodes.CollectionChanged += OnEpisodesChanged;
            _observed.PropertyChanged += OnObservedPropertyChanged;
        }
        UpdateRelated();
        ScrollCurrentEpisode();
    }

    private void OnRelatedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateRelated();

    private void OnEpisodesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ScrollCurrentEpisode();

    private void OnObservedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VodViewModel.CurrentEpisode)) ScrollCurrentEpisode();
    }

    private void ScrollCurrentEpisode()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (IsLoaded && _observed?.CurrentEpisode is { } episode && EpisodeGrid.Items.Contains(episode))
                EpisodeGrid.ScrollIntoView(episode);
        });
    }

    private void UpdateRelated() =>
        RelatedSection.Visibility = _observed is { Items.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    private void OnPlayEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Models.Episode episode } && DataContext is VodViewModel viewModel)
            viewModel.PlayEpisodeCommand.Execute(episode);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VodViewModel viewModel) return;
        var episodes = EpisodeToSave.SelectedItems.Cast<Models.Episode>().ToArray();
        if (episodes.Length == 0 && viewModel.Episodes.FirstOrDefault() is { } first) episodes = [first];
        if (viewModel.SaveEpisodesCommand.CanExecute(episodes)) viewModel.SaveEpisodesCommand.Execute(episodes);
    }

    private void OnSelectAllEpisodesClick(object sender, RoutedEventArgs e)
    {
        if (EpisodeToSave.SelectedItems.Count == EpisodeToSave.Items.Count) EpisodeToSave.SelectedItems.Clear();
        else EpisodeToSave.SelectAll();
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VodViewModel viewModel) return;
        var episode = EpisodeToSave.SelectedItems.Cast<Models.Episode>().FirstOrDefault() ?? viewModel.Episodes.FirstOrDefault();
        if (episode is not null && viewModel.SaveEpisodeCommand.CanExecute(episode)) viewModel.SaveEpisodeCommand.Execute(episode);
    }

    private void OnPlayFirstClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is VodViewModel viewModel && (viewModel.CurrentEpisode ?? viewModel.Episodes.FirstOrDefault()) is { } episode)
            viewModel.PlayEpisodeCommand.Execute(episode);
    }

    private void OnRelatedClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: VodCardViewModel card } || DataContext is not VodViewModel viewModel) return;
        viewModel.SelectedCard = card;
        DetailScroller.ChangeView(null, 0, null, true);
        MotionService.EnterSection(HeroBanner, offsetY: 12);
    }

    private void OnRelatedIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardHover(element, null, null, true);
    }

    private void OnRelatedOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardHover(element, null, null, false);
    }

    private void OnRelatedPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, true);
    }

    private void OnRelatedReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, false);
    }

    private void OnRelatedContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue && e.Item is VodCardViewModel card) _ = card.LoadVisualAsync();
    }

    private void OnCastClick(object sender, RoutedEventArgs e) => App.Window.Navigate("cast");
}
