namespace LisoP2P.App.Services;

public enum ErrorSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Identifies a condition that can be shown and later cleared. Persistent notices are about a
/// state, not an event, so a repeated report must replace the existing notice instead of stacking
/// another copy of it.
/// </summary>
public enum NotificationKey
{
    PortConflict,
    EncoderUnavailable,
    CaptureUnavailable,
    IdentityMigrated,
}

/// <summary>
/// The one rule this exists to enforce: a network error is never a modal dialog that blocks the UI
/// thread. Transient for things that resolve themselves (reconnecting), persistent for things that
/// need the user to act (port taken, no encoder).
/// </summary>
public interface IErrorPresenter
{
    void ShowTransient(string message, ErrorSeverity severity);

    void ShowPersistent(string message, string? actionLabel, Action? action);

    void ShowPersistent(NotificationKey key, string message, string? actionLabel, Action? action);

    void Dismiss(NotificationKey key);
}
