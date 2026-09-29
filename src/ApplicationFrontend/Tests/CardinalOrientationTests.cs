using System.IO;
using System.Text.Json;
using OpenCvSharp;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Desktop;
using SolarShade.Shading;
using SolarShade.SkyPhotoMasking;
using Xunit;

public sealed class CardinalOrientationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "solar-cardinals-" + Guid.NewGuid().ToString("N"));
    private string? maskCache;
    private UserSettings Inputs()
    {
        Directory.CreateDirectory(root);
        string imagePath = Path.Combine(root, "photo.png");
        using var image = new Mat(303, 201, MatType.CV_8UC3, new Scalar(120, 85, 35));
        Cv2.ImWrite(imagePath, image);
        var settings = new UserSettings { SkyImage = imagePath, ProfilePath = Path.Combine(root, "camera-profile.json"),
            ImportPath = Path.Combine(root, "missing-weather.xlsx"), Zone = "Invalid zone deliberately unused by orientation" };
        var calibration = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
            201, 303, [100, 151], [0, 70], 70, 95, null, 0, [], DateTimeOffset.UnixEpoch, "Synthetic test lens");
        CameraProfileFiles.Export(new(calibration, "Synthetic", "Test coverage"), root);
        using var binary = new Mat(303, 201, MatType.CV_8UC1, new Scalar(255));
        Cv2.ImEncode(".png", binary, out var png);
        var result = new SkyMaskResult(png, settings.Model, 201, 303, settings.Resolution,
            new(100, 151, 95, 201, 303), new(5, 56, 190, 190), "Synthetic", "Synthetic", null, 0, 0, 0, null, null);
        string key = AppData.Key(new { Image = AppData.FileKey(imagePath), settings.Model, settings.Resolution, settings.CenteredDisk,
            Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppServices.MaskCacheSchema });
        maskCache = AppData.PathFor(Path.Combine("mask-stages", key));
        SkyMaskExporter.Export(result, maskCache, "Synthetic fixture");
        return settings;
    }

    [Fact]
    public async Task PreviewWorksWithoutWeatherOrValidStudySettingsAndExportsLibraryBytes()
    {
        var settings = Inputs(); using var service = new AppServices(Path.Combine(root, "Debug Data"));
        var callbacks = new List<string>();
        CardinalDirectionOverlayResult? shown = null;
        var result = await service.PrepareOrientation(settings, _ => { }, _ => callbacks.Add("photo"), CancellationToken.None,
            overlay => { shown = overlay; callbacks.Add("cardinals"); });
        Assert.Equal(new[] { "photo", "cardinals" }, callbacks);
        Assert.NotNull(result.Mask); Assert.True(result.Cardinals!.HasMarkers); Assert.Same(shown, result.Cardinals);
        Assert.Equal(0, service.PreparationCount);
        Assert.Equal(result.Cardinals.Png, File.ReadAllBytes(Path.Combine(result.DebugDirectory, "02-orientation", "cardinal-directions-overlay.png")));
        Assert.True(File.Exists(Path.Combine(result.DebugDirectory, "02-orientation", "cardinal-directions.xlsx")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.DebugDirectory, "run.json")));
        Assert.Equal("Partial", manifest.RootElement.GetProperty("Status").GetString());
        Assert.False(manifest.RootElement.GetProperty("Stages").TryGetProperty("03-irradiance", out _));
    }

    [Fact]
    public async Task CacheIgnoresWeatherDatesPanelAndVisibilityButReprojectsPoseAndCalibration()
    {
        var settings = Inputs(); using var service = new AppServices(Path.Combine(root, "Debug Data"));
        var first = await service.PrepareOrientation(settings, _ => { }, null, CancellationToken.None);
        var unrelated = settings with { PanelTilt = 70, PanelAzimuth = 45, Isotropic = true, Latitude = 48,
            Longitude = 2, Elevation = 80, Zone = "UTC", Start = new(2021, 1, 1), End = new(2023, 1, 1),
            ImportPath = "", Provider = 1, Substeps = 60, ImportIntervalMinutes = 15, ShowCardinalDirections = false, ShowSunPath = false };
        var second = await service.PrepareOrientation(unrelated, _ => { }, null, CancellationToken.None);
        Assert.Same(first.Cardinals, second.Cardinals); Assert.Equal(1, service.CardinalGenerationCount);
        var changed = await service.PrepareOrientation(settings with { CameraRoll = 20, CameraTilt = 12, BottomAzimuth = 210 }, _ => { }, null, CancellationToken.None);
        Assert.NotSame(first.Cardinals, changed.Cardinals); Assert.Equal(2, service.CardinalGenerationCount);
        Assert.False(first.Cardinals!.Png.SequenceEqual(changed.Cardinals!.Png));
        var profileChanged = await service.PrepareOrientation(settings with { CoverageAngle = 60 }, _ => { }, null, CancellationToken.None);
        Assert.Equal(3, service.CardinalGenerationCount); Assert.NotSame(changed.Cardinals, profileChanged.Cardinals);
    }

    [Fact]
    public async Task FullCalculationPublishesOrientationBeforeMissingWeatherFails()
    {
        var settings = Inputs() with { Zone = "UTC" }; using var service = new AppServices(Path.Combine(root, "Debug Data"));
        CardinalDirectionOverlayResult? shown = null;
        await Assert.ThrowsAnyAsync<IOException>(() => service.Evaluate(settings, false, _ => { }, null,
            CancellationToken.None, onCardinals: result => shown = result));
        Assert.NotNull(shown); Assert.True(shown.HasMarkers); Assert.Equal(0, service.PreparationCount);
        string run = Path.Combine(root, "Debug Data");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "run.json")));
        Assert.Equal("Failed", manifest.RootElement.GetProperty("Status").GetString());
        Assert.Equal("Complete", manifest.RootElement.GetProperty("Stages").GetProperty("02-orientation").GetProperty("Status").GetString());
    }

    [Fact]
    public void UIOrientationIdentityChangesOnlyWithImageGeometryInputs()
    {
        var original = new UserSettings(); string key = MainWindow.OrientationInputKey(original);
        Assert.Equal(key, MainWindow.OrientationInputKey(original with { Start = DateTime.Today, End = DateTime.Today,
            Latitude = 50, Longitude = 5, Zone = "UTC", PanelTilt = 70, PanelAzimuth = 270, Isotropic = true,
            Provider = 1, ImportPath = "different.csv", Substeps = 15, ShowCardinalDirections = false, ShowSunPath = false }));
        foreach (var changed in new[] { original with { CameraTilt = 10 }, original with { CameraRoll = 10 },
            original with { BottomAzimuth = 40 }, original with { SkyImage = "another.jpg" }, original with { ProfilePath = "another.json" },
            original with { CoverageAngle = 80 }, original with { CenteredDisk = true }, original with { Resolution = 512 },
            original with { Model = SkyModel.EfficientNetB7 } }) Assert.NotEqual(key, MainWindow.OrientationInputKey(changed));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (maskCache != null && Directory.Exists(maskCache)) Directory.Delete(maskCache, true);
    }
}
