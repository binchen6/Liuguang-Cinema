using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TV.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is true;
        if (parameter is "Inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is Visibility.Visible;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text));
        if (parameter is "Inverse") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class BoolToFavoriteConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "已收藏" : "收藏";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IntroMaxLinesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? 0 : 5;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class IntroToggleTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "收起 ▲" : "查看全部 ▼";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
