using System.IO;
using System.Text.Json;
using OpenCvSharp;
using SolarShade.Desktop;
using SolarShade.ShadingCorrection;
using SolarShade.SkyPhotoMasking;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class ImportedMaskTests
{
    private static string WriteMask(Fixture fixture, string name, bool half = true)
    {
        string path = Path.Combine(fixture.Root, name + ".png");
        using var image = new Mat(303, 201, MatType.CV_8UC1, Scalar.White);
        if (half) Cv2.Rectangle(image, new Rect(0, 0, 100, 303), Scalar.Black, -1);
        Cv2.ImWrite(path, image); return path;
    }

    [Fact]
    public void HalfBlackImportFeedsCalculationAndExportWithoutAnyAiModel() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Directory.Delete(Path.Combine(AppData.Root, "mask-stages"), true);
        string halfPath = WriteMask(fixture, "half-black"), whitePath = WriteMask(fixture, "white-disk", false);
        var half = ManualMaskStore.Import(fixture.Settings.SkyImage, halfPath, "Half black", centeredDisk: true);
        var white = ManualMaskStore.Import(fixture.Settings.SkyImage, whitePath, "Open sky", centeredDisk: true);
        byte[] expected = ManualMaskStore.Load(fixture.Settings.SkyImage, half.Id).Png;
        // Invalid enum values would fail ModelFilename/EnsureModel immediately if inference were entered.
        var settings = fixture.Settings with { Model = (SkyModel)999, Resolution = -1, SelectedMaskId = white.Id };
        int calculations = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        { calculations++; return ShadingCorrectionModule.ApplyToPanel(input, scene, ct); });
        var open = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        var selected = settings with { SelectedMaskId = half.Id };
        service.ObserveInputs(selected, refreshSources: true);
        Assert.False(service.IsCurrent(open));
        var shaded = await service.Evaluate(selected, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(2, calculations); Assert.Equal(expected, shaded.Mask!.Png);
        Assert.True(shaded.Run.AfterEnergy < open.Run.AfterEnergy * .8, "The half-black mask must reduce irradiance relative to the white mask.");
        Assert.Equal(open.Run.BeforeEnergy, shaded.Run.BeforeEnergy);
        string exported = AppServices.Export(shaded, Path.Combine(fixture.Root, "exports"));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(exported, "02-sky-mask", "sky-mask.png")));
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(exported, "02-sky-mask", "mask-provenance.json")));
        Assert.Equal("Imported", provenance.RootElement.GetProperty("Source").GetString());
        Assert.Equal("half-black.png", provenance.RootElement.GetProperty("ImportedFileName").GetString());
        Assert.Equal(JsonValueKind.Null, provenance.RootElement.GetProperty("AiAncestorSha256").ValueKind);
        Assert.False(Directory.Exists(Path.Combine(AppData.Root, "models-v1")));
        Assert.False(Directory.Exists(Path.Combine(AppData.Root, "mask-stages")));
        // Returning to this exact imported mask restores only its own verified result.
        service.ObserveInputs(settings, refreshSources: true);
        var restored = service.TryRestoreAccepted(settings);
        Assert.NotNull(restored); Assert.Equal(open.Mask!.Png, restored.Mask!.Png); Assert.Equal(2, calculations);
    });

    [Fact]
    public void ImportedMaskCanBeEditedAndRetainsItsImportedLineage() => Sta(async () =>
    {
        using var fixture = new Fixture();
        var imported = ManualMaskStore.Import(fixture.Settings.SkyImage, WriteMask(fixture, "original"), "Original import", centeredDisk: true);
        using var black = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black);
        Cv2.ImEncode(".png", black, out byte[] pixels);
        var edit = ManualMaskStore.Save(fixture.Settings.SkyImage, imported.BaseMask, "", imported.Id, "Blocked", pixels);
        Assert.True(edit.IsImported); Assert.Equal("Edited", edit.Source); Assert.Equal(imported.Id, edit.ParentId);
        Assert.Equal(imported.ImportedAncestorSha256, edit.ImportedAncestorSha256); Assert.Equal("", edit.AiAncestorSha256);
        using var service = new AppServices(fixture.Debug);
        var result = await service.Evaluate(fixture.Settings with { SelectedMaskId = edit.Id, Model = (SkyModel)999 }, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(pixels, result.Mask!.Png); Assert.Equal(0, result.Run.AfterEnergy);
        Assert.Equal(imported.PngSha256, ManualMaskStore.Load(fixture.Settings.SkyImage, imported.Id).Variant.PngSha256);
    });

    [Fact]
    public void MissingImportedSourceInvalidatesResultsAndCannotFallBackToAi() => Sta(async () =>
    {
        using var fixture = new Fixture();
        var imported = ManualMaskStore.Import(fixture.Settings.SkyImage, WriteMask(fixture, "source"), "Imported source", centeredDisk: true);
        var settings = fixture.Settings with { SelectedMaskId = imported.Id, Model = (SkyModel)999 };
        using var service = new AppServices(fixture.Debug);
        var result = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        File.Delete(ManualMaskStore.SourcePngPath(imported.PhotoSha256, imported));
        service.ObserveInputs(settings, refreshSources: true);
        Assert.False(service.IsCurrent(result)); Assert.Null(service.TryRestoreAccepted(settings));
        Assert.Throws<InvalidOperationException>(() => AppServices.Export(result, Path.Combine(fixture.Root, "exports")));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.Evaluate(settings, false, _ => { }, null, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(AppData.Root, "models-v1")));
    });

    [Fact]
    public void InversionIsExplicitAndOutsideThePhotoDiskAlwaysStaysBlack()
    {
        using var fixture = new Fixture(); string path = WriteMask(fixture, "polarity");
        var ordinary = ManualMaskStore.Import(fixture.Settings.SkyImage, path, "White is sky", centeredDisk: true);
        var inverted = ManualMaskStore.Import(fixture.Settings.SkyImage, path, "Black is sky", inverted: true, centeredDisk: true);
        using var a = Cv2.ImDecode(ManualMaskStore.Load(fixture.Settings.SkyImage, ordinary.Id).Png, ImreadModes.Grayscale);
        using var b = Cv2.ImDecode(ManualMaskStore.Load(fixture.Settings.SkyImage, inverted.Id).Png, ImreadModes.Grayscale);
        Assert.Equal((byte)0, a.At<byte>(151, 50)); Assert.Equal((byte)255, b.At<byte>(151, 50));
        Assert.Equal((byte)255, a.At<byte>(151, 150)); Assert.Equal((byte)0, b.At<byte>(151, 150));
        Assert.Equal((byte)0, a.At<byte>(0, 0)); Assert.Equal((byte)0, b.At<byte>(0, 0));
        Assert.False(ordinary.ImportInverted); Assert.True(inverted.ImportInverted);
    }

    [Theory]
    [InlineData("dimensions")][InlineData("gray")][InlineData("color")][InlineData("alpha")][InlineData("depth")]
    public void InvalidImportLeavesTheSelectedMaskAndStoreUntouched(string invalid) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            var selected = window.Irradiance.ImportMaskForTest(WriteMask(fixture, "valid"), "Keep this");
            string badPath = Path.Combine(fixture.Root, "invalid.png");
            using var bad = invalid switch
            {
                "dimensions" => new Mat(303, 202, MatType.CV_8UC1, Scalar.White),
                "gray" => new Mat(303, 201, MatType.CV_8UC1, new Scalar(128)),
                "color" => new Mat(303, 201, MatType.CV_8UC3, new Scalar(0, 255, 0)),
                "alpha" => new Mat(303, 201, MatType.CV_8UC4, new Scalar(255, 255, 255, 0)),
                _ => new Mat(303, 201, MatType.CV_16UC1, Scalar.White)
            };
            Cv2.ImWrite(badPath, bad);
            Assert.Throws<InvalidDataException>(() => window.Irradiance.ImportMaskForTest(badPath, "Rejected"));
            Assert.Equal(selected.Id, window.IrradianceController.Settings.SelectedMaskId);
            Assert.Single(ManualMaskStore.List(selected.PhotoSha256));
            Assert.Empty(Directory.EnumerateFiles(AppData.Root, "*.partial", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(AppData.Root, "*.tmp", SearchOption.AllDirectories));
            await window.InputsSettled;
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PhotoAndMaskChoicesSurviveSwitchingRestartAndOriginalMaskRemoval() => Sta(async () =>
    {
        using var fixture = new Fixture(); string path = WriteMask(fixture, "saved-import");
        string otherPhoto = Path.Combine(fixture.Root, "other-photo.png");
        using (var image = new Mat(303, 201, MatType.CV_8UC3, new Scalar(130, 95, 45))) Cv2.ImWrite(otherPhoto, image);
        string firstId, secondId;
        using (var service = new AppServices(fixture.Debug))
        {
            var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
            try
            {
                firstId = window.Irradiance.ImportMaskForTest(path, "First photo").Id;
                window.Irradiance.SelectSkyPhotoForTest(otherPhoto); await window.InputsSettled;
                Assert.Null(window.IrradianceController.Settings.SelectedMaskId);
                secondId = window.Irradiance.ImportMaskForTest(path, "Second photo").Id;
                window.Irradiance.SelectSkyPhotoForTest(fixture.Settings.SkyImage); await window.InputsSettled;
                Assert.Equal(firstId, window.IrradianceController.Settings.SelectedMaskId);
                window.Irradiance.SelectMaskForTest(null); await window.InputsSettled;
                window.Irradiance.SelectMaskForTest(firstId); await window.InputsSettled;
                File.Delete(path); // Saved variants do not depend on the external import file.
            }
            finally { window.Close(); }
        }
        using var restartedService = new AppServices(fixture.Debug);
        var restarted = new MainWindow(applicationServices: restartedService, loadExampleOnFirstRun: false);
        try
        {
            Assert.Equal(firstId, restarted.IrradianceController.Settings.SelectedMaskId);
            restarted.Irradiance.SelectSkyPhotoForTest(otherPhoto); await restarted.InputsSettled;
            Assert.Equal(secondId, restarted.IrradianceController.Settings.SelectedMaskId);
            restarted.Calculate(); await Until(() => !restarted.UpdatingForTest);
            Assert.True(restarted.ExportEnabledForTest); Assert.True(restarted.Completed!.Mask!.Variant!.IsImported);
        }
        finally { restarted.Close(); }
    });
}
