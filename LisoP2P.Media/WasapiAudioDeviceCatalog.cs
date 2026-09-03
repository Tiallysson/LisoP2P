using System.Runtime.Versioning;
using NAudio.CoreAudioApi;

namespace LisoP2P.Media;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioDeviceCatalog : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => Enumerate(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => Enumerate(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = TryGetDefaultId(enumerator, flow);

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
            }
        }
        catch (Exception)
        {
        }

        return devices;
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
