using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
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

public sealed class BoolToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

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

public sealed class DeliveredTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "entregue" : "pendente";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The session state as a colour. "Reconectando" and "Desconectado" have to differ by more than
/// the wording, which is the whole point of this converter existing.
/// </summary>
public sealed class SessionStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Connected = Freeze("#FF98C379");
    private static readonly SolidColorBrush Working = Freeze("#FFE5C07B");
    private static readonly SolidColorBrush Closed = Freeze("#FFE06C75");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SessionState.Connected => Connected,
            SessionState.Connecting or SessionState.Handshaking or SessionState.Reconnecting => Working,
            _ => Closed,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = Freeze("#FF2F3B47");
    private static readonly SolidColorBrush Warning = Freeze("#FF3E3520");
    private static readonly SolidColorBrush Error = Freeze("#FF43262A");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ErrorSeverity.Warning => Warning,
            ErrorSeverity.Error => Error,
            _ => Info,
        };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
