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
}
