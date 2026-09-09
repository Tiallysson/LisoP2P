namespace LisoP2P.Core;

public interface IIdentityStore
{
    PeerIdentity Current { get; }

    PeerId Id { get; }
    string Nickname { get; }
    string Fingerprint { get; }

    event Action<string>? NicknameChanged;

    void SetNickname(string nickname);
}
