using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace LisoP2P.App.Services;

public sealed class AppNotification
{
    public required string Message { get; init; }
    public required ErrorSeverity Severity { get; init; }
    public NotificationKey? Key { get; init; }
    public string? ActionLabel { get; init; }
    public ICommand? Action { get; init; }

    public bool HasAction => Action is not null && !string.IsNullOrWhiteSpace(ActionLabel);

    public static AppNotification Transient(string message, ErrorSeverity severity) =>
        new() { Message = message, Severity = severity };

    public static AppNotification Persistent(
        NotificationKey? key,
        string message,
        string? actionLabel,
        Action? action) => new()
    {
        Message = message,
        Severity = ErrorSeverity.Error,
        Key = key,
        ActionLabel = actionLabel,
        Action = action is null ? null : new RelayCommand(action),
    };
}
