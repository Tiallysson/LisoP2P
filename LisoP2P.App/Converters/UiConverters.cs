using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LisoP2P.App.Converters;

public sealed class NicknameToInitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Initials(value as string);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var words = name.Split([' ', '\t', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(2);

        foreach (var word in words)
        {
            var letter = word.FirstOrDefault(char.IsLetterOrDigit);

            if (letter != default)
            {
                builder.Append(char.ToUpper(letter, CultureInfo.CurrentCulture));
            }

            if (builder.Length == 2)
            {
                return builder.ToString();
            }
        }

        if (builder.Length > 0)
        {
            return builder.ToString();
        }

        return name.Trim()[..Math.Min(2, name.Trim().Length)].ToUpper(CultureInfo.CurrentCulture);
    }
}

internal static class ThemeBrushes
{
    private static readonly SolidColorBrush Fallback = Freeze(Colors.Gray);

    public static Brush Lookup(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return Fallback;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
