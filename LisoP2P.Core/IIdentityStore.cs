namespace LisoP2P.Core;

public interface IIdentityStore
{
    PeerId Id { get; }
    string Nickname { get; }
}
