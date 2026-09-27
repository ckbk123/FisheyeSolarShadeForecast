using System.Text.Json;
using OpenCvSharp;

namespace SolarShade.SkyPhotoMasking;

public sealed record SkyMaskStage(SkyMaskResult Result, IReadOnlyList<string> Artifacts);

/// <summary>Persists actual returned segmentation bytes; exporting a cache hit never reruns inference.</summary>
public static class SkyMaskExporter
{
    private sealed record SavedMask(string Origin, SkyMaskResult Mask, string PixelConvention);

    public static SkyMaskResult Load(string directory)
    {
        var saved = JsonSerializer.Deserialize<SavedMask>(File.ReadAllText(Path.Combine(directory, "mask-details.json")))
            ?? throw new InvalidDataException("Mask metadata is empty.");
        var result = saved.Mask with { Png = File.ReadAllBytes(Path.Combine(directory, "sky-mask.png")) };
        Validate(result, CancellationToken.None);
        return result;
    }

    public static IReadOnlyList<string> Export(SkyMaskResult result, string directory, string origin = "Computed",
        CancellationToken cancellationToken = default)
    {
        Validate(result, cancellationToken);
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string png = Path.Combine(directory, "sky-mask.png"), metadata = Path.Combine(directory, "mask-details.json");
        Write(png, result.Png, cancellationToken);
        Write(metadata, JsonSerializer.SerializeToUtf8Bytes(new SavedMask(origin, result,
            "255 = sky; 0 = obstruction or outside lens; EXIF-oriented image coordinates"),
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return Array.AsReadOnly(new[] { png, metadata });
    }

    private static unsafe void Validate(SkyMaskResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var decoded = Cv2.ImDecode(result.Png, ImreadModes.Grayscale);
        if (decoded.Empty() || decoded.Width != result.Width || decoded.Height != result.Height ||
            result.Disk.ImageWidth != result.Width || result.Disk.ImageHeight != result.Height)
            throw new InvalidDataException("Mask PNG and returned dimensions disagree.");
        int height = decoded.Height, width = decoded.Width;
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            // One row pointer access rather than one native call per pixel; span search is vectorized.
            var pixels = new ReadOnlySpan<byte>((void*)decoded.Ptr(y), width);
            if (pixels.IndexOfAnyExcept((byte)0, (byte)255) >= 0)
                throw new InvalidDataException("Expected a black-and-white sky mask.");
        }
    }

    private static void Write(string path, byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); ct.ThrowIfCancellationRequested(); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
