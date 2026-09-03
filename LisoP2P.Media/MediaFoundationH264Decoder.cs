using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public sealed class MediaFoundationH264Decoder : IVideoDecoder
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NoMoreTypes = unchecked((int)0xC00D36B9);
    private const uint InterlaceModeProgressive = 2;
    private const long TicksPerSecond = 10_000_000;
    private const int AssumedFps = 30;

    private static readonly Guid LowLatencyKey = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private IMFTransform? _transform;
    private byte[][] _bgra = [];
    private int _bgraIndex;
    private long _sampleTime;
    private int _stride;
    private int _codedWidth;
    private int _codedHeight;
    private int _outputBufferSize;

    public string Name { get; }
    public bool IsHardware { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public MediaFoundationH264Decoder(IMFTransform transform, string name, bool isHardware)
    {
        _transform = transform;
        Name = name;
        IsHardware = isHardware;
    }

    public void Configure(int width, int height)
    {
        MediaFoundationRuntime.Startup();

        if (_transform is null)
        {
            throw new InvalidOperationException("Decoder já foi liberado.");
        }

        if (ReadTransformFlag(_transform, TransformAttributeKeys.TransformAsync))
        {
            throw new NotSupportedException("MFT de decode assíncrono não é suportado nesta fase.");
        }

        TrySetLowLatency(_transform);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive).CheckError();
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, AssumedFps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        _transform.SetInputType(0, inputType, 0);

        Width = width;
        Height = height;
        _codedWidth = width;
        _codedHeight = height;
        _stride = width;

        SelectNv12OutputType();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public PreviewFrame? Decode(DecodableFrame frame)
    {
        if (_transform is null)
        {
            throw new InvalidOperationException("Decoder não configurado.");
        }

        if (frame.Data.Length == 0)
        {
            return null;
        }

        using (var sample = CreateInputSample(frame))
        {
            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException)
            {
                return null;
            }
        }

        return DrainOutput();
    }

    private PreviewFrame? DrainOutput()
    {
        PreviewFrame? last = null;

        while (true)
        {
            var streamInfo = _transform!.GetOutputStreamInfo(0);
            var providesSamples = (streamInfo.Flags & 0x100) != 0 || (streamInfo.Flags & 0x200) != 0;

            IMFSample? allocated = null;
            var outputBuffer = new OutputDataBuffer { StreamID = 0 };

            if (!providesSamples)
            {
                allocated = MediaFactory.MFCreateSample();
                var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(streamInfo.Size, _outputBufferSize));
                allocated.AddBuffer(mediaBuffer);
                mediaBuffer.Dispose();
                outputBuffer.Sample = allocated;
            }

            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);

            if (result.Code == NeedMoreInput)
            {
                allocated?.Dispose();
                return last;
            }

            if (result.Code == StreamChange)
            {
                allocated?.Dispose();
                SelectNv12OutputType();
                continue;
            }

            if (result.Failure)
            {
                allocated?.Dispose();
                return last;
            }

            var sample = outputBuffer.Sample;

            if (sample is null)
            {
                return last;
            }

            try
            {
                last = ReadSample(sample) ?? last;
            }
            finally
            {
                sample.Dispose();
                outputBuffer.Events?.Dispose();
            }
        }
    }

    private PreviewFrame? ReadSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);

        try
        {
            var stride = _stride;

            if (length < Nv12Converter.Nv12BufferSize(stride, _codedHeight))
            {
                stride = _codedWidth;

                if (length < Nv12Converter.Nv12BufferSize(stride, _codedHeight))
                {
                    return null;
                }
            }

            var nv12 = new byte[length];
            Marshal.Copy(pointer, nv12, 0, length);

            if (_bgra.Length == 0)
            {
                return null;
            }

            var target = _bgra[_bgraIndex];
            _bgraIndex = (_bgraIndex + 1) % _bgra.Length;

            Nv12Converter.ToBgra(nv12, stride, _codedHeight, Width, Height, target);

            return new PreviewFrame(target, Width, Height, Width * 4);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private IMFSample CreateInputSample(DecodableFrame frame)
    {
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Data.Length);

        buffer.Lock(out var destination, out _, out _);
        Marshal.Copy(frame.Data, 0, destination, frame.Data.Length);
        buffer.Unlock();
        buffer.CurrentLength = frame.Data.Length;

        sample.AddBuffer(buffer);
        buffer.Dispose();

        _sampleTime += TicksPerSecond / AssumedFps;
        sample.SampleTime = _sampleTime;
        sample.SampleDuration = TicksPerSecond / AssumedFps;

        if (frame.IsKeyframe)
        {
            sample.Set(SampleAttributeKeys.CleanPoint, 1u);
        }

        return sample;
    }

    private void SelectNv12OutputType()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType available;

            try
            {
                available = _transform!.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == NoMoreTypes)
            {
                throw new NotSupportedException("O decoder não expôs uma saída NV12.");
            }

            using (available)
            {
                if (available.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                {
                    continue;
                }

                _transform!.SetOutputType(0, available, 0);
                ReadOutputGeometry(available);
                return;
            }
        }
    }

    /// <summary>
    /// H.264 codes in 16-pixel macroblocks, so a 1080-line picture comes out of the decoder as a
    /// 1088-line surface. The coded size drives the buffer and the chroma plane offset; the
    /// visible size stays the one the sender announced, and the padding rows are cropped away.
    /// </summary>
    private void ReadOutputGeometry(IMFMediaType outputType)
    {
        MediaFactory.MFGetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, out var width, out var height);

        _codedWidth = width > 0 ? (int)width : Width;
        _codedHeight = height > 0 ? (int)height : Height;

        Width = Math.Min(Width, _codedWidth);
        Height = Math.Min(Height, _codedHeight);

        _stride = outputType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride) == Result.Ok && stride > 0
            ? (int)stride
            : _codedWidth;

        _outputBufferSize = Nv12Converter.Nv12BufferSize(Math.Max(_stride, _codedWidth), _codedHeight);

        var size = Nv12Converter.BgraBufferSize(Width, Height);
        _bgra = [new byte[size], new byte[size], new byte[size]];
        _bgraIndex = 0;
    }

    private static bool ReadTransformFlag(IMFTransform transform, Guid key)
    {
        using var attributes = transform.Attributes;
        return attributes is not null
            && attributes.GetUInt32(key, out var value).Success
            && value != 0;
    }

    private static void TrySetLowLatency(IMFTransform transform)
    {
        try
        {
            using var attributes = transform.Attributes;
            attributes?.Set(LowLatencyKey, 1u);
        }
        catch (SharpGenException)
        {
        }
    }

    public void Dispose()
    {
        if (_transform is null)
        {
            return;
        }

        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }

        _transform.Dispose();
        _transform = null;
        _bgra = [];
    }
}
