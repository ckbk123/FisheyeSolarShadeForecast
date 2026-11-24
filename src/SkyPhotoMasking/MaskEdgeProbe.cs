using System.Buffers.Binary;
using OpenCvSharp;

namespace SolarShade.SkyPhotoMasking;

public sealed record MaskEdgeFinding(int BlackPercent, double? RadiusPixels, string Explanation, double? EstimatedLimitFullDegrees);
public readonly record struct MaskRingSample(double RadiusPixels, int BlackSamples, int ObservedBlackSamples, int UnknownSamples);
public sealed record MaskEdgeScan(IReadOnlyList<MaskEdgeFinding> Findings, IReadOnlyList<MaskRingSample> Samples)
{
    public double CalibratedFullDegrees { get; init; }
    public double? LimitRadiusPixels { get; init; }
    public bool AlreadyCovers180 => CalibratedFullDegrees >= 180;
    public bool Reached180 { get; init; }

    // The first ring is exactly at the calibrated limit. The question here concerns additional sky outside it.
    public double? FarthestObservedSkyRadiusPixels
    {
        get
        {
            for (int i = Samples.Count - 1; i > 0; i--)
                if (Samples[i].ObservedBlackSamples + Samples[i].UnknownSamples < MaskEdgeProbe.SamplesPerRing)
                    return Samples[i].RadiusPixels;
            return null;
        }
    }
    public bool HasObservedOuterSamples => Samples.Skip(1).Any(s => s.UnknownSamples < MaskEdgeProbe.SamplesPerRing);
    public bool HasUnknownOuterSamples => Samples.Skip(1).Any(s => s.UnknownSamples > 0);
}

/// <summary>Searches native, complete concentric rings for a persistent outward transition in a binary sky mask.</summary>
public static class MaskEdgeProbe
{
    public const int SamplesPerRing = 1440;
    private static readonly (double Sin, double Cos)[] Bearings = Enumerable.Range(0, SamplesPerRing)
        .Select(i => Math.SinCos(i * Math.Tau / SamplesPerRing)).ToArray();
    private static readonly int[] Thresholds = [80, 90, 95, 100];

    public static MaskEdgeScan AnalyzeTo180(byte[] maskPng, int width, int height, double centerX, double centerY,
        double[] coefficients, double calibratedHalfAngleDegrees, LensDisk? supportDisk, MaskCrop? aiCrop, CancellationToken ct = default,
        int maskInputSize = 0)
    {
        ArgumentNullException.ThrowIfNull(maskPng);
        ct.ThrowIfCancellationRequested();
        if (width <= 0 || height <= 0 || !double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(calibratedHalfAngleDegrees) || calibratedHalfAngleDegrees is <= 0 or > 180)
            throw new ArgumentOutOfRangeException(nameof(calibratedHalfAngleDegrees), "A finite calibrated circle and valid photo dimensions are required.");
        if (supportDisk != null) SkyPhotoMasker.ValidateDisk(supportDisk, width, height);
        if (aiCrop is { } c && (c.X < 0 || c.Y < 0 || c.Width < 2 || c.Width != c.Height ||
            c.X > width - c.Width || c.Y > height - c.Height))
            throw new ArgumentException("The AI crop must be a square inside the native photo.", nameof(aiCrop));
        if (maskInputSize is < 0 or 1) throw new ArgumentOutOfRangeException(nameof(maskInputSize));

        double full = 2 * calibratedHalfAngleDegrees;
        MaskEdgeScan Unavailable(string reason) => new(Thresholds.Select(p => new MaskEdgeFinding(p, null, reason, null)).ToArray(), [])
            { CalibratedFullDegrees = full };
        if (full >= 180) return Unavailable("Calibration already covers the scan ceiling.");
        if (!Endpoint(coefficients, calibratedHalfAngleDegrees, out double start, out double slope))
            return Unavailable("The lens fit has no positive outward slope at the calibration limit.");
        double cap = start + slope * ((90 - calibratedHalfAngleDegrees) * Math.PI / 180);
        if (!double.IsFinite(cap) || cap <= start) return Unavailable("The lens fit cannot resolve the 180° scan radius.");
        using var binary = DecodeBinary(maskPng, width, height, ct);
        double frame = Math.Min(Math.Min(centerX, width - 1 - centerX), Math.Min(centerY, height - 1 - centerY));
        double end = Math.Min(frame, cap);
        int step = Math.Min(width, height) >= 1000 ? 2 : 1;
        var rings = new List<MaskRingSample>();
        for (double r = start; r < end; r += step)
        {
            ct.ThrowIfCancellationRequested();
            rings.Add(Sample(binary, centerX, centerY, r, supportDisk, aiCrop, maskInputSize));
        }
        if (end >= start)
            rings.Add(Sample(binary, centerX, centerY, end, supportDisk, aiCrop, maskInputSize));
        bool reached = frame >= cap;
        var findings = Thresholds.Select(percent => Find(rings, percent, reached, start, slope, full)).ToArray();
        return new(Array.AsReadOnly(findings), rings.AsReadOnly())
            { CalibratedFullDegrees = full, LimitRadiusPixels = cap, Reached180 = reached };
    }

