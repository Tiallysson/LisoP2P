using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public static class MediaFoundationRuntime
{
    private static readonly object Sync = new();
    private static bool _started;

    public static void Startup()
    {
        lock (Sync)
        {
            if (_started)
            {
                return;
            }

            MediaFactory.MFStartup(true).CheckError();
            _started = true;
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (!_started)
            {
                return;
            }

            MediaFactory.MFShutdown();
            _started = false;
        }
    }
}
