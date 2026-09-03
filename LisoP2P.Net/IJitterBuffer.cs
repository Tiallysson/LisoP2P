namespace LisoP2P.Net;

public interface IJitterBuffer
{
    int Depth { get; }
    int ConcealedFrames { get; }
    int LateDiscards { get; }
    int OverflowDiscards { get; }

    void Push(uint sequenceNumber, byte[] opusData, long arrivalTicks);
    float[]? Pull();
    void Reset();
}
