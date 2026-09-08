namespace LisoP2P.Net;

/// <summary>
/// Encode quality by receiver count. One encode feeds every receiver, so CPU does not scale with
/// the audience but upload bandwidth does — the only lever left is to lower the source. The steps
/// are a reasoned starting point, not a measurement; calibrating them needs the four-peer test.
/// </summary>
public static class BitrateLadder
{
    public static (int BitrateKbps, int Fps) SelectFor(int receiverCount) => receiverCount switch
    {
        <= 1 => (3000, 30),
        2 => (2200, 30),
        3 => (1600, 24),
        _ => (1000, 15),
    };
}
