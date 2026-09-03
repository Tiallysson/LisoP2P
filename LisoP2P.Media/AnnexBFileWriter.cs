namespace LisoP2P.Media;

public sealed class AnnexBFileWriter : IEncodedFrameWriter
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public long BytesWritten { get; private set; }

    public AnnexBFileWriter(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    public static AnnexBFileWriter Create(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new AnnexBFileWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16));
    }

    public void Write(EncodedFrame frame)
    {
        var span = frame.Data.AsSpan();

        if (!AnnexB.HasStartCode(span))
        {
            _stream.Write(AnnexB.StartCode);
            BytesWritten += AnnexB.StartCode.Length;
        }

        _stream.Write(span);
        BytesWritten += span.Length;
    }

    public void Dispose()
    {
        _stream.Flush();

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
