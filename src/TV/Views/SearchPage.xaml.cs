using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TV.ViewModels;
using TV.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace TV.Views;

public sealed partial class SearchPage : Page
{
    private double _scrollOffset;
    private ScrollViewer? _resultScroll;
    private SearchViewModel? _observed;
    private bool _firstVisit = true;
    private CancellationTokenSource? _suggestions;
    private bool _suggestionsFromKeyboard;
    private bool _pointerOverSuggestions;

    private bool AreSuggestionsOpen => SuggestionsOverlay.Visibility == Visibility.Visible;

    private void ShowSuggestions()
    {
        var width = QueryBox.ActualWidth;
        if (width > 0) SuggestionsOverlay.Width = Math.Max(220, width);
        SuggestionsOverlay.Visibility = Visibility.Visible;
    }

    private void HideSuggestions()
    {
        SuggestionsOverlay.Visibility = Visibility.Collapsed;
        SuggestionList.SelectedIndex = -1;
        _pointerOverSuggestions = false;
    }

    public SearchPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    private SearchViewModel ViewModel => (SearchViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var model = ViewModel;
        model.PropertyChanged += OnSearchChanged;
        model.Rows.CollectionChanged += OnRowsChanged;
        _observed = model;
        await model.PrepareAsync();
        if (!IsLoaded) return;

        if (_firstVisit || ViewModel.ShowsHistory) FocusQuery(false);
        _firstVisit = false;
        UpdateResultStates();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded) return;
            ConfigureResultsPanel();
            _resultScroll = FindScrollViewer(ResultsList);
            if (_scrollOffset > 0) _resultScroll?.ChangeView(null, _scrollOffset, null, true);
        });
    }

    internal void CloseTransientUI(bool shutdown = false)
    {
        _suggestionsFromKeyboard = false;
        _suggestions?.Cancel();
        HideSuggestions();
        SuggestionList.ItemsSource = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CloseTransientUI();
        if (_observed is { } model)
        {
            model.PropertyChanged -= OnSearchChanged;
            model.Rows.CollectionChanged -= OnRowsChanged;
        }
        _observed = null;
        HideSuggestions();
        if (_resultScroll is not null) _scrollOffset = _resultScroll.VerticalOffset;
        _resultScroll = null;
        if (_suggestions is not null)
        {
            _suggestions.Cancel();
            _suggestions.Dispose();
            _suggestions = null;
        }
    }

    private void OnSuggestionTextChanged(object sender, TextChangedEventArgs args) => ScheduleSuggestions();

    private async void ScheduleSuggestions()
    {
        if (!_suggestionsFromKeyboard || QueryBox.IsComposing) return;
        var pending = _suggestions;
        _suggestions = null;
        pending?.Cancel();
        pending?.Dispose();
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            SuggestionList.ItemsSource = null;
            HideSuggestions();
            return;
        }
        var cts = new CancellationTokenSource();
        _suggestions = cts;
        try
        {
            await Task.Delay(260, cts.Token);
            var suggestions = await ViewModel.SuggestAsync(query, cts.Token);
            if (!IsLoaded || cts.IsCancellationRequested || QueryBox.IsComposing || QueryBox.Text.Trim() != query) return;
            if (QueryBox.FocusState == FocusState.Unfocused)
            {
                HideSuggestions();
                return;
            }
            SuggestionList.ItemsSource = suggestions;
            SuggestionList.SelectedIndex = -1;
            if (suggestions.Count > 0) ShowSuggestions();
            else HideSuggestions();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { Services.Diagnostics.Log($"搜索建议失败: {ex.GetType().Name}"); }
        finally
        {
            if (ReferenceEquals(_suggestions, cts)) _suggestions = null;
            cts.Dispose();
        }
    }

    private void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string title }) return;
        _suggestionsFromKeyboard = false;
        _suggestions?.Cancel();
        ViewModel.Query = title;
        HideSuggestions();
        QueryBox.SelectionStart = title.Length;
        QueryBox.SelectionLength = 0;
        FocusQuery();
    }

    private void OnQueryLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_pointerOverSuggestions) HideSuggestions();
    }

    private void OnQuerySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0) SuggestionsOverlay.Width = Math.Max(220, e.NewSize.Width);
    }

    private void OnSuggestionsPointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOverSuggestions = true;

    private void OnSuggestionsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverSuggestions = false;
        if (QueryBox.FocusState == FocusState.Unfocused) HideSuggestions();
    }

    private void OnSearchChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.SubmittedQuery))
        {
            _suggestionsFromKeyboard = false;
            _suggestions?.Cancel();
            HideSuggestions();
        }
        if (e.PropertyName is nameof(SearchViewModel.Scope) or nameof(SearchViewModel.SubmittedQuery))
        {
            _scrollOffset = 0;
            _resultScroll?.ChangeView(null, 0, null, true);
        }
        UpdateResultStates();
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateResultStates();

    private void UpdateResultStates()
    {
        if (_observed is not { } model) return;
        var resultsMode = !model.ShowsHistory;
        var hasRows = model.Rows.Count > 0;
        var failed = model.Scopes.Any(scope => scope.Group?.CanRetry == true || scope.Group?.CanVerify == true);
        var needsVerify = model.Scopes.Any(scope => scope.Group?.CanVerify == true);

        LoadingState.Visibility = resultsMode && model.IsSearching && !hasRows
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!resultsMode || model.IsSearching || hasRows)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyIcon.Glyph = failed ? "\uE7BA" : "\uE721";
        EmptyTitle.Text = needsVerify ? "部分站点需要安全验证" : failed ? "部分站点搜索未完成" : "没有找到匹配的影片";
        EmptyDescription.Text = needsVerify
            ? "请在左侧站点列表点击「验证」完成验证码后自动恢复搜索。"
            : failed
            ? (string.IsNullOrWhiteSpace(model.Status)
                ? "可在左侧站点列表点击「重试」重新搜索失败的站点。"
                : $"{model.Status} · 可在左侧站点列表点击「重试」重新搜索失败的站点。")
            : "换个关键词试试，或清空筛选条件后重新搜索。";
        EmptyState.Visibility = Visibility.Visible;
    }

    internal void FocusQuery(bool resetSuggestions = true)
    {
        if (resetSuggestions) _suggestionsFromKeyboard = false;
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void OnQueryTextInputReceived(object? sender, EventArgs e)
    {
        _suggestionsFromKeyboard = true;
        if (!QueryBox.IsComposing && QueryBox.Text.Trim().Length > 0) ScheduleSuggestions();
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var command = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        _suggestionsFromKeyboard = !command;
        if (e.Key == VirtualKey.Escape && AreSuggestionsOpen)
        {
            HideSuggestions();
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.Down or VirtualKey.Up && AreSuggestionsOpen && SuggestionList.Items.Count > 0)
        {
            var delta = e.Key == VirtualKey.Down ? 1 : -1;
            SuggestionList.SelectedIndex = Math.Clamp(SuggestionList.SelectedIndex + delta, 0, SuggestionList.Items.Count - 1);
            if (SuggestionList.SelectedItem is not null) SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
            e.Handled = true;
            return;
        }
        if (e.Key != VirtualKey.Enter) return;
        _suggestionsFromKeyboard = false;
        if (AreSuggestionsOpen && SuggestionList.SelectedItem is string title) ViewModel.Query = title;
        else ViewModel.Query = QueryBox.Text;
        _suggestions?.Cancel();
        HideSuggestions();
        ViewModel.SubmitCommand.Execute(null);
        e.Handled = true;
    }

    private void OnResultSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not GridView view || view.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        ConfigurePanel(panel);
    }

    private void ConfigureResultsPanel()
    {
        if (ResultsList?.ItemsPanelRoot is ItemsWrapGrid panel) ConfigurePanel(panel);
    }

    private static void ConfigurePanel(ItemsWrapGrid panel)
    {
        // 与 Tokens.xaml 的海报令牌保持一致：封面 188 宽 + 列间距 18；行高 = 封面 282 + 标题 44 + 元数据 18 + 间距 8×2 + 行间距 22。
        const double posterWidth = 188, columnGap = 18, itemHeight = 382;
        panel.ItemWidth = posterWidth + columnGap;
        panel.ItemHeight = itemHeight;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SearchGroup group }) ViewModel.RetryCommand.Execute(group);
    }

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchGroup { PendingVerification: { } challenge } group } || XamlRoot is null) return;
        try
        {
            var dialog = new Controls.SiteVerificationDialog(App.Services, challenge, XamlRoot);
            if (await dialog.ShowAsync() && IsLoaded)
                ViewModel.RetryCommand.Execute(group);
        }
        catch (Exception ex) { Diagnostics.Log($"站点验证对话框失败: {ex.Message}"); }
    }

    private void OnResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VodCardViewModel card }) ViewModel.OpenResultCommand.Execute(card);
    }

    private void OnScopeContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue && e.Item is SearchScope scope)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e.ItemContainer, scope.Name);
    }

    private void OnResultContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue && e.Item is VodCardViewModel card) _ = card.LoadVisualAsync();
    }

    private void OnResultCardIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardHover(element, null, null, true);
    }

    private void OnResultCardOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardHover(element, null, null, false);
    }

    private void OnResultCardPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, true);
    }

    private void OnResultCardReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, false);
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string query }) return;
        ViewModel.Query = query;
        FocusQuery();
        QueryBox.SelectAll();
        _ = ViewModel.SubmitCommand.ExecuteAsync(null);
    }

    private void OnRemoveHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string query }) ViewModel.RemoveHistoryCommand.Execute(query);
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
