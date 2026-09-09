using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Core;
using LisoP2P.Core.Settings;

namespace LisoP2P.App.ViewModels;

/// <summary>
/// One screen, a few fields, a button — shown only on the run that generated the key pair. The
/// firewall warning lives here as well as in the README because nobody reads the README.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly IIdentityStore _identity;
    private readonly IAppSettingsStore _settings;

    public string Fingerprint => _identity.Fingerprint;

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private string _error = "";

    public event Action? Completed;

    public WelcomeViewModel(IIdentityStore identity, IAppSettingsStore settings)
    {
        _identity = identity;
        _settings = settings;
        _nickname = identity.Nickname;
    }

    [RelayCommand]
    private void Start()
    {
        if (!NicknameRules.IsValid(Nickname))
        {
            Error = $"Informe um nome com até {NicknameRules.MaxLength} caracteres.";
            return;
        }

        _identity.SetNickname(Nickname);

        var settings = _settings.Current;
        settings.Nickname = _identity.Nickname;
        _settings.Save(settings);

        Completed?.Invoke();
    }
}
