using System.IO;
using System.Security.Cryptography;
using OpenCvSharp;
using SolarShade.Desktop;
using SolarShade.MaskEditor;
using SolarShade.SkyPhotoMasking;
using SolarShade.ShadingCorrection;
using SolarShade.PvBattery.Integration;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class MaskEditingTests
{
    [Fact]
    public void NativeBrushIsBinaryDiskBoundedAndUndoable() => Sta(() =>
    {
        var canvas = new MaskCanvas(41, 41, new byte[41 * 41], 20, 20, 15);
        canvas.BeginStroke(); canvas.PaintLine(0, 20, 40, 20, 7, 255); canvas.EndStroke();
        Assert.Equal((byte)0, canvas.Pixel(0, 20)); Assert.Equal((byte)255, canvas.Pixel(20, 20));
        Assert.True(canvas.IsDirty); canvas.Undo(); Assert.False(canvas.IsDirty); canvas.Redo(); Assert.True(canvas.IsDirty);
        canvas.Reset(); Assert.False(canvas.IsDirty);
        using var decoded = Cv2.ImDecode(canvas.EncodePng(), ImreadModes.Grayscale);
        Assert.Equal(41, decoded.Width); Assert.Equal(41, decoded.Height);
        return Task.CompletedTask;
    });

    [Fact]
    public void BundledExampleMaskOpensAtNativeResolution() => Sta(() =>
    {
        string example = Path.Combine(AppContext.BaseDirectory, "Example", "Debug Data", "reference-run", "02-sky-mask");
        var result = SkyMaskExporter.Load(example);
        using var decoded = Cv2.ImDecode(result.Png, ImreadModes.Grayscale);
        var pixels = new byte[checked(result.Width * result.Height)];
        for (int y = 0; y < result.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(decoded.Ptr(y), pixels, y * result.Width, result.Width);
        var canvas = new MaskCanvas(result.Width, result.Height, pixels, result.Disk.CenterX, result.Disk.CenterY, result.Disk.Radius);
        Assert.Equal(result.Width, canvas.Width); Assert.Equal(result.Height, canvas.Height);
        return Task.CompletedTask;
    });

    [Fact]
    public void SavedVariantIsImmutableAndDetectsChangedPixels() => Sta(() =>
    {
        using var fixture = new Fixture();
        var original = SkyMaskExporter.Load(AppData.PathFor(Path.Combine("mask-stages", AppData.Key(new
        {
            Image = AppData.FileKey(fixture.Settings.SkyImage), fixture.Settings.Model, fixture.Settings.Resolution,
            fixture.Settings.CenteredDisk, Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppServices.MaskCacheSchema
        }))));
        using var image = new Mat(original.Height, original.Width, MatType.CV_8UC1, new Scalar(0));
        Cv2.ImEncode(".png", image, out byte[] png);
        string ancestor = Convert.ToHexString(SHA256.HashData(original.Png));
        var variant = ManualMaskStore.Save(fixture.Settings.SkyImage, original, ancestor, null, "Edited test", png, original.Png);
        var (loaded, pixels) = ManualMaskStore.Load(fixture.Settings.SkyImage, variant.Id);
        Assert.Equal("Edited test", loaded.Label); Assert.Equal(png, pixels);
        Assert.Single(ManualMaskStore.List(variant.PhotoSha256));
        File.WriteAllBytes(ManualMaskStore.VariantPngPath(variant.PhotoSha256, variant.Id), [0, 1, 2]);
        Assert.Throws<InvalidDataException>(() => ManualMaskStore.Load(fixture.Settings.SkyImage, variant.Id));
        return Task.CompletedTask;
    });

    [Fact]
    public void ExactAcceptedResultReturnsWithoutRecalculationAndExports() => Sta(async () =>
    {
        using var fixture = new Fixture(); int calculations = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        { calculations++; return ShadingCorrectionModule.ApplyToPanel(input, scene, ct); });
        var first = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        Assert.True(AcceptedResultVault.Exists(fixture.Settings));
        var changed = fixture.Settings with { PanelTilt = 42 };
        service.ObserveInputs(changed, refreshSources: true);
        var second = await service.Evaluate(changed, false, _ => { }, null, CancellationToken.None);
        Assert.True(service.IsCurrent(second));
        service.ObserveInputs(fixture.Settings, refreshSources: true);
        var restored = service.TryRestoreAccepted(fixture.Settings);
        Assert.NotNull(restored); Assert.True(service.IsCurrent(restored));
        Assert.Equal(first.Run.AfterEnergy, restored.Run.AfterEnergy);
        Assert.Equal(2, calculations);
        Assert.Equal(3, AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))?.UpdateNumber);
        string exported = AppServices.Export(restored, Path.Combine(fixture.Root, "exports"));
        Assert.True(File.Exists(Path.Combine(exported, "02-sky-mask", "mask-provenance.json")));
    });

    [Fact]
    public void SelectedEditFeedsShadingAndExportAndAiCanBeRestored() => Sta(async () =>
    {
        using var fixture = new Fixture(); int calculations = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        { calculations++; return ShadingCorrectionModule.ApplyToPanel(input, scene, ct); });
        var ai = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(ai.Mask!.Png, AppServices.LoadCachedAiMask(fixture.Settings)?.Png);
        using var black = new Mat(ai.Mask!.Result.Height, ai.Mask.Result.Width, MatType.CV_8UC1, new Scalar(0));
        Cv2.ImEncode(".png", black, out byte[] changedPixels);
        var variant = ManualMaskStore.Save(fixture.Settings.SkyImage, ai.Mask.Result with { DiskMethod = "Auto" }, ai.Mask.AiAncestorSha256,
            null, "Blocked sky", changedPixels, ai.Mask.Png);
        var selected = fixture.Settings with { SelectedMaskId = variant.Id };
        service.ObserveInputs(selected, refreshSources: true);
        var edited = await service.Evaluate(selected, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(changedPixels, edited.Mask!.Png); Assert.Equal(2, calculations);
        string exported = AppServices.Export(edited, Path.Combine(fixture.Root, "exports"));
        Assert.Equal(changedPixels, File.ReadAllBytes(Path.Combine(exported, "02-sky-mask", "sky-mask.png")));
        Assert.Contains(variant.Id, File.ReadAllText(Path.Combine(exported, "02-sky-mask", "mask-provenance.json")));
        service.ObserveInputs(fixture.Settings, refreshSources: true);
        var restored = service.TryRestoreAccepted(fixture.Settings);
        Assert.NotNull(restored); Assert.Equal(ai.Mask.Png, restored.Mask!.Png); Assert.Equal(2, calculations);
        Assert.True(service.IsCurrent(restored));
    });

    [Fact]
    public void ReturningToAnExactStudyRestoresMatchingPvWithoutRunningBatteryAgain() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var first = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        using var source = new IrradianceWorkspaceController(service) { Completed = first, State = UpdateState.UpToDate };
        source.RefreshSource(); int batteryRuns = 0;
        var backend = new PvEvaluationService(source, (snapshot, settings, ct) =>
        { batteryRuns++; return PanelBatterySimulation.Compute(snapshot.Run, settings, snapshot.TimeZoneId, ct); });
        using var pv = new PvWorkspaceController(source, backend);
        pv.SetDraft(PvSettingsDraft.Example()); await pv.EvaluateAsync();
        Assert.True(pv.IsCurrent, pv.Status); Assert.Equal(1, batteryRuns);
        var other = fixture.Settings with { PanelTilt = 42 };
        service.ObserveInputs(other, refreshSources: true);
        source.State = UpdateState.UpdateRequired; source.RefreshSource();
        var second = await service.Evaluate(other, false, _ => { }, null, CancellationToken.None);
        source.Completed = second; source.State = UpdateState.UpToDate; source.RefreshSource();
        Assert.False(pv.IsCurrent);
        service.ObserveInputs(fixture.Settings, refreshSources: true);
        source.State = UpdateState.UpdateRequired; source.RefreshSource();
        var restored = service.TryRestoreAccepted(fixture.Settings);
        Assert.NotNull(restored);
        source.Completed = restored; source.State = UpdateState.UpToDate; source.RefreshSource();
        await Until(() => pv.IsCurrent);
        Assert.Equal(1, batteryRuns); Assert.True(pv.CanExport);
    });

    [Fact]
    public void ClearingSavedStudiesKeepsCurrentResultAndUnrecognizedData() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        Assert.Equal((0, 0L), AcceptedResultVault.StorageUsage());
        Assert.Equal(0, AcceptedResultVault.ClearKnown());
        var result = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        Assert.True(AcceptedResultVault.Exists(fixture.Settings));
        string unknown = AppData.PathFor(Path.Combine("AcceptedResults", "personal-note.txt"));
        File.WriteAllText(unknown, "keep");
        Assert.True(AcceptedResultVault.StorageUsage().Studies > 0);
        Assert.Equal(1, AcceptedResultVault.ClearKnown());
        Assert.False(AcceptedResultVault.Exists(fixture.Settings));
        Assert.True(service.IsCurrent(result)); Assert.Equal("keep", File.ReadAllText(unknown));
    });

    [Fact]
    public void CorruptSavedStudyCannotBecomeCurrent() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var first = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        var other = fixture.Settings with { PanelTilt = 42 };
        var second = await service.Evaluate(other, false, _ => { }, null, CancellationToken.None);
        string archive = AppData.PathFor(Path.Combine("AcceptedResults", AcceptedResultVault.Key(fixture.Settings), "evaluation.json"));
        File.AppendAllText(archive, "corrupt");
        service.ObserveInputs(fixture.Settings, refreshSources: true);
        Assert.Throws<InvalidDataException>(() => service.TryRestoreAccepted(fixture.Settings));
        Assert.False(service.IsCurrent(first));
        Assert.False(service.IsCurrent(second));
    });

    [Fact]
    public void PhotoSwitchBackRestoresOnlyTheExactVerifiedPhoto() => Sta(async () =>
    {
        using var fixture = new Fixture(); int calculations = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        { calculations++; return ShadingCorrectionModule.ApplyToPanel(input, scene, ct); });
        var first = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        string secondPhoto = Path.Combine(fixture.Root, "second.jpg");
        using (var original = Cv2.ImRead(fixture.Settings.SkyImage, ImreadModes.Color)) Cv2.ImWrite(secondPhoto, original);
        var secondSettings = fixture.Settings with { SkyImage = secondPhoto };
        string secondKey = AppData.Key(new { Image = AppData.FileKey(secondPhoto), secondSettings.Model,
            secondSettings.Resolution, secondSettings.CenteredDisk,
            Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppServices.MaskCacheSchema });
        SkyMaskExporter.Export(first.Mask!.Result, AppData.PathFor(Path.Combine("mask-stages", secondKey)), "Synthetic second photo");
        service.ObserveInputs(secondSettings, refreshSources: true);
        var second = await service.Evaluate(secondSettings, false, _ => { }, null, CancellationToken.None);
        Assert.True(service.IsCurrent(second)); Assert.Equal(2, calculations);
        service.ObserveInputs(fixture.Settings, refreshSources: true);
        var restored = service.TryRestoreAccepted(fixture.Settings);
        Assert.NotNull(restored); Assert.True(service.IsCurrent(restored)); Assert.Equal(2, calculations);
        Assert.Equal(first.Mask.Png, restored.Mask!.Png);
        Assert.NotEqual(AppData.FileKey(secondPhoto), AppData.FileKey(fixture.Settings.SkyImage));
    });
}
