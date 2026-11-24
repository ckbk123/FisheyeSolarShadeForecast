using SolarShade.Core.Models;
using SolarShade.Shading;
using Xunit;

public sealed class PanelVisibilityTests
{
    private static PanelSkyScene Scene(bool open, double coverage = 90)
    {
        const int n = 501;
        var pixels = Enumerable.Repeat(open ? (byte)255 : (byte)0, n * n).ToArray();
        var calibration = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
            n, n, [250, 250], [0, 500 / Math.PI], coverage, 250, null, 0, [], DateTimeOffset.UtcNow, "Synthetic");
        return new(new(n, n, pixels), calibration, new(), new(250, 250, 250));
    }
    [Theory][InlineData(0)][InlineData(60)][InlineData(90)]
    public void OpenAndBlockedMasksGivePhysicalReceiverBounds(double tilt)
    {
        var normal = Direction.FromAngles(90, tilt);
        var open = Scene(true); var blocked = Scene(false);
        Assert.InRange(open.Dome(normal).Visible, .999, 1);
        Assert.Equal(0, blocked.Dome(normal).Visible);
        var sun = Direction.FromAngles(90, 45);
        Assert.Equal(1, open.Disk(sun, normal).Visible, 10);
        Assert.Equal(0, blocked.Disk(sun, normal).Visible);
    }
    [Fact] public void UnobservedSkyIsSeparateFromObstruction()
    {
        var scene = Scene(true, 50); var normal = new Direction(0, 0, 1);
        var dome = scene.Dome(normal);
        Assert.InRange(dome.Known, .55, .65); Assert.Equal(dome.Known, dome.Visible, 10);
        var outside = scene.Disk(Direction.FromAngles(90, 70), normal);
        Assert.Equal(0, outside.Known); Assert.Equal(0, outside.Visible);
    }
    [Fact] public void GrazingReceiverAndHorizonAreClipped()
    {
        var scene = Scene(true);
        var normal = Direction.FromAngles(90, 90);
        var tangent = scene.Disk(Direction.FromAngles(0, 80), normal);
        Assert.InRange(tangent.Visible, .999, 1);
        var below = scene.Disk(Direction.FromAngles(90, 100), new(0, 0, 1));
        Assert.Equal(0, below.Visible); Assert.Equal(0, below.Known);
    }
    [Fact] public void InvalidDirectionsCannotPoisonMomentCache()
    {
        var scene = Scene(true);
        Assert.Throws<ArgumentException>(() => scene.Moments(new(2, 0, 0)));
        Assert.Equal(0, scene.CachedSolarDirections);
    }

    [Fact]
    public void ClassifierPaddingIsUnknownOnlyOnTheExtensionIncludingDisksThatStraddleTheJoin()
    {
        var measured = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
            501, 501, [250, 250], [0, 150], 60, 250, null, 0, [], DateTimeOffset.UnixEpoch, "Synthetic");
        var effective = CalibrationRadialMap.ExtendTo(measured, 90);
        var mask = new SkyMask(501, 501, Enumerable.Repeat((byte)255, 501 * 501).ToArray());
        static bool Observed(int x, int y) => x >= 250;
        var scene = new PanelSkyScene(mask, effective, new(), new(250, 250, 250), isObservedExtensionPixel: Observed);
        var normal = new Direction(0, 0, 1);
        Assert.Equal(new FluxVisibility(1, 1), scene.Disk(Direction.FromAngles(90, 45), normal));
        Assert.Equal(new FluxVisibility(0, 0), scene.Disk(Direction.FromAngles(90, 75), normal));
        Assert.Equal(new FluxVisibility(1, 1), scene.Disk(Direction.FromAngles(270, 75), normal));
        var horizonJoin = scene.Disk(Direction.FromAngles(90, 60), normal);
        Assert.InRange(horizonJoin.Known, .4, .6); Assert.Equal(horizonJoin.Known, horizonJoin.Visible);
        // The inner cone contributes sin²(60°) = .75. Half the outer annulus is observed.
        Assert.InRange(scene.Dome(normal).Known, .874, .876);

        var site = new SolarSite(0, 0, 0);
        var angles = new[] { new SolarAngles(90, 45), new SolarAngles(90, 60), new SolarAngles(90, 75), new SolarAngles(270, 75) };
        var sequence = new SolarSequence(site, TimeSampling.Instant, angles.Select(a =>
            new SolarRow(DateTimeOffset.UnixEpoch, 100, a, [a.Direction])).ToArray());
        foreach (bool shortcut in new[] { true, false })
        {
            var module = new SkyShadingModule(mask, effective, options: new() { UseUniformRegionShortcut = shortcut,
                DiskSamples = 256, DiffuseSamples = 8192 }, isObservedExtensionPixel: Observed);
            var output = module.Evaluate(sequence);
            Assert.Equal(1, output.Rows[0].Direct.Coverage, 10);
            Assert.InRange(output.Rows[1].Direct.Coverage, .4, .6);
            Assert.Equal(0, output.Rows[2].Direct.Coverage);
            Assert.Equal(1, output.Rows[3].Direct.Coverage, 10);
            Assert.InRange(output.Diffuse.Coverage, .874, .876);
        }
    }
}
