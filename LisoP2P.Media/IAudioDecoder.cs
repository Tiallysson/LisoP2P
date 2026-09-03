namespace LisoP2P.Media;

public interface IAudioDecoder : IDisposable
{
    float[] Decode(byte[] opusData);
    float[] DecodePacketLoss();
}
