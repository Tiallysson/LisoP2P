using System.Drawing;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LisoP2P.Media;

public sealed class DxgiScreenCapture : IScreenCapture
{
    private sealed record MonitorLocation(int AdapterIndex, int OutputIndex);

    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly List<CaptureAdapterInfo> _monitors = [];
    private readonly List<MonitorLocation> _locations = [];
    private readonly object _sync = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _copy;
    private Thread? _thread;
    private volatile bool _running;
    private int _monitorIndex;
    private Size _frameSize;

    public Size FrameSize => _frameSize;
    public IReadOnlyList<CaptureAdapterInfo> AvailableMonitors => _monitors;
    public ID3D11Device? Device => _device;
    public ID3D11DeviceContext? Context => _context;

    public event Action<CapturedFrame>? FrameCaptured;
    public event Action<string>? Log;
    public event Action<Exception>? Failed;

    public DxgiScreenCapture()
    {
        EnumerateMonitors();
    }

    private void EnumerateMonitors()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (var adapterIndex = 0; factory.EnumAdapters1((uint)adapterIndex, out var adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (var outputIndex = 0; adapter.EnumOutputs((uint)outputIndex, out var output).Success; outputIndex++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        var bounds = description.DesktopCoordinates;
                        var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);

                        _monitors.Add(new CaptureAdapterInfo(_monitors.Count, description.DeviceName, size));
                        _locations.Add(new MonitorLocation(adapterIndex, outputIndex));
                    }
                }
            }
        }
    }

    public IReadOnlyList<string> DescribeAdapters()
    {
        var lines = new List<string>();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (var adapterIndex = 0; factory.EnumAdapters1((uint)adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    var outputs = new List<string>();

                    for (var outputIndex = 0; adapter.EnumOutputs((uint)outputIndex, out var output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            var bounds = output.Description.DesktopCoordinates;
                            outputs.Add($"{output.Description.DeviceName} {bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}");
                        }
                    }

                    lines.Add(
                        $"adapter[{adapterIndex}] {description.Description} " +
                        $"vendor=0x{description.VendorId:X4} device=0x{description.DeviceId:X4} " +
                        $"vram={description.DedicatedVideoMemory / (1024 * 1024)}MB " +
                        $"saidas=[{string.Join(", ", outputs)}]");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add($"falha ao enumerar adaptadores: {ex.Message}");
        }

        return lines;
    }

    public void Start(int monitorIndex)
    {
        if (_running)
        {
            return;
        }

        if (monitorIndex < 0 || monitorIndex >= _locations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorIndex), "Monitor inexistente.");
        }

        _monitorIndex = monitorIndex;
        _running = true;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "LisoP2P.ScreenCapture",
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;

        lock (_sync)
        {
            ReleaseDuplication();
            ReleaseDevice();
        }
    }

    private void CaptureLoop()
    {
        try
        {
            CreateDevice();
            CreateDuplication();
        }
        catch (Exception ex)
        {
            _running = false;
            Failed?.Invoke(ex);
            return;
        }

        while (_running)
        {
            IDXGIOutputDuplication? duplication;
            lock (_sync)
            {
                duplication = _duplication;
            }

            if (duplication is null)
            {
                if (!TryRecoverDuplication())
                {
                    break;
                }

                continue;
            }

            var result = duplication.AcquireNextFrame(100, out _, out var resource);

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                continue;
            }

            if (result.Failure)
            {
                Log?.Invoke($"AcquireNextFrame falhou ({result.Code:X8}); recriando duplication.");
                if (!TryRecoverDuplication())
                {
                    break;
                }

                continue;
            }

            long timestamp;
            try
            {
                using (resource)
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    _context!.CopyResource(_copy!, texture);
                }

                timestamp = DateTime.UtcNow.Ticks;
            }
            finally
            {
                duplication.ReleaseFrame();
            }

            FrameCaptured?.Invoke(new CapturedFrame(_copy!, timestamp));
        }
    }

    private bool TryRecoverDuplication()
    {
        lock (_sync)
        {
            ReleaseDuplication();
        }

        for (var attempt = 0; attempt < 50 && _running; attempt++)
        {
            Thread.Sleep(200);

            try
            {
                CreateDuplication();
                Log?.Invoke("Duplication recriada.");
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 0)
                {
                    Log?.Invoke($"Aguardando duplication ficar disponível: {ex.Message}");
                }
            }
        }

        if (_running)
        {
            _running = false;
            Failed?.Invoke(new InvalidOperationException("Não foi possível recriar a captura de tela."));
        }

        return false;
    }

    private void CreateDevice()
    {
        lock (_sync)
        {
            if (_device is not null)
            {
                return;
            }

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var location = _locations[_monitorIndex];

            if (factory.EnumAdapters1((uint)location.AdapterIndex, out var adapter).Failure)
            {
                throw new InvalidOperationException("Adaptador de vídeo do monitor selecionado não está disponível.");
            }

            using (adapter)
            {
                var result = D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    FeatureLevels,
                    out var device,
                    out _,
                    out var context);

                if (result.Failure)
                {
                    throw new InvalidOperationException(
                        $"Falha ao criar o dispositivo Direct3D 11 ({result.Code:X8}). Verifique o driver da GPU.");
                }

                _device = device;
                _context = context;
            }

            using var multithread = _device.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);
        }
    }

    private void CreateDuplication()
    {
        var location = _locations[_monitorIndex];

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1((uint)location.AdapterIndex, out var adapter).Failure)
        {
            throw new InvalidOperationException("Adaptador de vídeo indisponível.");
        }

        using (adapter)
        {
            if (adapter.EnumOutputs((uint)location.OutputIndex, out var output).Failure)
            {
                throw new InvalidOperationException("Monitor indisponível.");
            }

            using (output)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                var duplication = output1.DuplicateOutput(_device!);
                var description = duplication.Description;
                var width = (int)description.ModeDescription.Width;
                var height = (int)description.ModeDescription.Height;

                lock (_sync)
                {
                    _duplication = duplication;

                    if (_copy is null || _frameSize.Width != width || _frameSize.Height != height)
                    {
                        _copy?.Dispose();
                        _copy = _device!.CreateTexture2D(new Texture2DDescription
                        {
                            Format = description.ModeDescription.Format,
                            Width = (uint)width,
                            Height = (uint)height,
                            ArraySize = 1,
                            MipLevels = 1,
                            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                            Usage = ResourceUsage.Default,
                            CPUAccessFlags = CpuAccessFlags.None,
                            SampleDescription = new SampleDescription(1, 0),
                            MiscFlags = ResourceOptionFlags.None,
                        });

                        _frameSize = new Size(width, height);
                    }
                }
            }
        }
    }

    private void ReleaseDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    private void ReleaseDevice()
    {
        _copy?.Dispose();
        _copy = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        Stop();

        lock (_sync)
        {
            ReleaseDuplication();
            ReleaseDevice();
        }
    }
}
