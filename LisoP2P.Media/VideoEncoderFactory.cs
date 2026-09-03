using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public static class VideoEncoderFactory
{
    public static IVideoEncoder Create(
        int width,
        int height,
        int targetBitrateKbps,
        int fps,
        Action<string>? log = null,
        IMediaLogger? logger = null,
        ID3D11Device? device = null,
        bool includeHardware = true)
    {
        return Create(EnumerateSystemEncoders(device, includeHardware), width, height, targetBitrateKbps, fps, log, logger);
    }

    public static IVideoEncoder Create(
        IEnumerable<VideoEncoderCandidate> candidates,
        int width,
        int height,
        int targetBitrateKbps,
        int fps,
        Action<string>? log = null,
        IMediaLogger? logger = null)
    {
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            IVideoEncoder? encoder = null;

            try
            {
                logger?.Info($"Tentando encoder {candidate.Name} (hardware={candidate.IsHardware}) em {width}x{height}@{fps} {targetBitrateKbps}kbps.");

                encoder = candidate.Create();
                encoder.Configure(width, height, targetBitrateKbps, fps);

                var kind = candidate.IsHardware ? "hardware" : "software";
                log?.Invoke($"Encoder ativo: {candidate.Name} ({kind}).");
                logger?.Info($"Encoder ativo: {candidate.Name} ({kind}).");

                if (!candidate.IsHardware && failures.Count > 0)
                {
                    log?.Invoke($"Fallback para software. Motivo: {string.Join(" | ", failures)}");
                    logger?.Info($"Fallback para software. Motivo: {string.Join(" | ", failures)}");
                }

                return encoder;
            }
            catch (Exception ex)
            {
                TryDispose(encoder);
                failures.Add($"{candidate.Name}: {ex.Message}");
                log?.Invoke($"Encoder {candidate.Name} indisponível: {ex.Message}");
                logger?.Error($"Encoder {candidate.Name} indisponível.", ex);
            }
        }

        throw new InvalidOperationException(
            failures.Count == 0
                ? "Nenhum encoder H.264 disponível nesta máquina."
                : $"Nenhum encoder H.264 utilizável. Tentativas: {string.Join(" | ", failures)}");
    }

    private static void TryDispose(IVideoEncoder? encoder)
    {
        try
        {
            encoder?.Dispose();
        }
        catch
        {
        }
    }

    public static IEnumerable<VideoEncoderCandidate> EnumerateSystemEncoders(
        ID3D11Device? device = null,
        bool includeHardware = true)
    {
        MediaFoundationRuntime.Startup();

        if (includeHardware)
        {
            foreach (var candidate in EnumerateCategory(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter, true, device))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in EnumerateCategory(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter, false, null))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<VideoEncoderCandidate> EnumerateCategory(EnumFlag flags, bool isHardware, ID3D11Device? device)
    {
        var outputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };

        using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags, null, outputType);

        foreach (var activate in collection)
        {
            var name = SafeName(activate);
            yield return new VideoEncoderCandidate(
                name,
                isHardware,
                () => new MediaFoundationH264Encoder(activate.ActivateObject<IMFTransform>(), name, isHardware, device));
        }
    }

    public static IReadOnlyList<string> DescribeAvailableEncoders()
    {
        var lines = new List<string>();

        try
        {
            MediaFoundationRuntime.Startup();
            DescribeCategory(lines, EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter, "hardware");
            DescribeCategory(lines, EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter, "software-sync");
            DescribeCategory(lines, EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter, "async");
        }
        catch (Exception ex)
        {
            lines.Add($"falha ao enumerar MFTs: {ex.Message}");
        }

        return lines;
    }

    private static void DescribeCategory(List<string> lines, EnumFlag flags, string label)
    {
        var outputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264,
        };

        using var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags, null, outputType);

        foreach (var activate in collection)
        {
            lines.Add(
                $"mft[{label}] {SafeName(activate)} " +
                $"async={ReadFlag(activate, TransformAttributeKeys.TransformAsync)} " +
                $"d3d11aware={ReadFlag(activate, TransformAttributeKeys.D3D11Aware)}");
        }
    }

    private static string ReadFlag(IMFActivate activate, Guid key)
    {
        try
        {
            return activate.GetUInt32(key, out var value).Success ? value.ToString() : "-";
        }
        catch
        {
            return "-";
        }
    }

    private static string SafeName(IMFActivate activate)
    {
        try
        {
            var name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            return string.IsNullOrWhiteSpace(name) ? "MFT H.264" : name;
        }
        catch
        {
            return "MFT H.264";
        }
    }
}
