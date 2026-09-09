using System.Windows.Input;

namespace LisoP2P.App.ViewModels;

public sealed record PushToTalkOption(string Name, Key Key)
{
    public override string ToString() => Name;
}

public static class PushToTalkKeys
{
    public const string Default = "LeftCtrl";

    /// <summary>
    /// Settings store the key by name so settings.json stays readable and survives a WPF enum
    /// change. An unknown name falls back to Ctrl rather than leaving push-to-talk dead.
    /// </summary>
    public static Key Parse(string? name) =>
        Enum.TryParse<Key>(name, ignoreCase: true, out var key) && key != Key.None ? key : Key.LeftCtrl;

    public static string Describe(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.Space => "Espaço",
        _ => key.ToString(),
    };

    /// <summary>
    /// True when the key that was pressed should count as the configured one. Left and right
    /// modifiers are the same key to the user, so either side of the keyboard works.
    /// </summary>
    public static bool Matches(Key expected, Key pressed) =>
        pressed == expected || (expected, pressed) switch
        {
            (Key.LeftCtrl, Key.RightCtrl) or (Key.RightCtrl, Key.LeftCtrl) => true,
            (Key.LeftAlt, Key.RightAlt) or (Key.RightAlt, Key.LeftAlt) => true,
            (Key.LeftShift, Key.RightShift) or (Key.RightShift, Key.LeftShift) => true,
            _ => false,
        };
}
