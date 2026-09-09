using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using LisoP2P.Core.Diagnostics;

namespace LisoP2P.App.Services;

/// <summary>
/// Holds what the shell shows as a banner. Every notice is logged on the way through, so app.log
/// carries the same story the user saw without needing to reproduce anything.
/// </summary>
public sealed class NotificationCenter : IErrorPresenter
{
    public static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(6);

    private const int MaxVisible = 4;

    private readonly IAppLogger _log;

    public ObservableCollection<AppNotification> Notifications { get; } = [];

    public NotificationCenter(IAppLogger log) => _log = log;

    public void ShowTransient(string message, ErrorSeverity severity)
    {
        Log(message, severity);

        var notification = AppNotification.Transient(message, severity);

        OnUiThread(() =>
        {
            Add(notification);

            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TransientLifetime };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Notifications.Remove(notification);
            };
            timer.Start();
        });
    }

    public void ShowPersistent(string message, string? actionLabel, Action? action) =>
        ShowPersistent(null, message, actionLabel, action);

    public void ShowPersistent(NotificationKey key, string message, string? actionLabel, Action? action) =>
        ShowPersistent((NotificationKey?)key, message, actionLabel, action);

    public void Dismiss(NotificationKey key) => OnUiThread(() =>
    {
        foreach (var existing in Notifications.Where(n => n.Key == key).ToList())
        {
            Notifications.Remove(existing);
        }
    });

    private void ShowPersistent(NotificationKey? key, string message, string? actionLabel, Action? action)
    {
        Log(message, ErrorSeverity.Error);

        var notification = AppNotification.Persistent(key, message, actionLabel, action);

        OnUiThread(() =>
        {
            if (key is { } existingKey)
            {
                // A state reported twice replaces its notice; it never stacks a second copy.
                foreach (var existing in Notifications.Where(n => n.Key == existingKey).ToList())
                {
                    Notifications.Remove(existing);
                }
            }

            Add(notification);
        });
    }

    private void Add(AppNotification notification)
    {
        Notifications.Add(notification);

        while (Notifications.Count > MaxVisible)
        {
            // Drop the oldest transient first; a persistent notice is about an unresolved state.
            var victim = Notifications.FirstOrDefault(n => n.Key is null && !n.HasAction) ?? Notifications[0];
            Notifications.Remove(victim);
        }
    }

    private void Log(string message, ErrorSeverity severity)
    {
        switch (severity)
        {
            case ErrorSeverity.Error:
                _log.Error(message);
                break;
            case ErrorSeverity.Warning:
                _log.Warning(message);
                break;
            default:
                _log.Info(message);
                break;
        }
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
