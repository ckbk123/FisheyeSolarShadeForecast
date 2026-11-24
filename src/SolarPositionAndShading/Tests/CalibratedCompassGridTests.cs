using OpenCvSharp;
using SolarShade.Core.Models;
using SolarShade.Shading;
using Xunit;

public sealed class CalibratedCompassGridTests
{
    private const double Radians = Math.PI / 180;
    private static CalibrationResult Profile(double limit) => new(2,
        CameraModelKind.OmniCalibIncidentAnglePolynomial, 701, 701, [312.25, 328.75], [0, 100, 10],
        limit, 320, null, 0, [], DateTimeOffset.UnixEpoch, "Synthetic nonlinear lens");

    [Theory]
    [InlineData(29.9, 0)]
    [InlineData(60, 2)]
    [InlineData(117.5, 3)]
    public void RingsUseCalibratedIncidentAnglesAboutTheFittedOpticalCenter(double limit, int expectedCount)
    {
        var profile = Profile(limit);
        var grid = CardinalDirectionOverlayGenerator.Generate(new(profile, new(0, -28, 34), new(340, 340, 310)));
        using var png = Cv2.ImDecode(grid.Png, ImreadModes.Unchanged);
        double cx = profile.PrincipalPoint[0], cy = profile.PrincipalPoint[1];
        Assert.True(grid.LensCenterVisible);
        Assert.True(InkNear(png, cx, cy, lens: true, radius: 2));
        Assert.Equal(expectedCount, grid.Rings.Count);
        for (int i = 1; i <= expectedCount; i++)
        {
            var ring = grid.Rings[i - 1];
            double angle = 30 * i * Radians, radius = 100 * angle + 10 * angle * angle;
            Assert.Equal(30 * i, ring.IncidentAngleDegrees);
            Assert.Equal(radius, ring.RadiusPixels, 9);
            Assert.True(ring.Visible);
            int hits = 0;
            for (int azimuth = 0; azimuth < 360; azimuth += 10)
            {
                var (sin, cos) = Math.SinCos(azimuth * Radians);
                if (InkNear(png, cx + radius * cos, cy + radius * sin, lens: true)) hits++;
            }
            // Labels, compass dots and the native raster can obscure individual points.
            Assert.InRange(hits, 25, 36);
        }
    }

    [Fact]
    public void AntialiasedRingEdgesBlendIntoTheDarkOutlineAtNativeResolution()
    {
        var grid = CardinalDirectionOverlayGenerator.Generate(new(Profile(66.43)));
        using var png = Cv2.ImDecode(grid.Png, ImreadModes.Unchanged);
        // Inspect the upper-left sector of the 30° ring, away from the two compass axes.
        // A faint cyan sample over a solid dark outline must retain some outline color.
        int mixed = 0;
        for (int y = 275; y < 310; y++)
        for (int x = 255; x < 293; x++)
        {
            var p = png.At<Vec4b>(y, x);
            if (p.Item3 >= 190 && p.Item1 is > 60 and < 190 && p.Item2 < 20) mixed++;
        }
        Assert.True(mixed > 0, "The antialiased ring edge should have intermediate cyan/outline colors.");
    }

    [Fact]
    public void OffsetImageDiskClipsRingsButKeepsFittedCenterVisibleForDiagnosis()
    {
        var profile = Profile(66.43) with { PrincipalPoint = [320, 300], IncidentAngleToRadiusPolynomial = [0, 100] };
        var grid = CardinalDirectionOverlayGenerator.Generate(new(profile, new(0, -28), new(420, 300, 75)));
        using var png = Cv2.ImDecode(grid.Png, ImreadModes.Unchanged);
        Assert.True(grid.LensCenterVisible);
        Assert.True(InkNear(png, 320, 300, lens: true, radius: 2));
        foreach (int angle in new[] { 30, 60 })
        {
            double radius = 100 * angle * Radians;
            Assert.True(InkNear(png, 320 + radius, 300, lens: true));
            Assert.False(InkNear(png, 320 - radius, 300, lens: true));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(34)]
    public void DottedEastWestMeridianFollowsTiltedRolledPhotoAcrossTheHorizon(double roll)
    {
        var profile = Profile(117.5) with { IncidentAngleToRadiusPolynomial = [0, 100] };
        var grid = CardinalDirectionOverlayGenerator.Generate(new(profile, new(0, -28, roll)));
        using var png = Cv2.ImDecode(grid.Png, ImreadModes.Unchanged);
        double cx = profile.PrincipalPoint[0], cy = profile.PrincipalPoint[1];
        var (tiltSin, tiltCos) = Math.SinCos(28 * Radians);
        var (rollSin, rollCos) = Math.SinCos(roll * Radians);
        // The lens points south of vertical. Zenith moves up the unrolled image by f * tilt.
        Assert.NotNull(grid.Zenith);
        Assert.Equal(cx - 100 * 28 * Radians * rollSin, grid.Zenith.X, 9);
        Assert.Equal(cy - 100 * 28 * Radians * rollCos, grid.Zenith.Y, 9);
        Assert.True(InkNear(png, grid.Zenith.X, grid.Zenith.Y, lens: false));
        foreach (int eastSign in new[] { -1, 1 })
        foreach (var (start, end) in new[] { (10, 80), (94, 114) })
        {
            int dots = 0, gaps = 0;
            for (double z = start; z <= end; z += .25)
            {
                var (sinZ, cosZ) = Math.SinCos(z * Radians);
                // Independent ENU projection of (±sin z, 0, cos z) into the camera.
                double right = -eastSign * sinZ, down = -tiltSin * cosZ;
                double incident = Math.Acos(tiltCos * cosZ);
                double factor = 100 * incident / double.Hypot(right, down);
                double x = cx + factor * (right * rollCos + down * rollSin);
                double y = cy + factor * (-right * rollSin + down * rollCos);
                if (InkNear(png, x, y, lens: false, radius: 0)) dots++;
                else gaps++;
            }
            Assert.True(dots > 8 && gaps > 8, $"Expected dotted coverage at roll {roll}°, zenith {start}–{end}°, sign {eastSign}. Found {dots} hits and {gaps} gaps.");
        }
    }

    [Fact]
    public void DottedMeridiansDoNotBridgeTheExcludedRearCap()
    {
        var profile = Profile(160) with { PrincipalPoint = [350, 350], IncidentAngleToRadiusPolynomial = [0, 100] };
        var grid = CardinalDirectionOverlayGenerator.Generate(new(profile, new(8, 90)));
        using var png = Cv2.ImDecode(grid.Png, ImreadModes.Unchanged);
        // A horizontal lens facing 8° east of north excludes part of the south meridian.
        // Both visible ends are on the 160° circle at the same x. A chord across the gap
        // would paint this central section, where neither true meridian passes.
        double cap = 160 * Radians, heading = 8 * Radians;
        double sinZ = -Math.Cos(cap) / Math.Cos(heading);
        double chordX = 350 - 100 * cap * Math.Sin(heading) * sinZ / Math.Sin(cap);
        for (int y = 315; y <= 385; y++)
            Assert.False(InkNear(png, chordX, y, lens: false));
    }

    private static bool InkNear(Mat png, double x, double y, bool lens, int radius = 1)
    {
        int column = (int)Math.Round(x), row = (int)Math.Round(y);
        for (int py = row - radius; py <= row + radius; py++)
        for (int px = column - radius; px <= column + radius; px++)
        {
            var p = png.At<Vec4b>(py, px);
            if (p.Item3 >= 25 && (lens ? p.Item1 > p.Item2 + 60 : p.Item2 > p.Item1 + 60)) return true;
        }
        return false;
    }
}
