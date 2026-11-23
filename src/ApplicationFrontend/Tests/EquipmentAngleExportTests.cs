using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using SolarShade.Desktop;
using Xunit;

[Collection("Manual update UI")]
public sealed class EquipmentAngleExportTests
{
    [Fact]
    public async Task SavedWestFacingSetupHasTheSameAnglesInManifestReceiptAndWorkbook()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        using var service = new AppServices(fixture.Debug);
        // A legacy 270° panel and camera image bottom represent a west-facing mounted phone.
        var settings = fixture.Settings with
        {
            PanelAzimuth = 270, PanelTilt = 28, BottomAzimuth = 270, CameraTilt = -28, CameraRoll = 0
        };
        var result = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        string output = AppServices.Export(result, fixture.Root, new DataExportOptions());

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Debug, "run.json")));
        Assert.Contains("Settings.PanelAzimuth", manifest.RootElement.GetProperty("StoredEquipmentConvention").GetString());
        var angles = manifest.RootElement.GetProperty("EquipmentAngles");
        Assert.Equal(90, angles.GetProperty("PhoneLensAzimuthClockwiseFromSouthDegrees").GetDouble());
        Assert.Equal(90, angles.GetProperty("PanelFrontAzimuthClockwiseFromSouthDegrees").GetDouble());
        Assert.Equal(28, angles.GetProperty("PhoneTiltFromHorizontalDegrees").GetDouble());
        Assert.Equal(90, angles.GetProperty("ImageTopBearingClockwiseFromTrueNorthDegrees").GetDouble(), 8);
        Assert.True(angles.GetProperty("IsVincentPhonePose").GetBoolean());

        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "export.json")));
        Assert.True(JsonElement.DeepEquals(angles, receipt.RootElement.GetProperty("EquipmentAngles")));
        using var book = ZipFile.OpenRead(Path.Combine(output, "irradiance.xlsx"));
        using var metadata = book.GetEntry("docProps/core.xml")!.Open();
        var description = XDocument.Load(metadata).Descendants(XName.Get("description", "http://purl.org/dc/elements/1.1/")).Single().Value;
        const string prefix = "Provenance: ";
        using var provenance = JsonDocument.Parse(description[(description.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..]);
        Assert.True(JsonElement.DeepEquals(angles, provenance.RootElement.GetProperty("EquipmentAngles")));
        Assert.Equal(270, provenance.RootElement.GetProperty("Panel").GetProperty("AzimuthDegrees").GetDouble());
        Assert.Contains("true north", provenance.RootElement.GetProperty("PanelStorageConvention").GetString());
    }

    [Fact]
    public void FlatVerticalAndHistoricalDownwardPosesHaveUnambiguousReports()
    {
        var settings = new UserSettings { PanelTilt = 0, BottomAzimuth = 270, CameraTilt = 0 };
        var flat = EquipmentAngleReport.From(settings)!;
        Assert.Null(flat.PanelFrontAzimuthClockwiseFromSouthDegrees);
        Assert.Null(flat.PhoneLensAzimuthClockwiseFromSouthDegrees);
        Assert.Equal(90, flat.ImageTopBearingClockwiseFromTrueNorthDegrees!.Value, 8);
        Assert.Contains("undefined", flat.DescribePhone());

        var vertical = EquipmentAngleReport.From(settings with { CameraTilt = -90 })!;
        Assert.Null(vertical.ImageTopBearingClockwiseFromTrueNorthDegrees);
        Assert.Contains("undefined", vertical.DescribeImage());

        var downward = EquipmentAngleReport.From(settings with { CameraTilt = -120 })!;
        Assert.False(downward.IsVincentPhonePose);
        Assert.Null(downward.PhoneTiltFromHorizontalDegrees);
        Assert.Equal(120, downward.LensZenithFromUpDegrees);
        Assert.Contains("outside", downward.DescribePhone());
        Assert.Null(EquipmentAngleReport.From(settings with { CameraTilt = double.NaN }));
    }
}
