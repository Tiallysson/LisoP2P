using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NoEventsAvailable = unchecked((int)0xC00D3E80);
    private const uint InterlaceModeProgressive = 2;
    private const uint H264ProfileMain = 77;
    private const uint PictureTypeIdr = 0;
    private const long TicksPerSecond = 10_000_000;
    private const int NoWait = 1;
    private const int MaxQueuedInputs = 2;
    private const int PumpIdleMilliseconds = 2;

    private readonly ID3D11Device? _device;
    private readonly Queue<IMFSample> _inputQueue = new();
    private readonly object _inputSync = new();
    private readonly ManualResetEventSlim _work = new(false);
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _events;
    private IMFDXGIDeviceManager? _deviceManager;
    private CodecApi? _codecApi;
    private bool _supportsForcedKeyframe;
    private long _frameDuration;
    private long _sampleTime;
    private long _baseTimestampTicks;
    private int _outputBufferSize;
    private bool _isAsync;
    private int _inputRequests;
    private Thread? _pump;
    private Exception? _pumpError;
    private volatile bool _stopping;

    public int DroppedInputs { get; private set; }

    public string Name { get; }
    public bool IsHardware { get; }
    public bool IsAsync => _isAsync;

    public event Action<EncodedFrame>? FrameEncoded;

    public MediaFoundationH264Encoder(IMFTransform transform, string name, bool isHardware, ID3D11Device? device = null)
    {
        _transform = transform;
        _device = device;
        Name = name;
        IsHardware = isHardware;
    }

    public void Configure(int width, int height, int targetBitrateKbps, int fps)
    {
        MediaFoundationRuntime.Startup();

        if (_transform is null)
        {
            throw new InvalidOperationException("Encoder já foi liberado.");
        }

        _frameDuration = TicksPerSecond / fps;
        _sampleTime = 0;
        _baseTimestampTicks = 0;
        _inputRequests = 0;
        _isAsync = ReadTransformFlag(_transform, TransformAttributeKeys.TransformAsync);

        if (_isAsync)
        {
            UnlockAsyncTransform(_transform);
            AttachDeviceManager(_transform);
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        }

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(targetBitrateKbps * 1000)).CheckError();
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileMain).CheckError();
        outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(fps * 2)).CheckError();
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        _transform.SetOutputType(0, outputType, 0);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive).CheckError();
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        _transform.SetInputType(0, inputType, 0);

        var streamInfo = _transform.GetOutputStreamInfo(0);
        _outputBufferSize = Math.Max(streamInfo.Size, width * height);

        _codecApi = CodecApi.From(_transform);
        _supportsForcedKeyframe = _codecApi?.IsSupported(CodecApi.ForceKeyFrame) == true;

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        if (_isAsync)
        {
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "LisoP2P.EncoderPump",
            };
            _pump.Start();
        }
    }

    public IMFMediaType? CreateOutputMediaType()
    {
        if (_transform is null)
        {
            return null;
        }

        try
        {
            using var current = _transform.GetOutputCurrentType(0);
            var copy = MediaFactory.MFCreateMediaType();
            current.CopyAllItems(copy).CheckError();
            return copy;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private static bool ReadTransformFlag(IMFTransform transform, Guid key)
    {
        using var attributes = transform.Attributes;
        return attributes is not null
            && attributes.GetUInt32(key, out var value).Success
            && value != 0;
    }

    private static void UnlockAsyncTransform(IMFTransform transform)
    {
        using var attributes = transform.Attributes
            ?? throw new NotSupportedException("MFT assíncrono sem atributos; não é possível destravá-lo.");

        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
    }

    private void AttachDeviceManager(IMFTransform transform)
    {
        if (_device is null || !ReadTransformFlag(transform, TransformAttributeKeys.D3D11Aware))
        {
            return;
        }

        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(_device).CheckError();
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)manager.NativePointer);
        _deviceManager = manager;
    }

    public EncodedFrame? Encode(VideoFrame frame, bool forceKeyframe)
    {
        if (_transform is null)
        {
            throw new InvalidOperationException("Encoder não configurado.");
        }

        return _isAsync ? EncodeAsync(frame, forceKeyframe) : EncodeSync(frame, forceKeyframe);
    }

    private EncodedFrame? EncodeSync(VideoFrame frame, bool forceKeyframe)
    {
        using (var sample = CreateInputSample(frame, forceKeyframe))
        {
            _transform!.ProcessInput(0, sample, 0);
        }

        return DrainOutput();
    }

    private EncodedFrame? EncodeAsync(VideoFrame frame, bool forceKeyframe)
    {
        if (_pumpError is { } error)
        {
            _pumpError = null;
            throw error;
        }

        var sample = CreateInputSample(frame, forceKeyframe);

        lock (_inputSync)
        {
            while (_inputQueue.Count >= MaxQueuedInputs)
            {
                _inputQueue.Dequeue().Dispose();
                DroppedInputs++;
            }

            _inputQueue.Enqueue(sample);
        }

        _work.Set();
        return null;
    }

    private void PumpLoop()
    {
        while (!_stopping)
        {
            try
            {
                if (TryProcessEvent() || TryFeedInput())
                {
                    continue;
                }

                _work.Wait(PumpIdleMilliseconds);
                _work.Reset();
            }
            catch (Exception ex)
            {
                _pumpError ??= ex;
                return;
            }
        }
    }

    private bool TryProcessEvent()
    {
        if (!TryGetEvent(out var mediaEvent) || mediaEvent is null)
        {
            return false;
        }

        using (mediaEvent)
        {
            switch (mediaEvent.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    _inputRequests++;
                    return true;

                case MediaEventTypes.TransformHaveOutput:
                    var encoded = ReceiveAsyncOutput();
                    if (encoded is not null)
                    {
                        FrameEncoded?.Invoke(encoded.Value);
                    }

                    return true;

                default:
                    return true;
            }
        }
    }

    private bool TryFeedInput()
    {
        if (_inputRequests <= 0)
        {
            return false;
        }

        IMFSample sample;

        lock (_inputSync)
        {
            if (_inputQueue.Count == 0)
            {
                return false;
            }

            sample = _inputQueue.Dequeue();
        }

        try
        {
            _transform!.ProcessInput(0, sample, 0);
            _inputRequests--;
        }
        finally
        {
            sample.Dispose();
        }

        return true;
    }

    private bool TryGetEvent(out IMFMediaEvent? mediaEvent)
    {
        try
        {
            mediaEvent = _events!.GetEvent(NoWait);
            return true;
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == NoEventsAvailable)
        {
            mediaEvent = null;
            return false;
        }
    }

    private EncodedFrame? ReceiveAsyncOutput()
    {
        var outputBuffer = new OutputDataBuffer { StreamID = 0 };
        var result = _transform!.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);

        if (result.Code == StreamChange)
        {
            RenegotiateOutputType();
            return null;
        }

        if (result.Failure)
        {
            outputBuffer.Sample?.Dispose();
            result.CheckError();
            return null;
        }

        var sample = outputBuffer.Sample;
        if (sample is null)
        {
            return null;
        }

        try
        {
            return ReadSample(sample);
        }
        finally
        {
            sample.Dispose();
            outputBuffer.Events?.Dispose();
        }
    }

    private IMFSample CreateInputSample(VideoFrame frame, bool forceKeyframe)
    {
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);

        buffer.Lock(out var destination, out _, out _);
        Marshal.Copy(frame.Nv12, 0, destination, frame.Length);
        buffer.Unlock();
        buffer.CurrentLength = frame.Length;

        sample.AddBuffer(buffer);
        buffer.Dispose();

        sample.SampleTime = NextSampleTime(frame.TimestampTicks);
        sample.SampleDuration = _frameDuration;

        if (forceKeyframe)
        {
            sample.Set(SampleAttributeKeys.VideoEncodePictureType, PictureTypeIdr);

            if (_supportsForcedKeyframe)
            {
                _codecApi!.TrySetUInt32(CodecApi.ForceKeyFrame, 1);
            }
        }

        return sample;
    }

    private long NextSampleTime(long timestampTicks)
    {
        if (timestampTicks <= 0)
        {
            _sampleTime += _frameDuration;
            return _sampleTime;
        }

        if (_baseTimestampTicks == 0)
        {
            _baseTimestampTicks = timestampTicks;
        }

        var elapsed = timestampTicks - _baseTimestampTicks;

        if (elapsed <= _sampleTime)
        {
            elapsed = _sampleTime + _frameDuration;
        }

        _sampleTime = elapsed;
        return _sampleTime;
    }

    private EncodedFrame? DrainOutput()
    {
        var streamInfo = _transform!.GetOutputStreamInfo(0);
        var providesSamples = (streamInfo.Flags & 0x100) != 0 || (streamInfo.Flags & 0x200) != 0;

        EncodedFrame? last = null;

        while (true)
        {
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
                RenegotiateOutputType();
                continue;
            }

            if (result.Failure)
            {
                allocated?.Dispose();
                result.CheckError();
                return last;
            }

            var sample = outputBuffer.Sample;
            if (sample is null)
            {
                return last;
            }

            try
            {
                var encoded = ReadSample(sample);
                last = encoded;
                FrameEncoded?.Invoke(encoded);
            }
            finally
            {
                sample.Dispose();
                outputBuffer.Events?.Dispose();
            }
        }
    }

    private void RenegotiateOutputType()
    {
        using var available = _transform!.GetOutputAvailableType(0, 0);
        _transform.SetOutputType(0, available, 0);
    }

    private EncodedFrame ReadSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);

        var data = new byte[length];
        Marshal.Copy(pointer, data, 0, length);
        buffer.Unlock();

        var isKeyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out var cleanPoint).Success && cleanPoint != 0;

        var sampleTime = ReadSampleTime(sample);

        return new EncodedFrame(
            data,
            isKeyframe,
            _baseTimestampTicks + sampleTime,
            sampleTime,
            ReadSampleDuration(sample));
    }

    private long ReadSampleTime(IMFSample sample)
    {
        try
        {
            return sample.SampleTime;
        }
        catch (SharpGenException)
        {
            return _sampleTime;
        }
    }

    private long ReadSampleDuration(IMFSample sample)
    {
        try
        {
            var duration = sample.SampleDuration;
            return duration > 0 ? duration : _frameDuration;
        }
        catch (SharpGenException)
        {
            return _frameDuration;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _work.Set();
        _pump?.Join(TimeSpan.FromSeconds(2));
        _pump = null;

        lock (_inputSync)
        {
            while (_inputQueue.Count > 0)
            {
                _inputQueue.Dequeue().Dispose();
            }
        }

        if (_transform is not null)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);

                if (_isAsync)
                {
                    _transform.ProcessMessage(TMessageType.MessageSetD3DManager, UIntPtr.Zero);
                }
            }
            catch (SharpGenException)
            {
            }

            _codecApi?.Dispose();
            _codecApi = null;
            _events?.Dispose();
            _events = null;
            _transform.Dispose();
            _transform = null;
        }

        _deviceManager?.Dispose();
        _deviceManager = null;
        _work.Dispose();
    }
}
