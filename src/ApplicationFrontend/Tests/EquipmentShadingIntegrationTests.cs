using System.IO;
using SolarShade.Desktop;
using Xunit;

[Collection("Manual update UI")]
public sealed class EquipmentShadingIntegrationTests
{
    [Fact]
    public async Task SouthFacingInputsReproduceLegacyShadingAndTheOppositeLensChangesOnlyVisibility()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var site = fixture.Settings with { Latitude = 43.56, Longitude = 1.48 };
        File.WriteAllLines(site.ImportPath, new[] { "Timestamp,BHI,DHI" }.Concat(
            Enumerable.Range(0, 24).Select(h => $"2025-05-15T{h:00}:00:00+00:00,{(h is >= 8 and <= 16 ? 400 : 0)},100")));

        var legacy = site with { PanelAzimuth = 180, PanelTilt = 28, BottomAzimuth = 180, CameraTilt = -28, CameraRoll = 0 };
        var entered = site with { PanelTilt = 28, PanelFacingAzimuth = 0, PhoneInclination = 28, PhoneFacingAzimuth = 0 };
        var first = await Evaluate(legacy, "legacy");
        AppData.SaveSettings(entered);
        var second = await Evaluate(AppData.ReadSettings()!, "reopened");
        Assert.Equal(first.Run.Rows.ToArray(), second.Run.Rows.ToArray());
        Assert.Equal(first.Cardinals!.Png, second.Cardinals!.Png);
        Assert.Equal(first.SunPath!.Png, second.SunPath!.Png);
        Assert.True(first.Run.AfterEnergy > 0);

        var opposite = await Evaluate(legacy with { CameraTilt = 28 }, "opposite");
        Assert.Equal(first.Run.BeforeEnergy, opposite.Run.BeforeEnergy);
        Assert.NotEqual(first.Run.AfterEnergy, opposite.Run.AfterEnergy);
        Assert.False(first.SunPath.Png.SequenceEqual(opposite.SunPath!.Png));
        var oppositePose = EquipmentOrientation.Describe(opposite.Settings);
        Assert.Equal(180d, oppositePose.PhoneAzimuth);
        Assert.False(oppositePose.IsVincentPhonePose);

        async Task<Evaluation> Evaluate(UserSettings settings, string directory)
        {
            using var service = new AppServices(Path.Combine(fixture.Root, directory));
            return await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        }
    }
}
