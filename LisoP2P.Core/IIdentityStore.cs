namespace LisoP2P.Core;

public interface IIdentityStore
{
    PeerId Id { get; }
    string Nickname { get; }

    event Action<string>? NicknameChanged;

    void SetNickname(string nickname);
}
