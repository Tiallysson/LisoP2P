using LisoP2P.Core;

namespace LisoP2P.Net;

public interface IAudioMixer
{
    void SetActiveSources(IReadOnlyCollection<PeerId> peers);

    /// <summary>
    /// Pulls one frame from every active source and mixes them. Called every 20 ms. Returns null
    /// when no source had audio to play - the same "silence" the playback provider already expects
    /// from a jitter buffer that is still filling.
    /// </summary>
    float[]? MixNextFrame();
}
