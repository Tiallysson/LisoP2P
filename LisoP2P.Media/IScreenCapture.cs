using System.Drawing;
using Vortice.Direct3D11;

namespace LisoP2P.Media;

public interface IScreenCapture : IDisposable
{
    Size FrameSize { get; }
    IReadOnlyList<CaptureAdapterInfo> AvailableMonitors { get; }
    ID3D11Device? Device { get; }
    ID3D11DeviceContext? Context { get; }

    IReadOnlyList<string> DescribeAdapters();

    void Start(int monitorIndex);
    void Stop();

    event Action<CapturedFrame> FrameCaptured;
    event Action<string> Log;
    event Action<Exception> Failed;
}
