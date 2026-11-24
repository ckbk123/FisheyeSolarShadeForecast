using SolarShade.Core.Models;
using SolarShade.Shading;
using Xunit;

public sealed class CalibratedSkyProjectionTests
{
    private static CalibrationResult Profile(double coverage = 90) => new(2,
        CameraModelKind.OmniCalibIncidentAnglePolynomial, 501, 501, [250, 250], [0, 500 / Math.PI],
        coverage, 250, null, 0, [], DateTimeOffset.UnixEpoch, "Synthetic equidistant lens");

    [Fact]
    public void TheMeasuredProfileIsPreservedThroughTheJoinAndTheOuterGridFollowsTheTangent()
    {
        // This polynomial folds at 79.03°. Extending it directly to the horizon is invalid.
        var measured = Profile(60) with { IncidentAngleToRadiusPolynomial = [0, 160, -58] };
        var effective = CalibrationRadialMap.ExtendTo(measured, 90);
        var reference = new CalibratedSkyProjection(measured);
        var projected = new CalibratedSkyProjection(effective);
        foreach (double zenith in new[] { 0d, 12, 30, 51, 59.9999, 60 })
        {
            var ray = Direction.FromAngles(90, zenith);
            Assert.True(reference.TryProject(ray, out double x, out double y));
            Assert.True(projected.TryProject(ray, out double extendedX, out double extendedY));
            Assert.Equal(x, extendedX); Assert.Equal(y, extendedY);
        }
        double join = Math.PI / 3, joinRadius = 160 * join - 58 * join * join, slope = 160 - 116 * join;
        double horizonRadius = joinRadius + slope * Math.PI / 6;
        Assert.True(projected.TryProject(Direction.FromAngles(270, 90), out var horizonX, out var horizonY));
        Assert.Equal(250 + horizonRadius, horizonX, 10); Assert.Equal(250, horizonY, 10);
        Assert.False(projected.TryProject(Direction.FromAngles(270, 90.0001), out _, out _));
        var overlay = CardinalDirectionOverlayGenerator.Generate(projected);
        Assert.Equal(60, overlay.Projection.LinearExtensionStartDegrees);
        Assert.Equal(horizonRadius, Assert.Single(overlay.Rings, r => r.IncidentAngleDegrees == 90).RadiusPixels, 10);
        Assert.Null(measured.LinearExtensionStartDegrees);
        Assert.Throws<ArgumentException>(() => new CalibratedSkyProjection(measured with { MaximumIncidentAngleDegrees = 90 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationRadialMap.ExtendTo(measured, 135.1));
    }

    [Fact]
    public void A270DegreeFieldUsesTheTangentBehindTheLensAndBreaksPathsAcrossTheRearCap()
    {
        var measured = Profile(60) with { IncidentAngleToRadiusPolynomial = [0, 160, -58] };
        var projection = new CalibratedSkyProjection(CalibrationRadialMap.WithApparentFullFov(measured, 270));
        const double join = Math.PI / 3;
        double joinRadius = 160 * join - 58 * join * join;
        double slope = 160 - 116 * join;
        foreach (double incident in new[] { 90d, 120, 135 })
        {
            Assert.True(projection.TryProject(Direction.FromAngles(270, incident), out var x, out var y));
            Assert.Equal(250 + joinRadius + (incident * Math.PI / 180 - join) * slope, x, 10);
            Assert.Equal(250, y, 10);
        }
        Assert.False(projection.TryProject(Direction.FromAngles(270, 135.001), out _, out _));
        Assert.False(projection.CanConnectProjectedRays(Direction.FromAngles(90, 120), Direction.FromAngles(270, 120)));
        Assert.True(projection.CanConnectProjectedRays(Direction.FromAngles(90, 120), Direction.FromAngles(95, 120)));
        var grid = CardinalDirectionOverlayGenerator.Generate(projection);
        Assert.Equal(joinRadius + join * slope, Assert.Single(grid.Rings, ring => ring.IncidentAngleDegrees == 120).RadiusPixels, 10);
    }

    [Theory]
    [InlineData(0, 250, 125)]
    [InlineData(90, 125, 250)]
    [InlineData(180, 250, 375)]
    [InlineData(270, 375, 250)]
    public void DefaultCardinalDirectionsMatchAnalyticalEquidistantPixels(double azimuth, double expectedX, double expectedY)
    {
        var projection = new CalibratedSkyProjection(Profile());
        Assert.Equal(501, projection.Width); Assert.Equal(501, projection.Height);
        Assert.True(projection.TryProject(Direction.FromAngles(azimuth, 45), out var x, out var y));
        Assert.Equal(expectedX, x, 10); Assert.Equal(expectedY, y, 10);
    }

    [Fact]
    public void HeadingTiltAndRollUseTheSameCameraConvention()
    {
        var heading = new CalibratedSkyProjection(Profile(), CameraPose.FromImageBottom(270));
        Assert.True(heading.TryProject(Direction.FromAngles(0, 45), out var x, out var y));
        Assert.Equal(375, x, 10); Assert.Equal(250, y, 10);
        var tilted = new CalibratedSkyProjection(Profile(), new(0, 30));
        Assert.True(tilted.TryProject(Direction.FromAngles(0, 30), out x, out y));
        Assert.Equal(250, x, 10); Assert.Equal(250, y, 10);
        var rolled = new CalibratedSkyProjection(Profile(), new(0, 0, 90));
        Assert.True(rolled.TryProject(Direction.FromAngles(90, 45), out x, out y));
        Assert.Equal(250, x, 10); Assert.Equal(375, y, 10);
    }

    [Fact]
    public void CoverageDiskRectangleAndAntipodalAxisAreDistinctLimits()
    {
        var limited = new CalibratedSkyProjection(Profile(66));
        Assert.True(limited.TryProject(Direction.FromAngles(90, 65.999), out _, out _));
        Assert.False(limited.TryProject(Direction.FromAngles(90, 66.001), out _, out _));
        var offsetDisk = new CalibratedSkyProjection(Profile(), imageDisk: new(280, 250, 20));
        Assert.False(offsetDisk.TryProject(new(0, 0, 1), out _, out _));
        Assert.True(offsetDisk.TryProject(Direction.FromAngles(270, 10.8), out var x, out var y));
        Assert.Equal(280, x, 10); Assert.Equal(250, y, 10);
        var rectangular = new CalibratedSkyProjection(Profile() with
        { ImageWidth = 100, ImageHeight = 60, PrincipalPoint = [50, 30], IncidentAngleToRadiusPolynomial = [0, 100], ImageCircleRadiusPixels = 1000 });
        Assert.False(rectangular.TryProject(Direction.FromAngles(270, 30), out _, out _));
        var fullAngle = new CalibratedSkyProjection(Profile(180) with { IncidentAngleToRadiusPolynomial = [0, 50] });
        Assert.False(fullAngle.TryProject(new(0, 0, -1), out _, out _));
        Assert.True(fullAngle.TryProject(Direction.FromAngles(90, 110), out x, out y));
        Assert.True(x < 250); Assert.Equal(250, y, 10);
    }

    [Fact]
    public void FullShadingAndLightweightProjectionAgreeForPosesAndCoverage()
    {
        var profile = Profile(110);
        var mask = new SkyMask(profile.ImageWidth, profile.ImageHeight, new byte[profile.ImageWidth * profile.ImageHeight]);
        foreach (var pose in new[] { new CameraPose(), new(72, 35, -24), new(235, -45, 120) })
        {
            var disk = new ImageDisk(246, 254, 239);
            var lightweight = new CalibratedSkyProjection(profile, pose, disk);
            var shading = new SkyShadingModule(mask, profile, pose,
                new() { DiskSamples = 8, DiffuseSamples = 64 }, disk);
            for (int azimuth = 0; azimuth < 360; azimuth += 7)
            for (int zenith = 0; zenith <= 180; zenith += 5)
            {
                var ray = Direction.FromAngles(azimuth, zenith);
                Assert.Equal(shading.TryProject(ray, out var sx, out var sy), lightweight.TryProject(ray, out var x, out var y));
                Assert.Equal(sx, x); Assert.Equal(sy, y);
            }
        }
    }

    [Fact]
    public void CallerArrayMutationCannotChangePreparedProjection()
    {
        var profile = Profile(); var projection = new CalibratedSkyProjection(profile);
        var ray = Direction.FromAngles(90, 45);
        Assert.True(projection.TryProject(ray, out var firstX, out var firstY));
        profile.PrincipalPoint[0] = -1e8;
        profile.IncidentAngleToRadiusPolynomial[1] = -500;
        Assert.True(projection.TryProject(ray, out var x, out var y));
        Assert.Equal(firstX, x); Assert.Equal(firstY, y);
    }

    [Fact]
    public void InvalidProfilesDisksAndPosesRetainShadingValidation()
    {
        var profile = Profile();
        foreach (var invalid in new[]
        {
            profile with { ImageWidth = 1 },
            profile with { PrincipalPoint = [0] },
            profile with { PrincipalPoint = [double.NaN, 250] },
            profile with { IncidentAngleToRadiusPolynomial = [1e-8, 100] },
            profile with { IncidentAngleToRadiusPolynomial = [0, 100, -100] },
            profile with { MaximumIncidentAngleDegrees = 0 },
            profile with { MaximumIncidentAngleDegrees = double.NaN },
            profile with { ImageCircleRadiusPixels = 0 }
        }) Assert.Throws<ArgumentException>(() => new CalibratedSkyProjection(invalid));
        Assert.Throws<ArgumentException>(() => new CalibratedSkyProjection(profile, imageDisk: new(double.NaN, 250, 200)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalibratedSkyProjection(profile, new(0, 181)));
    }

    [Fact]
    public void ProjectionHotPathDoesNotAllocate()
    {
        var projection = new CalibratedSkyProjection(Profile());
        var ray = Direction.FromAngles(90, 45);
        projection.TryProject(ray, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0;
        for (int i = 0; i < 100_000; i++)
            if (projection.TryProject(ray, out var x, out var y)) checksum += x + y;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(checksum > 0); Assert.Equal(0, bytes);
    }

    [Theory] [InlineData(180)] [InlineData(179.95)]
    public void ValidEndpointsCannotBridgeTheRearAxisOrAnUnsampledExcludedCap(double coverage)
    {
        var projection = new CalibratedSkyProjection(Profile(coverage) with
            { IncidentAngleToRadiusPolynomial = [0, 50] }, new(0, 180));
        var before = Direction.FromAngles(90, .1);
        var after = Direction.FromAngles(270, .1);
        Assert.True(projection.TryProject(before, out _, out _));
        Assert.True(projection.TryProject(after, out _, out _));
        Assert.False(projection.CanConnectProjectedRays(before, after));
        Assert.False(projection.CanConnectProjectedRays(after, before));
        var sameSide = Direction.FromAngles(90, .2);
        Assert.True(projection.TryProject(sameSide, out _, out _));
        Assert.True(projection.CanConnectProjectedRays(before, sameSide));
        Assert.True(projection.CanConnectProjectedRays(before, before));
    }

    [Fact]
    public void RearCapGuardDoesNotBreakContinuousNonSingularPaths()
    {
        var projection = new CalibratedSkyProjection(Profile(120) with
            { IncidentAngleToRadiusPolynomial = [0, 50] }, new(0, 150));
        var first = Direction.FromAngles(90, 80);
        var second = Direction.FromAngles(95, 80);
        Assert.True(projection.TryProject(first, out _, out _));
        Assert.True(projection.TryProject(second, out _, out _));
        Assert.True(projection.CanConnectProjectedRays(first, second));
        Assert.True(projection.CanConnectProjectedRays(second, first));

        var conventional = new CalibratedSkyProjection(Profile(90));
        Assert.True(conventional.CanConnectProjectedRays(Direction.FromAngles(20, 80), Direction.FromAngles(220, 80)));
    }
}
