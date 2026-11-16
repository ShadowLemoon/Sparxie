using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Sparxie.Contracts.Models;

namespace Sparxie.Desktop.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool visible && visible ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool visible && visible ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class GameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        GameType.Genshin => "原神",
        GameType.StarRail => "崩坏：星穹铁道",
        GameType.ZenlessZoneZero => "绝区零",
        GameType.Endfield => "明日方舟：终末地",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class GameIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is GameType game && Sparxie.Desktop.Services.GameIconCache.Get(game) is { } path
            ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path))
            : Microsoft.UI.Xaml.DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>终末地界面模式的显示名（存的是 touch / pc）。</summary>
public sealed class EndfieldInputDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        "pc" => "键鼠与手柄",
        _ => "触控",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PriorityDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ProcessPriority.BelowNormal => "低于正常",
        ProcessPriority.AboveNormal => "高于正常",
        ProcessPriority.High => "高",
        _ => "正常",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
