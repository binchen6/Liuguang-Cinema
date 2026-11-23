using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TV.Controls;

public sealed class AnchoredChoice : DropDownButton
{
    private readonly TextBlock _caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new()
    {
        Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
        FontSize = Size("FontSizeMeta"), Opacity = 0.72
    };
    private readonly Border _labelDivider = new()
    {
        Width = 1, Height = 14, Margin = new Thickness(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Opacity = 0.28
    };
    private readonly MenuFlyout _menu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private bool _synchronizing;

    public AnchoredChoice()
    {
        // 单行紧凑布局：小号分类标签 + 分隔线 + 当前值；整体 40 高、8 圆角，和次级按钮同一视觉层。
        _caption.FontSize = Size("FontSizeSecondary");
        _caption.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_label);
        content.Children.Add(_labelDivider);
        content.Children.Add(_caption);
        Content = content;
        Height = 40;
        MinHeight = 40;
        MinWidth = 148;
        Padding = new Thickness(14, 0, 10, 0);
        if (TryResource("RadiusControl") is CornerRadius radius) CornerRadius = radius;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Center;
        FontSize = Size("FontSizeSecondary");
        Flyout = _menu;
        _menu.Opening += (_, _) => BuildMenu();
        Loaded += (_, _) => { UpdateDividerBrush(); Synchronize(); };
        ActualThemeChanged += (_, _) => UpdateDividerBrush();
        TV.Views.MotionService.AttachTactile(this, 1.015f, 0.975f, 0f);
    }

    private void UpdateDividerBrush()
    {
        _labelDivider.Background = Foreground;
    }

    private static double Size(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is double size ? size : 14;

    private static object? TryResource(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value : null;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(AnchoredChoice), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options), typeof(string), typeof(AnchoredChoice), new PropertyMetadata("", OnSourceChanged));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(AnchoredChoice), new PropertyMetadata(null, OnItemChanged));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(AnchoredChoice), new PropertyMetadata(-1, OnIndexChanged));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(AnchoredChoice), new PropertyMetadata("", OnAppearanceChanged));
    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(AnchoredChoice), new PropertyMetadata("", OnAppearanceChanged));
    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(AnchoredChoice), new PropertyMetadata("选择", OnAppearanceChanged));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public string Options { get => (string)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string DisplayMemberPath { get => (string)GetValue(DisplayMemberPathProperty); set => SetValue(DisplayMemberPathProperty, value); }
    public string PlaceholderText { get => (string)GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }
    public event EventHandler? SelectionChanged;

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((AnchoredChoice)sender).Synchronize();
    }

    private static void OnItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var choice = (AnchoredChoice)sender;
        if (!choice._synchronizing) choice.Synchronize(useIndex: false);
        choice.SelectionChanged?.Invoke(choice, EventArgs.Empty);
    }

    private static void OnIndexChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((AnchoredChoice)sender).Synchronize(useIndex: true);

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((AnchoredChoice)sender).UpdateCaption();

    private object[] Values() => ItemsSource?.Cast<object>().ToArray() ?? Options.Split('|', StringSplitOptions.RemoveEmptyEntries);

    private void Synchronize(bool? useIndex = null)
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            var values = Values();
            if (useIndex == true || useIndex is null && SelectedItem is null)
                SelectedItem = SelectedIndex >= 0 && SelectedIndex < values.Length ? values[SelectedIndex] : null;
            else
                SelectedIndex = Array.IndexOf(values, SelectedItem);
            UpdateCaption();
        }
        finally { _synchronizing = false; }
    }

    private string TextFor(object? item) => item is null ? PlaceholderText
        : (DisplayMemberPath.Length > 0 ? item.GetType().GetProperty(DisplayMemberPath)?.GetValue(item)?.ToString() : null)
            ?? item.ToString() ?? PlaceholderText;

    private void UpdateCaption()
    {
        _caption.Text = TextFor(SelectedItem);
        _label.Text = Header;
        _label.Visibility = Header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _labelDivider.Visibility = Header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var announced = Header.Length > 0 ? Header : _caption.Text;
        AutomationProperties.SetName(this, announced);
        AutomationProperties.SetItemStatus(this, SelectedItem is null ? "" : _caption.Text);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        foreach (var value in Values())
        {
            var item = new ToggleMenuFlyoutItem { Text = TextFor(value), IsChecked = Equals(SelectedItem, value) };
            item.Click += (_, _) => SelectedItem = value;
            _menu.Items.Add(item);
        }
    }
}
