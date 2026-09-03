using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public sealed class Mp4FileWriter : IEncodedFrameWriter
{
    private readonly IMFSinkWriter _writer;
    private readonly IMFMediaType _mediaType;
    private readonly int _streamIndex;
    private readonly long _defaultDuration;
    private bool _closed;

    public long BytesWritten { get; private set; }

    private Mp4FileWriter(IMFSinkWriter writer, IMFMediaType mediaType, int streamIndex, long defaultDuration)
    {
        _writer = writer;
        _mediaType = mediaType;
        _streamIndex = streamIndex;
        _defaultDuration = defaultDuration;
    }

    public static Mp4FileWriter Create(string path, IMFMediaType encodedType, int fps)
    {
        MediaFoundationRuntime.Startup();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u).CheckError();
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 0u).CheckError();

        var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);
        var streamIndex = writer.AddStream(encodedType);
        writer.SetInputMediaType(streamIndex, encodedType, null);
        writer.BeginWriting();

        return new Mp4FileWriter(writer, encodedType, streamIndex, fps > 0 ? 10_000_000L / fps : 0);
    }

    public void Write(EncodedFrame frame)
    {
        if (_closed || frame.Data.Length == 0)
        {
            return;
        }

        using var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Data.Length);

        buffer.Lock(out var destination, out _, out _);
        Marshal.Copy(frame.Data, 0, destination, frame.Data.Length);
        buffer.Unlock();
        buffer.CurrentLength = frame.Data.Length;

        sample.AddBuffer(buffer);
        buffer.Dispose();

        sample.SampleTime = frame.SampleTimeTicks;
        sample.SampleDuration = frame.DurationTicks > 0 ? frame.DurationTicks : _defaultDuration;

        if (frame.IsKeyframe)
        {
            sample.Set(SampleAttributeKeys.CleanPoint, 1u);
        }

        _writer.WriteSample(_streamIndex, sample);
        BytesWritten += frame.Data.Length;
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;

            try
            {
                _writer.Finalize();
            }
            catch (SharpGenException)
            {
            }
        }

        _writer.Dispose();
        _mediaType.Dispose();
    }
}
