using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public static class VideoDecoderFactory
{
    public static IVideoDecoder Create(
        int width,
        int height,
        Action<string>? log = null,
        IMediaLogger? logger = null,
        bool includeHardware = true)
    {
        return Create(EnumerateSystemDecoders(includeHardware), width, height, log, logger);
    }

    public static IVideoDecoder Create(
        IEnumerable<VideoDecoderCandidate> candidates,
        int width,
        int height,
        Action<string>? log = null,
        IMediaLogger? logger = null)
    {
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            IVideoDecoder? decoder = null;

            try
            {
                logger?.Info($"Tentando decoder {candidate.Name} (hardware={candidate.IsHardware}) em {width}x{height}.");

                decoder = candidate.Create();
                decoder.Configure(width, height);

                var kind = candidate.IsHardware ? "hardware" : "software";
                log?.Invoke($"Decoder ativo: {candidate.Name} ({kind}).");
                logger?.Info($"Decoder ativo: {candidate.Name} ({kind}).");

                return decoder;
            }
            catch (Exception ex)
            {
                TryDispose(decoder);
                failures.Add($"{candidate.Name}: {ex.Message}");
                log?.Invoke($"Decoder {candidate.Name} indisponível: {ex.Message}");
                logger?.Error($"Decoder {candidate.Name} indisponível.", ex);
            }
        }

        throw new InvalidOperationException(
            failures.Count == 0
                ? "Nenhum decoder H.264 disponível nesta máquina."
                : $"Nenhum decoder H.264 utilizável. Tentativas: {string.Join(" | ", failures)}");
    }

    /// <summary>
    /// Software decoders come first on purpose: fase 3 already depends on hardware encode from
    /// fase 2, and the plan warns against debugging hardware encode and hardware decode at the
    /// same time. A single 1:1 H.264 stream decodes cheaply in software.
    /// </summary>
    public static IEnumerable<VideoDecoderCandidate> EnumerateSystemDecoders(bool includeHardware = true)
    {
        MediaFoundationRuntime.Startup();

        foreach (var candidate in EnumerateCategory(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter, false))
        {
            yield return candidate;
        }

        if (!includeHardware)
        {
            yield break;
        }

        foreach (var candidate in EnumerateCategory(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter, true))
        {
            yield return candidate;
        }
    }

    public static IReadOnlyList<string> DescribeAvailableDecoders()
    {
        var lines = new List<string>();

        try
        {
            MediaFoundationRuntime.Startup();
            DescribeCategory(lines, EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter, "software-sync");
            DescribeCategory(lines, EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter, "hardware");
        }
        catch (Exception ex)
        {
            lines.Add($"falha ao enumerar decoders: {ex.Message}");
        }

        return lines;
    }

    private static IEnumerable<VideoDecoderCandidate> EnumerateCategory(EnumFlag flags, bool isHardware)
    {
        using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, (uint)flags, H264InputType(), null);

        foreach (var activate in collection)
        {
            var name = SafeName(activate);
            yield return new VideoDecoderCandidate(
                name,
                isHardware,
                () => new MediaFoundationH264Decoder(activate.ActivateObject<IMFTransform>(), name, isHardware));
        }
    }

    private static void DescribeCategory(List<string> lines, EnumFlag flags, string label)
    {
        using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, (uint)flags, H264InputType(), null);

        foreach (var activate in collection)
        {
            lines.Add($"mft-decoder[{label}] {SafeName(activate)}");
        }
    }

    private static RegisterTypeInfo H264InputType() => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = VideoFormatGuids.H264,
    };

    private static void TryDispose(IVideoDecoder? decoder)
    {
        try
        {
            decoder?.Dispose();
        }
        catch
        {
        }
    }

    private static string SafeName(IMFActivate activate)
    {
        try
        {
            var name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return string.IsNullOrWhiteSpace(name) ? "MFT H.264 decoder" : name;
        }
        catch
        {
            return "MFT H.264 decoder";
        }
    }
}
