using System.Text.Json;
using SolarShade.Core.Models;
using SolarShade.Desktop;
using SolarShade.Shading;
using Xunit;

public sealed class EquipmentOrientationTests
{
    private static CalibratedSkyProjection Projection(UserSettings settings)
    {
        var calibration = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
            501, 501, [250, 250], [0, 500 / Math.PI], 90, 250, null, 0, [], DateTimeOffset.UnixEpoch, "Equidistant lens");
        return new(calibration, EquipmentOrientation.ToCameraPose(settings));
    }

    [Fact]
    public void BundledExampleShowsTheSouthFacingUphillPlacement()
    {
        var settings = PortablePaths.Example();
        var angles = EquipmentOrientation.Describe(settings);
        Assert.Equal(28, settings.PanelTilt);
        Assert.Equal(28, settings.PhoneInclination);
        Assert.Equal(0, settings.PanelTopHeading);
        Assert.Equal(0, settings.PhoneTopHeading);
        Assert.Equal(0, angles.PanelAzimuth);
        Assert.Equal(0, angles.PhoneAzimuth);
        Assert.Equal("Lens end uphill", angles.PhonePlacement);
        var (right, down, lens) = EquipmentOrientation.ToCameraPose(settings).Axes();
        var southFacingNormal = Direction.FromAngles(180, 28);
        Assert.Equal(1, lens.Dot(southFacingNormal), 12);
        Assert.True(down.Up < 0 && down.North < 0 && right.East < 0);
    }

    [Theory]
    [InlineData(0, 180, 0)]
    [InlineData(90, 270, 90)]
    [InlineData(-90, 90, -90)]
    [InlineData(270, 90, -90)]
    [InlineData(-180, 0, 180)]
    [InlineData(180, 0, 180)]
    [InlineData(-26, 154, -26)]
    public void SamePanelAndPhoneInputsCenterTheSunAtThePanelNormal(double facing, double bearing, double canonical)
    {
        var settings = new UserSettings { PanelTilt = 28, PanelFacingAzimuth = facing, PhoneInclination = 28, PhoneFacingAzimuth = facing };
        var angles = EquipmentOrientation.Describe(settings);
        Assert.Equal(canonical, angles.PanelAzimuth);
        Assert.Equal(canonical, angles.PhoneAzimuth);
        Assert.Equal(bearing, settings.PanelAzimuth);
        Assert.Equal(-28, settings.CameraTilt);
        Assert.Equal(bearing, settings.BottomAzimuth);
        Assert.Equal(0, settings.CameraRoll);
        Assert.True(angles.IsVincentPhonePose);
        Assert.Equal("Lens end uphill", angles.PhonePlacement);
        var incidentSun = Direction.FromAngles(bearing, 28);
        Assert.True(Projection(settings).TryProject(incidentSun, out var x, out var y));
        Assert.Equal(250, x, 10);
        Assert.Equal(250, y, 10);
    }

    [Fact]
    public void FlatPhoneRotatesTheImageWithNoOpticalOrPanelBearing()
    {
        var settings = new UserSettings { PanelTilt = 0, CameraRoll = 90 };
        var before = EquipmentOrientation.Describe(settings);
        Assert.True(before.IsVincentPhonePose);
        Assert.Null(before.PanelAzimuth);
        Assert.Null(before.PhoneAzimuth);
        Assert.Equal(270, before.ImageTopBearing!.Value, 10);
        settings.FlatImageTopBearing = 90;
        Assert.Equal(90, settings.FlatImageTopBearing, 10);
        var projected = Projection(settings);
        Assert.True(projected.TryProject(Direction.FromAngles(0, 45), out var x, out var y));
        Assert.Equal(375, x, 10);
        Assert.Equal(250, y, 10);
        Assert.Throws<InvalidOperationException>(() => settings.PhoneFacingAzimuth = 0);
        settings.PhoneInclination = 28;
        Assert.Equal(90, settings.PhoneFacingAzimuth);
        Assert.Equal(0, settings.CameraRoll);
        Assert.Equal(90, EquipmentOrientation.Describe(settings).ImageTopBearing!.Value, 10);
        settings.PhoneInclination = 0;
        Assert.Null(EquipmentOrientation.Describe(settings).PhoneAzimuth);
        Assert.Equal(90, settings.FlatImageTopBearing, 10);
    }

    [Theory]
    [InlineData(35, 180, true)]
    [InlineData(-35, 20, true)]
    [InlineData(35, -24, false)]
    [InlineData(-35, 90, false)]
    [InlineData(-115, 17, false)]
    [InlineData(180, 180, false)]
    public void LegacyPoseAndItsFingerprintSurviveLoadingViewingAndResaving(double tilt, double roll, bool supported)
    {
        string oldJson = JsonSerializer.Serialize(new { BottomAzimuth = 252, CameraTilt = tilt, CameraRoll = roll, PanelAzimuth = 0, PanelTilt = 30 });
        var loaded = JsonSerializer.Deserialize<UserSettings>(oldJson)!;
        string key = AppData.Key(loaded);
        var expected = CameraPose.FromImageBottom(252, tilt, roll);
        var angles = EquipmentOrientation.Describe(loaded);
        loaded.PhoneTopHeading = loaded.PhoneTopHeading;
        loaded.PanelTopHeading = loaded.PanelTopHeading;
        Assert.Equal(supported, angles.IsVincentPhonePose);
        Assert.Equal(Math.Abs(tilt), angles.PhoneTilt);
        Assert.Equal(180, angles.PanelAzimuth);
        if (tilt == 180) Assert.Null(angles.PhoneAzimuth);
        else loaded.PhoneFacingAzimuth = angles.PhoneAzimuth!.Value;
        if (Math.Abs(tilt) <= 90) loaded.PhoneInclination = Math.Abs(tilt);
        loaded.PanelFacingAzimuth = -180;
        string saved = JsonSerializer.Serialize(loaded);
        Assert.DoesNotContain("PhoneFacingAzimuth", saved);
        Assert.DoesNotContain("PhoneInclination", saved);
        Assert.DoesNotContain("FlatImageTopBearing", saved);
        Assert.DoesNotContain("PanelFacingAzimuth", saved);
        Assert.DoesNotContain("TopHeading", saved);
        var reloaded = JsonSerializer.Deserialize<UserSettings>(saved)!;
        Assert.Equal(expected.Axes(), EquipmentOrientation.ToCameraPose(reloaded).Axes());
        Assert.Equal(key, AppData.Key(reloaded));
    }

    [Fact]
    public void UnsupportedLegacyNeedsAnExplicitActionToTurnImageUphill()
    {
        var settings = new UserSettings { BottomAzimuth = 180, CameraTilt = 28 };
        Assert.False(EquipmentOrientation.Describe(settings).IsVincentPhonePose);
        Assert.Throws<InvalidOperationException>(() => settings.PhoneInclination = 27);
        Assert.Throws<InvalidOperationException>(() => settings.PhoneFacingAzimuth = 90);
        var oldAxis = EquipmentOrientation.ToCameraPose(settings).Axes().Z;
        EquipmentOrientation.UseUphillPlacement(settings);
        Assert.True(EquipmentOrientation.Describe(settings).IsVincentPhonePose);
        Assert.Equal(-28, settings.CameraTilt);
        Assert.Equal(0, settings.CameraRoll);
        Assert.Equal(0, settings.BottomAzimuth);
        Assert.Equal(1, oldAxis.Dot(EquipmentOrientation.ToCameraPose(settings).Axes().Z), 12);
    }

    [Fact]
    public void MountingOnHorizontalPanelCopiesItsHeadingEvenForAnOriginallyVerticalImageEdge()
    {
        var settings = new UserSettings { PanelAzimuth = 47, PanelTilt = 0, BottomAzimuth = 270, CameraTilt = -90 };
        Assert.Null(EquipmentOrientation.Describe(settings).ImageTopBearing);
        EquipmentOrientation.PlacePhoneOnPanel(settings);
        Assert.Equal(0, settings.CameraTilt);
        Assert.Equal(0, settings.CameraRoll);
        Assert.Equal(227, settings.FlatImageTopBearing, 10);
        Assert.Null(EquipmentOrientation.Describe(settings).PhoneAzimuth);
        settings.PanelTilt = 6.8;
        settings.PanelFacingAzimuth = -26;
        EquipmentOrientation.PlacePhoneOnPanel(settings);
        Assert.Equal(-6.8, settings.CameraTilt);
        Assert.Equal(154, settings.BottomAzimuth);
        Assert.Equal(-26, settings.PhoneFacingAzimuth);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, -90)]
    [InlineData(326.7, -33.3)]
    public void TopHeadingsRemainEditableFromFlatThroughUpright(double heading, double azimuth)
    {
        var settings = new UserSettings();
        foreach (double tilt in new[] { 0, 28, 90, 0 })
        {
            settings.PanelTilt = tilt;
            settings.PhoneInclination = tilt;
            settings.PanelTopHeading = heading;
            settings.PhoneTopHeading = heading;
            Assert.Equal(heading, settings.PanelTopHeading, 10);
            Assert.Equal(heading, settings.PhoneTopHeading, 10);
            var angles = EquipmentOrientation.Describe(settings);
            if (tilt == 0)
            {
                Assert.Null(angles.PhoneAzimuth);
                Assert.Null(angles.PanelAzimuth);
            }
            else
            {
                Assert.Equal(azimuth, angles.PhoneAzimuth!.Value, 10);
                Assert.Equal(azimuth, angles.PanelAzimuth!.Value, 10);
            }
            if (tilt == 90) Assert.Null(angles.ImageTopBearing);
            else Assert.Equal(heading, angles.ImageTopBearing!.Value, 10);
            // Make the next iteration an actual heading edit, including at 90°.
            settings.PhoneTopHeading = heading + 45;
            settings.PanelTopHeading = heading + 45;
        }
    }

    [Theory]
    [InlineData(-72, 30)]
    [InlineData(72, 145)]
    public void RolledLegacyTopHeadingUsesTheFlattenedPhotoFrameAtEveryTilt(double signedTilt, double roll)
    {
        var settings = new UserSettings { BottomAzimuth = 156, CameraTilt = signedTilt, CameraRoll = roll };
        var reference = EquipmentOrientation.Describe(settings with { CameraTilt = 0 }).ImageTopBearing!.Value;
        var initial = EquipmentOrientation.Describe(settings);
        Assert.True(initial.IsVincentPhonePose);
        Assert.NotEqual(reference, initial.ImageTopBearing!.Value, 6);
        Assert.Equal(reference, settings.PhoneTopHeading, 10);
        settings.PhoneTopHeading = 48.5;
        Assert.Equal(48.5, settings.PhoneTopHeading, 10);
        Assert.Equal(48.5, EquipmentOrientation.Describe(settings with { CameraTilt = 0 }).ImageTopBearing!.Value, 10);
        Assert.Equal(signedTilt, settings.CameraTilt);
        Assert.Equal(roll, settings.CameraRoll);
        foreach (double tilt in new[] { 90, 0, 28 })
        {
            settings.PhoneInclination = tilt;
            Assert.Equal(48.5, settings.PhoneTopHeading, 10);
            Assert.True(EquipmentOrientation.Describe(settings).IsVincentPhonePose);
        }
    }

    [Fact]
    public void FlatToTiltedAndBackRestoresRawCacheKeyAtNonCardinalBearing()
    {
        var settings = new UserSettings { BottomAzimuth = 154.87 };
        var original = AppData.Key(settings);
        settings.PhoneInclination = 28;
        settings.PhoneInclination = 0;
        Assert.Equal(original, AppData.Key(settings));
        settings.PanelAzimuth = -90;
        settings.PanelFacingAzimuth = 90;
        Assert.Equal(270, settings.PanelAzimuth);
        settings.PanelAzimuth = -90;
        settings.PanelTopHeading = 90;
        Assert.Equal(270, settings.PanelAzimuth);
    }

    [Fact]
    public void InvalidAngleEditsCannotMutateThePreviousPose()
    {
        var settings = new UserSettings { CameraTilt = -28, BottomAzimuth = 1080 };
        var before = settings with { };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PhoneInclination = -28);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PhoneInclination = 91);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PhoneFacingAzimuth = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PanelFacingAzimuth = double.PositiveInfinity);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PhoneTopHeading = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.PanelTopHeading = double.PositiveInfinity);
        Assert.Throws<InvalidOperationException>(() => settings.FlatImageTopBearing = 45);
        Assert.Equal(before, settings);
        settings.CameraTilt = 181;
        Assert.False(EquipmentOrientation.Describe(settings).IsVincentPhonePose);
        EquipmentOrientation.UseUphillPlacement(settings);
        Assert.True(EquipmentOrientation.Describe(settings).IsVincentPhonePose);
    }
}
