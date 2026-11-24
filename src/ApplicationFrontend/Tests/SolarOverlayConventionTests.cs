using System.Globalization;
using SolarShade.Camera;
using SolarShade.Desktop;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;
using Xunit;

public sealed class SolarOverlayConventionTests
{
    // Independent NREL SPA directions, projected with the example lens and a south-facing 28° frame.
    // The first three cases cover the reported track from morning through afternoon.
    [Theory]
    [InlineData(10.8, 106.7, "2025-05-15T09:03:30+07:00", 49.16679, 73.53158, 972.60, 1536.00, 0.581203)]
    [InlineData(10.8, 106.7, "2025-05-15T11:49:30+07:00", 81.87660, 0.05912, 1530.41, 1492.26, 0.807774)]
    [InlineData(10.8, 106.7, "2025-05-15T15:00:30+07:00", 43.28271, 285.93668, 2157.25, 1551.48, 0.511735)]
    [InlineData(10.8, 106.7, "2025-12-21T11:51:14+07:00", 55.76106, 179.99896, 1530.52, 2087.05, 0.994099)]
    [InlineData(43.6045, 1.4442, "2025-05-15T13:50:34+02:00", 65.38597, 180.00242, 1530.55, 1949.13, 0.998246)]
    public void SunDirectionPanelIncidenceAndDisplayedPixelAgreeWithIndependentReference(
        double latitude, double longitude, string isoTime, double altitude, double bearing,
        double x, double y, double incidenceCosine)
    {
        var time = DateTimeOffset.Parse(isoTime, CultureInfo.InvariantCulture);
        var inputs = PortablePaths.Example();
        var data = IrradianceDatasets.FromSamples([new(time, 300, 80)], TimeSpan.FromMinutes(1),
            TimestampLabel.Center, "Reference case", "UTC", "One-minute centered interval");
        var timeline = new SolarPositionModule(new(latitude, longitude, 0)).PrepareIntervals(data, 1);
        var sample = Assert.Single(Assert.Single(timeline.Intervals).Samples);
        Assert.Equal(time, sample.Timestamp);
        Assert.InRange(Math.Abs((90 - sample.GeometricZenithDegrees) - altitude), 0, 0.02);
        Assert.InRange(Math.Abs(Math.IEEERemainder(sample.AzimuthDegrees - bearing, 360)), 0, 0.12);

        var panel = new PanelOrientation(inputs.PanelTilt, inputs.PanelAzimuth);
        var interval = Assert.Single(TranspositionModule.ComputePrepared(timeline, panel).Rows);
        Assert.InRange(Math.Abs(interval.Direct / interval.EffectiveDni - incidenceCosine), 0, 0.0003);

        var profile = CameraProfileFiles.Load(PortablePaths.Resolve(inputs.ProfilePath), 3000, 4000, inputs.CoverageAngle);
        var projection = new CalibratedSkyProjection(profile.Calibration, EquipmentOrientation.ToCameraPose(inputs));
        var track = SunPathOverlayGenerator.Generate(timeline, projection, TimeZoneInfo.Utc);
        var vertex = Assert.Single(track.Vertices);
        Assert.Equal(time, vertex.Timestamp);
        Assert.InRange(double.Hypot(vertex.PixelX - x, vertex.PixelY - y), 0, 0.3);
    }
}
