using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LisoP2P.App.Services;
using LisoP2P.Net;

namespace LisoP2P.App.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        if (string.Equals(parameter as string, "Invert", StringComparison.Ordinal))
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;

        if (string.Equals(parameter as string, "Invert", StringComparison.Ordinal))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = !string.IsNullOrWhiteSpace(value as string);

        if (string.Equals(parameter as string, "Invert", StringComparison.Ordinal))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SessionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeBrushes.Lookup(value switch
        {
            SessionState.Connected => "StatusSuccess",
            SessionState.Connecting or SessionState.Handshaking or SessionState.Reconnecting => "StatusWarning",
            _ => "StatusDanger",
        });

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SessionStateToDotBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeBrushes.Lookup(value switch
        {
            SessionState.Connected => "StatusSuccess",
            SessionState.Connecting or SessionState.Handshaking or SessionState.Reconnecting => "StatusWarning",
            _ => "TextMuted",
        });

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeBrushes.Lookup(value switch
        {
            ErrorSeverity.Warning => "NotificationWarning",
            ErrorSeverity.Error => "NotificationError",
            _ => "NotificationInfo",
        });

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
