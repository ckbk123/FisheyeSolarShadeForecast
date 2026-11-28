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
    public void InversionAppliesAcrossTheFullImportedRaster()
    {
        using var fixture = new Fixture(); string path = WriteMask(fixture, "polarity");
        var ordinary = ManualMaskStore.Import(fixture.Settings.SkyImage, path, "White is sky", centeredDisk: true);
        var inverted = ManualMaskStore.Import(fixture.Settings.SkyImage, path, "Black is sky", inverted: true, centeredDisk: true);
        using var a = Cv2.ImDecode(ManualMaskStore.Load(fixture.Settings.SkyImage, ordinary.Id).Png, ImreadModes.Grayscale);
        using var b = Cv2.ImDecode(ManualMaskStore.Load(fixture.Settings.SkyImage, inverted.Id).Png, ImreadModes.Grayscale);
        Assert.Equal((byte)0, a.At<byte>(151, 50)); Assert.Equal((byte)255, b.At<byte>(151, 50));
        Assert.Equal((byte)255, a.At<byte>(151, 150)); Assert.Equal((byte)0, b.At<byte>(151, 150));
        Assert.Equal((byte)0, a.At<byte>(0, 0)); Assert.Equal((byte)255, b.At<byte>(0, 0));
        Assert.False(ordinary.ImportInverted); Assert.True(inverted.ImportInverted);
    }

    [Theory]
    [InlineData("dimensions")][InlineData("unreadable")]
    public void InvalidImportLeavesTheSelectedMaskAndStoreUntouched(string invalid) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            var selected = window.Irradiance.ImportMaskForTest(WriteMask(fixture, "valid"), "Keep this");
            string badPath = Path.Combine(fixture.Root, "invalid.png");
            if (invalid == "dimensions")
            {
                using var bad = new Mat(303, 202, MatType.CV_8UC1, Scalar.White);
                Cv2.ImWrite(badPath, bad);
            }
            else File.WriteAllText(badPath, "not an image");
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
    public void RemovingOneMaskPersistsTheEmptySelectionAndKeepsSiblingEdits() => Sta(async () =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(applicationServices: new AppServices(fixture.Debug), loadExampleOnFirstRun: false);
        try
        {
            var original = WriteMask(fixture, "painted");
            var selected = window.Irradiance.ImportMaskForTest(original, "First");
            var data = ManualMaskStore.Load(fixture.Settings.SkyImage, selected.Id);
            var child = ManualMaskStore.Save(fixture.Settings.SkyImage, selected.BaseMask, "",
                selected.Id, "Second", data.Png);
            window.Calculate(); await Until(() => window.Completed != null && !window.UpdatingForTest);
            var previous = window.Completed!;
            Assert.True(AcceptedResultVault.Exists(previous.Settings));
            string archive = Path.Combine(AppData.Root, "AcceptedResults", AcceptedResultVault.Key(previous.Settings), "vault.json");
            Assert.True(File.Exists(archive));
            await window.InputsSettled;
            await window.Irradiance.RemoveSelectedMask(); await window.InputsSettled;
            Assert.True(window.IrradianceController.Settings.NoMaskSelected);
            Assert.Null(window.Completed); Assert.False(window.ExportEnabledForTest);
            Assert.False(File.Exists(Path.Combine(fixture.Debug, "02-sky-mask", "sky-mask.png")));
            Assert.False(File.Exists(ManualMaskStore.VariantPngPath(selected.PhotoSha256, selected.Id)));
            Assert.False(File.Exists(archive));
            Assert.True(File.Exists(original));
            Assert.Equal(data.Png, ManualMaskStore.Load(fixture.Settings.SkyImage, child.Id).Png);
            window.Close(); await window.InputsSettled;
            window = new MainWindow(applicationServices: new AppServices(fixture.Debug), loadExampleOnFirstRun: false);
            await window.RecheckSourcesAsync();
            Assert.True(window.IrradianceController.Settings.NoMaskSelected);
            Assert.Null(window.Completed);
            window.Irradiance.SelectMaskForTest(child.Id); await window.InputsSettled;
            Assert.False(window.IrradianceController.Settings.NoMaskSelected);
            window.Calculate(); await Until(() => window.Completed != null && !window.UpdatingForTest);
            Assert.Equal(child.Id, window.Completed!.Mask!.Variant!.Id);
            window.Irradiance.SelectMaskForTest(null); await window.InputsSettled;
            await window.Irradiance.RemoveSelectedMask(); await window.InputsSettled;
            Assert.Null(AppServices.LoadCachedAiMask(fixture.Settings));
            Assert.True(window.IrradianceController.Settings.NoMaskSelected);
            Assert.Equal(data.Png, ManualMaskStore.Load(fixture.Settings.SkyImage, child.Id).Png);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void UpgradeRecoversAMovedPhotoFromThePreviousMaskRunBeforeAcceptingItsReplacement() => Sta(async () =>
    {
        using var fixture = new Fixture();
        string original = fixture.Settings.SkyImage, digest = AppData.FileKey(original);
        var dormant = ManualMaskStore.Import(original, WriteMask(fixture, "old-mask"), "Old mask", centeredDisk: true);
        using (var oldSession = new AppServices(fixture.Debug))
            await oldSession.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        // The 0.3.14 settings have no active-photo record, selected mask or pinned edge.
        AppData.SaveSettings(fixture.Settings);
        string run = Path.Combine(fixture.Debug, "run.json");
        string mask = Path.Combine(fixture.Debug, "02-sky-mask", "sky-mask.png");
        string oldMask = AppData.FileKey(mask), oldRun = AppData.FileKey(run);
        string moved = Path.Combine(fixture.Root, "archived-original.png");
        File.Move(original, moved);
        string replacement = Path.Combine(fixture.Root, "new-site.png");
        using (var image = new Mat(303, 201, MatType.CV_8UC3, Scalar.Green)) Cv2.ImWrite(replacement, image);
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            await window.RecheckSourcesAsync(); await window.InputsSettled;
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            Assert.Equal(digest, AppData.ReadSettings()!.ActiveSkyPhoto!.Content);
            Assert.Equal(oldRun, AppData.FileKey(run)); Assert.Equal(oldMask, AppData.FileKey(mask));
            Assert.Equal(dormant.Id, Assert.Single(ManualMaskStore.List(digest)).Id);
            window.Irradiance.SelectSkyPhotoForTest(replacement); await window.InputsSettled;
            Assert.Equal(AppData.FileKey(replacement), AppData.ReadSettings()!.ActiveSkyPhoto!.Content);
            Assert.Empty(ManualMaskStore.List(digest)); Assert.False(File.Exists(mask));
            Assert.Equal(digest, AppData.FileKey(moved));
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void AByteIdenticalPhotoAtAnotherPathKeepsTheActiveMaskAndRefinement() => Sta(async () =>
    {
        using var fixture = new Fixture();
        string input = WriteMask(fixture, "mask");
        var imported = ManualMaskStore.Import(fixture.Settings.SkyImage, input, "Current", centeredDisk: true);
        string copy = Path.Combine(fixture.Root, "same-photo.png");
        File.Copy(fixture.Settings.SkyImage, copy);
        AppData.SaveSettings(fixture.Settings with { SelectedMaskId = imported.Id, ApparentFullFovDegrees = 147,
            LastMaskByPhoto = new() { [imported.PhotoSha256] = imported.Id } });
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.Irradiance.SelectSkyPhotoForTest(copy); await window.InputsSettled;
            Assert.Equal(copy, window.IrradianceController.Settings.SkyImage);
            Assert.Equal(147, window.IrradianceController.Settings.ApparentFullFovDegrees);
            Assert.Equal(imported.Id, window.IrradianceController.Settings.SelectedMaskId);
            Assert.Single(ManualMaskStore.List(imported.PhotoSha256));
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void HalfWrittenReplacementKeepsTheOutgoingDebugMaskUntilAValidPhotoArrives() => Sta(async () =>
    {
        using var fixture = new Fixture();
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            string photo = fixture.Settings.SkyImage;
            var old = window.Irradiance.ImportMaskForTest(WriteMask(fixture, "source"), "A");
            await window.InputsSettled;
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            string png = Path.Combine(fixture.Debug, "02-sky-mask", "sky-mask.png");
            string oldMask = AppData.FileKey(png);
            File.WriteAllText(photo, "partial photo");
            await window.RecheckSourcesAsync(); await window.InputsSettled;
            window.Calculate(); await window.InputsSettled;
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            Assert.Throws<InvalidOperationException>(() => window.Irradiance.ImportMaskForTest(png, "Incomplete photo"));
            Assert.Equal(oldMask, AppData.FileKey(png));
            Assert.Single(ManualMaskStore.List(old.PhotoSha256));
            using (var image = new Mat(303, 201, MatType.CV_8UC3, Scalar.Green)) Cv2.ImWrite(photo, image);
            await window.RecheckSourcesAsync(); await window.InputsSettled;
            Assert.Equal(AppData.FileKey(photo), AppData.ReadSettings()!.ActiveSkyPhoto!.Content);
            Assert.Empty(ManualMaskStore.List(old.PhotoSha256));
            Assert.False(File.Exists(png));
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void APhotoChangeResumesAfterALockedGeneratedMaskIsReleasedAndTheAppRestarts() => Sta(async () =>
    {
        using var fixture = new Fixture();
        string replacement = Path.Combine(fixture.Root, "replacement.png");
        using (var pixels = new Mat(303, 201, MatType.CV_8UC3, Scalar.Green)) Cv2.ImWrite(replacement, pixels);
        string outgoing;
        using (var service = new AppServices(fixture.Debug))
        {
            var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
            try
            {
                var variant = window.Irradiance.ImportMaskForTest(WriteMask(fixture, "original"), "Original");
                outgoing = variant.PhotoSha256;
                await window.InputsSettled;
                using var hold = File.OpenRead(ManualMaskStore.VariantPngPath(outgoing, variant.Id));
                window.Irradiance.SelectSkyPhotoForTest(replacement);
                await window.InputsSettled;
                Assert.Equal(UpdateState.Failed, window.UpdateStateForTest);
                Assert.Throws<InvalidOperationException>(() => window.Irradiance.ImportMaskForTest(replacement, "Before retry"));
                Assert.False(window.ExportEnabledForTest);
                Assert.Equal(outgoing, AppData.ReadSettings()!.ActiveSkyPhoto!.Content);
            }
            finally { window.Close(); await window.InputsSettled; }
        }
        using var nextService = new AppServices(fixture.Debug);
        var restarted = new MainWindow(applicationServices: nextService, loadExampleOnFirstRun: false);
        try
        {
            await restarted.InputsSettled;
            Assert.Equal(AppData.FileKey(replacement), AppData.ReadSettings()!.ActiveSkyPhoto!.Content);
            Assert.Empty(ManualMaskStore.List(outgoing));
            Assert.Null(restarted.Completed);
            Assert.True(File.Exists(fixture.Settings.SkyImage));
        }
        finally { restarted.Close(); await restarted.InputsSettled; }
    });

    [Fact]
    public void SwitchingPhotoDiscardsItsVariantsButRestartingTheActivePhotoKeepsItsImport() => Sta(async () =>
    {
        using var fixture = new Fixture(); string path = WriteMask(fixture, "saved-import");
        string otherPhoto = Path.Combine(fixture.Root, "other-photo.png");
        using (var image = new Mat(303, 201, MatType.CV_8UC3, new Scalar(130, 95, 45))) Cv2.ImWrite(otherPhoto, image);
        string secondId;
        using (var service = new AppServices(fixture.Debug))
        {
            var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
            try
            {
                var first = window.Irradiance.ImportMaskForTest(path, "First photo");
                string emptyFolder = Directory.CreateDirectory(Path.Combine(fixture.Root, "empty")).FullName;
                window.Irradiance.BrowseSkyFolder(emptyFolder);
                Assert.Equal(fixture.Settings.SkyImage, window.IrradianceController.Settings.SkyImage);
                Assert.Single(ManualMaskStore.List(first.PhotoSha256));
                string invalid = Path.Combine(emptyFolder, "invalid.jpg");
                File.WriteAllText(invalid, "incomplete");
                window.Irradiance.SelectSkyPhotoForTest(invalid); await window.InputsSettled;
                Assert.Equal(fixture.Settings.SkyImage, window.IrradianceController.Settings.SkyImage);
                Assert.Single(ManualMaskStore.List(first.PhotoSha256));
                window.Irradiance.SelectSkyPhotoForTest(otherPhoto); await window.InputsSettled;
                Assert.Null(window.IrradianceController.Settings.SelectedMaskId);
                Assert.Empty(ManualMaskStore.List(first.PhotoSha256));
                Assert.True(File.Exists(fixture.Settings.SkyImage));
                Assert.True(File.Exists(path));
                secondId = window.Irradiance.ImportMaskForTest(path, "Second photo").Id;
                File.Delete(path); // Saved variants do not depend on the external import file.
            }
            finally { window.Close(); }
        }
        using var restartedService = new AppServices(fixture.Debug);
        var restarted = new MainWindow(applicationServices: restartedService, loadExampleOnFirstRun: false);
        try
        {
            Assert.Equal(secondId, restarted.IrradianceController.Settings.SelectedMaskId);
            restarted.Calculate(); await Until(() => !restarted.UpdatingForTest);
            Assert.True(restarted.ExportEnabledForTest); Assert.True(restarted.Completed!.Mask!.Variant!.IsImported);
            restarted.Irradiance.SelectSkyPhotoForTest(fixture.Settings.SkyImage); await restarted.InputsSettled;
            Assert.Null(restarted.IrradianceController.Settings.SelectedMaskId);
            Assert.Empty(ManualMaskStore.List(ManualMaskStore.PhotoHash(otherPhoto)));
            Assert.Empty(ManualMaskStore.List(ManualMaskStore.PhotoHash(fixture.Settings.SkyImage)));
        }
        finally { restarted.Close(); }
    });
}
