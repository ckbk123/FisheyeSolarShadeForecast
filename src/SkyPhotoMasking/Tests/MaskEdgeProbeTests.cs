using OpenCvSharp;
using SolarShade.SkyPhotoMasking;
using Xunit;

public sealed class MaskEdgeProbeTests
{
    private const int Width = 257, Height = 241;
    private const double CenterX = 121, CenterY = 112;

    private static byte[] Mask(Func<double, double, bool> isSky, int channels = 1)
    {
        var pixels = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double dx = x - CenterX, dy = y - CenterY;
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (angle < 0) angle += 360;
            pixels[y * Width + x] = isSky(double.Hypot(dx, dy), angle) ? (byte)255 : (byte)0;
        }
        using var gray = Mat.FromPixelData(Height, Width, MatType.CV_8UC1, pixels);
        if (channels == 1) return gray.ToBytes(".png");
        using var color = new Mat();
        Cv2.CvtColor(gray, color, channels == 3 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.GRAY2BGRA);
        return color.ToBytes(".png");
    }

    private static MaskEdgeScan Scan(byte[] mask, LensDisk? disk = null, MaskCrop? crop = null) =>
        ScanTo180(mask, halfAngle: 35, pixelsPerDegree: 1.2, disk: disk, crop: crop);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void OpaqueBinaryMasksFindTheOuterTransitionAfterAnInteriorDarkBand(int channels)
    {
        // The dark band lasts long enough to pass a persistence check, but sky returns outside it.
        var findings = Scan(Mask((r, _) => r < 53 || r is >= 63 and < 87, channels)).Findings;
        Assert.Equal(new[] { 80, 90, 95, 100 }, findings.Select(f => f.BlackPercent));
        Assert.All(findings, f => Assert.InRange(Assert.IsType<double>(f.RadiusPixels), 85, 89));
    }

    [Fact]
    public void AngularSectorsDistinguishMajorityFromCompleteDarkness()
    {
        // Beyond each boundary the surviving white arc narrows to 15%, 7%, 2%, then zero.
        var result = Scan(Mask((r, angle) => r < 59 || (r < 72 && angle < 54)
            || (r < 84 && angle < 25) || (r < 97 && angle < 8)));
        double[] expected = [59, 72, 84, 97];
        Assert.Equal(expected.Length, result.Findings.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Assert.IsType<double>(result.Findings[i].RadiusPixels), expected[i] - 2, expected[i] + 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArtificialBlackOutsideAnImportedDiskOrAiCropCannotConfirmAnEdge(bool aiCrop)
    {
        const int edge = 78;
        // Both masks have the same manufactured black circle. In the AI case the inference
        // window is smaller than the detected disk, so the disk alone cannot identify padding.
        var disk = new LensDisk(CenterX, CenterY, aiCrop ? 110 : edge, Width, Height);
        var crop = aiCrop ? new MaskCrop((int)CenterX - edge, (int)CenterY - edge, 2 * edge + 1, 2 * edge + 1) : null;
        var findings = Scan(Mask((r, _) => r <= edge), disk, crop).Findings;
        Assert.All(findings, f => Assert.Null(f.RadiusPixels));
    }

    [Fact]
    public void AThinDarkBandTouchingTheSupportLimitCannotVerifyPersistence()
    {
        var disk = new LensDisk(CenterX, CenterY, 105, Width, Height);
        Assert.All(Scan(Mask((r, _) => r < 104.5), disk).Findings, f => Assert.Null(f.RadiusPixels));
    }

    [Fact]
    public void ClassifierPaddingMatchesTheActualNearestNeighbourRestoration()
    {
        const int side = 157, inputSize = 32, radius = (side - 1) / 2;
        var crop = new MaskCrop((int)CenterX - radius, (int)CenterY - radius, side, side);
        using var predicted = SkyPhotoMasker.Threshold(new float[inputSize * inputSize], inputSize, SkyModel.EfficientNetB5);
        using var restored = new Mat();
        Cv2.Resize(predicted, restored, new Size(side, side), interpolation: InterpolationFlags.Nearest);
        using var cropCircle = new Mat(side, side, MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(cropCircle, new Point(radius, radius), radius, Scalar.White, -1);
        // Set pixels outside the native input circle to black even where nearest resampling spills over it.
        using var mask = new Mat(Height, Width, MatType.CV_8UC1, Scalar.Black);
        using var roi = new Mat(mask, new Rect(crop.X, crop.Y, side, side));
        restored.CopyTo(roi, cropCircle);
        var scan = MaskEdgeProbe.AnalyzeTo180(mask.ToBytes(".png"), Width, Height, CenterX, CenterY,
            [0, 180 / Math.PI], 42, null, crop, maskInputSize: inputSize);
        Assert.Contains(scan.Samples, ring => ring.RadiusPixels < radius && ring.UnknownSamples > 0);
        Assert.All(scan.Samples, ring =>
        {
            Assert.Equal(0, ring.ObservedBlackSamples);
            Assert.Equal(ring.BlackSamples, ring.UnknownSamples);
        });
    }

    [Fact]
    public void AClippedPhotoDoesNotCountMissingPixelsAsBlack()
    {
        byte[] mask = Mask((_, _) => true);
        var result = MaskEdgeProbe.AnalyzeTo180(mask, Width, Height, 5, CenterY, [0, 180 / Math.PI], 42, null, null);
        Assert.All(result.Findings, f => Assert.Null(f.RadiusPixels));
    }

    [Theory]
    [InlineData(1, 16, 16, 16, 255)]
    [InlineData(3, 0, 255, 255, 255)]
    [InlineData(4, 255, 255, 255, 128)]
    public void InvalidMaskValuesAreRejectedEvenAwayFromTheSampledRings(int channels, int b, int g, int r, int a)
    {
        MatType type = channels switch { 1 => MatType.CV_8UC1, 3 => MatType.CV_8UC3, _ => MatType.CV_8UC4 };
        using var mask = new Mat(Height, Width, type, new Scalar(255, 255, 255, 255));
        using var onePixel = new Mat(mask, new Rect((int)CenterX, (int)CenterY, 1, 1));
        onePixel.SetTo(new Scalar(b, g, r, a));
        Assert.Throws<InvalidDataException>(() => Scan(mask.ToBytes(".png")));
    }

    [Fact]
    public void RejectsMaskDimensionMismatchAndHonorsCancellation()
    {
        byte[] png = Mask((r, _) => r < 78);
        Assert.Throws<InvalidDataException>(() =>
            MaskEdgeProbe.AnalyzeTo180(png, Width + 1, Height, CenterX, CenterY, [0, 180 / Math.PI], 42, null, null));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MaskEdgeProbe.AnalyzeTo180(png, Width, Height, CenterX, CenterY, [0, 180 / Math.PI], 42, null, null, new CancellationToken(true)));
    }

    [Fact]
    public void BundledLensUsesThePositiveEndpointTangentBeyondThePolynomialsTurningPoint()
    {
        double[] coefficients = [0, 824.9346460936491, -47.154450106730124, 90.17908124542645, -116.02812744232502];
        // Scale the native demo (823.94 px measured radius) to this image. A rim at 89 px
        // lies beyond the scaled quartic's 86.48 px turning point.
        var scaled = coefficients.Select(value => value / 10).ToArray();
        var scan = MaskEdgeProbe.AnalyzeTo180(Mask((r, _) => r < 89), Width, Height, CenterX, CenterY,
            scaled, 66.43, null, null);
        Assert.Equal(97.03565, Assert.IsType<double>(scan.LimitRadiusPixels), 3);
        var band = Assert.Single(scan.Findings, f => f.BlackPercent == 100);
        double radius = Assert.IsType<double>(band.RadiusPixels);
        Assert.InRange(radius, 89, 91);
        double expected = 2 * (66.43 + (radius - 82.394281861) / .621186699);
        Assert.Equal(expected, Assert.IsType<double>(band.EstimatedLimitFullDegrees), 5);
    }

    // Linear fixture: at unit scale, native pixel radius equals incident angle in degrees.
    private static MaskEdgeScan ScanTo180(byte[] mask, double halfAngle = 60, double pixelsPerDegree = 1,
        LensDisk? disk = null, MaskCrop? crop = null) => MaskEdgeProbe.AnalyzeTo180(mask, Width, Height,
            CenterX, CenterY, [0, pixelsPerDegree * 180 / Math.PI], halfAngle, disk, crop);

    [Fact]
    public void TheLastBlackBandMustContinueThroughTheFull180DegreeCap()
    {
        var solidRim = ScanTo180(Mask((r, _) => r < 80));
        var skyReturns = ScanTo180(Mask((r, _) => r < 80 || r >= 86));
        var laterRim = ScanTo180(Mask((r, _) => r < 68 || r is >= 78 and < 85));

        Assert.False(solidRim.AlreadyCovers180);
        Assert.True(solidRim.Reached180);
        Assert.Equal(120, solidRim.CalibratedFullDegrees, 8);
        Assert.Equal(90, Assert.IsType<double>(solidRim.LimitRadiusPixels), 8);
        Assert.All(solidRim.Findings, f => Assert.InRange(Assert.IsType<double>(f.EstimatedLimitFullDegrees), 158, 162));
        Assert.All(skyReturns.Findings, f =>
        {
            Assert.Null(f.RadiusPixels);
            Assert.Equal(180, Assert.IsType<double>(f.EstimatedLimitFullDegrees), 8);
        });
        Assert.All(laterRim.Findings, f => Assert.InRange(Assert.IsType<double>(f.EstimatedLimitFullDegrees), 168, 172));
    }

    [Theory]
    [InlineData(true, 120)]
    [InlineData(false, 180)]
    public void OnlyTheMaskInsideTheFractionalCapCanAffectTheResult(bool blackInside, double expected)
    {
        // The cap falls between radial steps. Samples up to 0.71 px away can round onto the adjacent pixel.
        const double scale = 1.043, cap = 90 * scale;
        var result = ScanTo180(Mask((r, _) => blackInside ? r > cap + 1 : r < cap + 1), pixelsPerDegree: scale);
        Assert.True(result.Reached180);
        Assert.Equal(cap, result.Samples[^1].RadiusPixels, 8);
        Assert.All(result.Samples, s => Assert.True(s.RadiusPixels <= cap + 1e-9));
        Assert.All(result.Findings, f => Assert.Equal(expected, Assert.IsType<double>(f.EstimatedLimitFullDegrees), 8));
    }

    [Fact]
    public void ARemainingSkySectorAtTheCapSeparatesMajorityFromTotalBlack()
    {
        // Seven percent of bearings stay open from the 76 px boundary through the horizon.
        var result = ScanTo180(Mask((r, bearing) => r < 76 || bearing < 25));
        foreach (var finding in result.Findings)
        {
            double full = Assert.IsType<double>(finding.EstimatedLimitFullDegrees);
            if (finding.BlackPercent <= 90) Assert.InRange(full, 150, 154);
            else
            {
                Assert.Equal(180, full, 8);
                Assert.Null(finding.RadiusPixels);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOuterRingsCannotConfirmAnOtherwiseBlackSuffix(bool aiCrop)
    {
        const int radius = 86;
        var disk = aiCrop ? null : new LensDisk(CenterX, CenterY, radius, Width, Height);
        var crop = aiCrop ? new MaskCrop((int)CenterX - radius, (int)CenterY - radius, 2 * radius + 1, 2 * radius + 1) : null;
        var scan = ScanTo180(Mask((r, _) => r < 74), disk: disk, crop: crop);
        var strict = Assert.Single(scan.Findings, f => f.BlackPercent == 100);
        Assert.Contains(scan.Samples, s => s.UnknownSamples == MaskEdgeProbe.SamplesPerRing);
        Assert.Null(strict.RadiusPixels);
        Assert.Equal(180, Assert.IsType<double>(strict.EstimatedLimitFullDegrees), 8);
        Assert.False(string.IsNullOrWhiteSpace(strict.Explanation));
    }

    [Fact]
    public void ATerminalBlackBandCutOffByTheFrameDoesNotCertifyASmallerLimit()
    {
        // The photograph ends at radius 112. The 180° target lies at 126 px under this fit.
        var result = ScanTo180(Mask((r, _) => r < 96), pixelsPerDegree: 1.4);
        Assert.False(result.Reached180);
        Assert.Equal(126, Assert.IsType<double>(result.LimitRadiusPixels), 8);
        Assert.InRange(result.Samples[^1].RadiusPixels, 110, 112.0000001);
        Assert.All(result.Findings, f =>
        {
            Assert.Null(f.RadiusPixels);
            Assert.Equal(180, Assert.IsType<double>(f.EstimatedLimitFullDegrees), 8);
            Assert.False(string.IsNullOrWhiteSpace(f.Explanation));
        });
    }

    [Theory]
    [InlineData(90)]
    [InlineData(117.5)]
    public void AnAlreadyCalibratedHemisphericalOrWiderFieldIsNeverNarrowed(double halfAngle)
    {
        var result = ScanTo180(Mask((_, _) => false), halfAngle: halfAngle);
        Assert.True(result.AlreadyCovers180);
        Assert.Equal(2 * halfAngle, result.CalibratedFullDegrees, 8);
        Assert.All(result.Findings, f => Assert.Null(f.EstimatedLimitFullDegrees));
    }

    [Fact]
    public void ANonpositiveEndpointSlopeGivesNoAngularSearchLimit()
    {
        var result = MaskEdgeProbe.AnalyzeTo180(Mask((_, _) => false), Width, Height, CenterX, CenterY,
            [0, 100, -60], 60, null, null);
        Assert.False(result.Reached180);
        Assert.Null(result.LimitRadiusPixels);
        Assert.All(result.Findings, f => Assert.Null(f.EstimatedLimitFullDegrees));
    }
}
