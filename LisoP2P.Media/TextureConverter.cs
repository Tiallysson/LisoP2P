using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LisoP2P.Media;

public sealed class TextureConverter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _output;
    private readonly ID3D11Texture2D[] _staging = new ID3D11Texture2D[2];
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private readonly Dictionary<nint, ID3D11VideoProcessorInputView> _inputViews = [];
    private readonly Format _format;

    private int _writeIndex;
    private bool _hasPending;
    private long _pendingTimestamp;

    public int Width { get; }
    public int Height { get; }
    public int BufferSize { get; }

    public TextureConverter(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        Format format)
    {
        _context = context;
        _format = format;
        Width = width;
        Height = height;
        BufferSize = format == Format.NV12 ? width * height * 3 / 2 : width * height * 4;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();

        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)sourceWidth,
            InputHeight = (uint)sourceHeight,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDescription);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(0, 0, width, height));
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, width, height));
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        _output = device.CreateTexture2D(new Texture2DDescription
        {
            Format = format,
            Width = (uint)width,
            Height = (uint)height,
            ArraySize = 1,
            MipLevels = 1,
            BindFlags = BindFlags.RenderTarget,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            MiscFlags = ResourceOptionFlags.None,
        });

        for (var i = 0; i < _staging.Length; i++)
        {
            _staging[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Format = format,
                Width = (uint)width,
                Height = (uint)height,
                ArraySize = 1,
                MipLevels = 1,
                BindFlags = BindFlags.None,
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                SampleDescription = new SampleDescription(1, 0),
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        _outputView = _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });
    }

    public bool TryConvert(ID3D11Texture2D source, long timestampTicks, byte[] destination, out long readyTimestampTicks)
    {
        readyTimestampTicks = 0;

        if (destination.Length < BufferSize)
        {
            return false;
        }

        var inputView = GetInputView(source);

        var stream = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = inputView,
        };

        if (_videoContext.VideoProcessorBlt(_processor, _outputView, 0, [stream]).Failure)
        {
            return false;
        }

        _context.CopyResource(_staging[_writeIndex], _output);

        if (!_hasPending)
        {
            _hasPending = true;
            _pendingTimestamp = timestampTicks;
            _writeIndex ^= 1;
            return false;
        }

        var readIndex = _writeIndex ^ 1;
        readyTimestampTicks = _pendingTimestamp;
        _pendingTimestamp = timestampTicks;
        _writeIndex = readIndex;

        if (_context.Map(_staging[readIndex], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).Failure)
        {
            return false;
        }

        try
        {
            CopyRows(mapped, destination);
        }
        finally
        {
            _context.Unmap(_staging[readIndex], 0);
        }

        return true;
    }

    private void CopyRows(MappedSubresource mapped, byte[] destination)
    {
        var source = mapped.DataPointer;
        var pitch = (int)mapped.RowPitch;
        var rowBytes = _format == Format.NV12 ? Width : Width * 4;
        var chromaRows = _format == Format.NV12 ? Height / 2 : 0;

        if (pitch == rowBytes)
        {
            Marshal.Copy(source, destination, 0, rowBytes * (Height + chromaRows));
            return;
        }

        var offset = 0;

        for (var row = 0; row < Height; row++)
        {
            Marshal.Copy(source + (row * pitch), destination, offset, rowBytes);
            offset += rowBytes;
        }

        var chromaStart = source + (Height * pitch);
        for (var row = 0; row < chromaRows; row++)
        {
            Marshal.Copy(chromaStart + (row * pitch), destination, offset, rowBytes);
            offset += rowBytes;
        }
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D source)
    {
        var key = source.NativePointer;
        if (_inputViews.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var view = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        _inputViews[key] = view;
        return view;
    }

    public void Dispose()
    {
        foreach (var view in _inputViews.Values)
        {
            view.Dispose();
        }

        _inputViews.Clear();
        _outputView.Dispose();

        foreach (var staging in _staging)
        {
            staging?.Dispose();
        }

        _output.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
