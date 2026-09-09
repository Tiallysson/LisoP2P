namespace LisoP2P.Core.Settings;

public interface IAppSettingsStore
{
    AppSettings Current { get; }

    /// <summary>Where the file lives, so the UI can point the user at it.</summary>
    string FilePath { get; }

    event Action<AppSettings>? Changed;

    void Save(AppSettings settings);
}
