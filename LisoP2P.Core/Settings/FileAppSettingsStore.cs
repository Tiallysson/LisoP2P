using System.Text.Json;

namespace LisoP2P.Core.Settings;

public sealed class FileAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _sync = new();
    private AppSettings _current;

    public string FilePath { get; }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current.Clone();
            }
        }
    }

    public event Action<AppSettings>? Changed;

    public FileAppSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "settings.json");
        _current = Load();
    }

    public void Save(AppSettings settings)
    {
        var normalized = settings.Normalized();

        lock (_sync)
        {
            Persist(normalized);
            _current = normalized;
        }

        Changed?.Invoke(normalized.Clone());
    }

    private AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            // Written on the first boot rather than on the first save, so the file the README
            // points at is there to be inspected and hand-edited from the start.
            var defaults = new AppSettings();

            try
            {
                Persist(defaults);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return defaults;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            return decoded?.Normalized() ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A settings file that cannot be read means "use the defaults", never "do not start".
            return new AppSettings();
        }
    }

    private void Persist(AppSettings settings)
    {
        var temporary = FilePath + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
