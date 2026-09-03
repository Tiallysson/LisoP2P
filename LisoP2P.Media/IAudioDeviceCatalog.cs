namespace LisoP2P.Media;

public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
}