    /// <summary>Recognizes a re-encoded study mask so its known crop is retained during full-frame inspection.</summary>
    public static bool SameBinaryPixels(byte[] a, byte[] b, int width, int height, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ct.ThrowIfCancellationRequested();
        if (a.AsSpan().SequenceEqual(b)) return true;
        using var first = DecodeBinary(a, width, height, ct);
        using var second = DecodeBinary(b, width, height, ct);
        return Cv2.Norm(first, second, NormTypes.L1) == 0;
    }

    private static Mat DecodeBinary(byte[] png, int width, int height, CancellationToken ct)
    {
        // Check the native PNG header before decoding an optional full-frame mask.
        ReadOnlySpan<byte> header = png;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (header.Length < 33 || !header[..8].SequenceEqual(signature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Choose an opaque black-and-white PNG.");
        if (BinaryPrimitives.ReadUInt32BigEndian(header[16..]) != width || BinaryPrimitives.ReadUInt32BigEndian(header[20..]) != height)
            throw new InvalidDataException($"Mask dimensions must match the oriented photo ({width} × {height}).");
        using var source = Cv2.ImDecode(png, ImreadModes.Unchanged);
        if (source.Empty() || source.Width != width || source.Height != height || source.Depth() != MatType.CV_8U ||
            source.Channels() is not (1 or 3 or 4))
            throw new InvalidDataException("Choose a binary PNG with original photo dimensions and opaque black or white pixels.");
        var binary = new Mat(height, width, MatType.CV_8UC1);
        try { SkyMaskImporter.CopyBinaryPixels(source, binary, ct: ct); return binary; }
        catch { binary.Dispose(); throw; }
    }

    private static unsafe MaskRingSample Sample(Mat binary, double cx, double cy, double radius,
        LensDisk? disk, MaskCrop? crop, int inputSize)
    {
        byte* origin = (byte*)binary.Data;
        int stride = checked((int)binary.Step());
        int black = 0, observedBlack = 0, unknown = 0;
        foreach (var (sin, cos) in Bearings)
        {
            int x = (int)Math.Round(cx + radius * cos), y = (int)Math.Round(cy + radius * sin);
            bool dark = origin[(long)y * stride + x] == 0;
            if (dark) black++;
            // Mask import clips the native disk. AI also clips its cropped input and its
            // lower-resolution output circle, then restores it with nearest-neighbour sampling.
            if (!IsObservedPixel(x, y, disk, crop, inputSize)) unknown++;
            else if (dark) observedBlack++;
        }
        return new(radius, black, observedBlack, unknown);
    }

    private static bool Inside(double dx, double dy, double radius) => dx * dx + dy * dy <= radius * radius;

    public static bool IsObservedPixel(int x, int y, LensDisk? disk, MaskCrop? crop, int inputSize) =>
        (disk == null || Inside(x - disk.CenterX, y - disk.CenterY, disk.Radius)) &&
        (crop == null || InClassifier(x - crop.X, y - crop.Y, crop.Width, inputSize));

    private static bool InClassifier(int x, int y, int side, int inputSize)
    {
        int middle = (side - 1) / 2;
        if (!Inside(x - middle, y - middle, middle)) return false;
        if (inputSize == 0) return true;
        double modelRadius = (inputSize - 1) / 2d;
        int mx = (int)((long)x * inputSize / side), my = (int)((long)y * inputSize / side);
        return Inside(mx - modelRadius, my - modelRadius, modelRadius);
    }

    private static MaskEdgeFinding Find(List<MaskRingSample> rings, int percent, bool reached,
        double start, double slope, double full)
    {
        if (!reached)
            return new(percent, null, "The photo cuts off a complete circle before 180°. The cap is an extrapolation.", 180);
        int required = (percent * SamplesPerRing + 99) / 100;
        // Walk inward from the exact cap. Returning sky or insufficient evidence ends the suffix.
        double? observed = null;
        for (int i = rings.Count - 1; i >= 0 && rings[i].ObservedBlackSamples >= required; i--)
            observed = rings[i].RadiusPixels;
        if (observed == rings[^1].RadiusPixels)
            return new(percent, null, "Only the sampled circle at 180° meets the threshold. The scan stops at the cap.", 180);
        if (observed is double radius)
        {
            double degrees = Math.Clamp(full + 2 * (radius - start) / slope * 180 / Math.PI, full, 180);
            return new(percent, radius, "The sampled band meets this threshold through 180°. Obstructions can also produce it.", degrees);
        }
        var last = rings[^1];
        string reason = last.ObservedBlackSamples + last.UnknownSamples < required
            ? "Observed sky remains at 180°. The scan stops at the cap."
            : "Mask coverage at 180° is incomplete. The cap is an extrapolation.";
        return new(percent, null, reason, 180);
    }

    private static bool Endpoint(double[] coefficients, double halfDegrees, out double radius, out double slope)
    {
        radius = slope = 0;
        if (coefficients == null || coefficients.Length < 2 || Math.Abs(coefficients[0]) > 1e-9 ||
            !double.IsFinite(halfDegrees) || halfDegrees is <= 0 or > 180) return false;
        double radians = halfDegrees * Math.PI / 180;
        for (int i = coefficients.Length - 1; i >= 0; i--)
        {
            slope = slope * radians + radius;
            radius = radius * radians + coefficients[i];
        }
        return double.IsFinite(radius) && double.IsFinite(slope) && radius > 0 && slope > 0;
    }

}
