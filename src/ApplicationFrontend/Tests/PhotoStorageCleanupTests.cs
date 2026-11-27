using System.IO;
using System.Text.Json.Nodes;
using OpenCvSharp;
using SolarShade.Desktop;
using SolarShade.SkyPhotoMasking;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class PhotoStorageCleanupTests
{
    [Fact]
    public void ReplacingPhotoRemovesItsLegacyEditedRunAndAiCachesButKeepsOtherStudiesAndSharedBlobs() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var a = fixture.Settings;
        var source = InputDependencies.Capture(a).Image;
        var ai = AppServices.LoadCachedAiMask(a)!;
        string importedFile = Path.Combine(fixture.Root, "my-original-mask.png");
        File.WriteAllBytes(importedFile, ai.Png);
        var imported = ManualMaskStore.Import(a.SkyImage, importedFile, "Original", centeredDisk: true);
        var edit = ManualMaskStore.Save(a.SkyImage, imported.BaseMask, "", imported.Id, "Site A edit", ai.Png);
        a = a with { SelectedMaskId = edit.Id };

        string newFile = Path.Combine(fixture.Root, "B.png");
        using (var replacement = new Mat(ai.Result.Height, ai.Result.Width, MatType.CV_8UC3, Scalar.Green))
            Cv2.ImWrite(newFile, replacement);
        var b = a with { SkyImage = newFile, SelectedMaskId = null };
        string bHash = AppData.FileKey(newFile);
        string bCache = AppData.PathFor(Path.Combine("mask-stages", AppServices.AiMaskKey(b, bHash)));
        using var black = new Mat(ai.Result.Height, ai.Result.Width, MatType.CV_8UC1, Scalar.Black);
        Cv2.ImEncode(".png", black, out var blackPng);
        SkyMaskExporter.Export(ai.Result with { Png = blackPng }, bCache);
        var bEdit = ManualMaskStore.Save(b.SkyImage, ai.Result with { Png = blackPng }, AppData.FileKey(Path.Combine(bCache, "sky-mask.png")),
            null, "B edit", blackPng, blackPng);
        await service.Evaluate(b, false, _ => { }, null, CancellationToken.None);
        await service.Evaluate(a, false, _ => { }, null, CancellationToken.None);

        string vaultRoot = AppData.PathFor("AcceptedResults");
        string aVault = Path.Combine(vaultRoot, AcceptedResultVault.Key(a));
        string bVault = Path.Combine(vaultRoot, AcceptedResultVault.Key(b));
        var aRun = AppData.Read<DebugDataRun.Manifest>(Path.Combine(aVault, "run.json"))!;
        var bRun = AppData.Read<DebugDataRun.Manifest>(Path.Combine(bVault, "run.json"))!;
        var shared = aRun.ArtifactHashes.Values.Intersect(bRun.ArtifactHashes.Values).ToArray();
        var unique = aRun.ArtifactHashes.Values.Except(bRun.ArtifactHashes.Values).ToArray();
        Assert.NotEmpty(shared); Assert.NotEmpty(unique);
        // Emulate the v0.3.13 manifest and an AI cache created with its older assembly.
        string oldLibrary = Guid.NewGuid().ToString();
        string legacyAi = AppData.PathFor(Path.Combine("mask-stages", AppServices.AiMaskKey(a, source.Content, oldLibrary, "mask-orchestration-v1")));
        SkyMaskExporter.Export(ai.Result, legacyAi);
        string runPath = Path.Combine(aVault, "run.json"), vaultPath = Path.Combine(aVault, "vault.json");
        var run = JsonNode.Parse(File.ReadAllText(runPath))!;
        run["Inputs"]!["sky-mask"]!.AsObject().Remove("PhotoSha256");
        run["Stages"]!["02-sky-mask"]!["LibraryVersion"] = oldLibrary;
        AppData.Write(runPath, run);
        var vault = JsonNode.Parse(File.ReadAllText(vaultPath))!;
        vault.AsObject().Remove("PhotoSha256"); vault["RunSha256"] = AppData.FileKey(runPath);
        AppData.Write(vaultPath, vault);
        string privateNote = Path.Combine(aVault, "my-note.txt");
        File.WriteAllText(privateNote, "keep");

        await service.DiscardPhotoAsync(source, b, bHash);
        Assert.False(File.Exists(vaultPath)); Assert.True(File.Exists(privateNote));
        Assert.False(Directory.Exists(legacyAi)); Assert.Null(AppServices.LoadCachedAiMask(a));
        Assert.Empty(ManualMaskStore.List(source.Content));
        Assert.True(File.Exists(importedFile)); Assert.True(File.Exists(a.SkyImage));
        Assert.True(AcceptedResultVault.Exists(b)); Assert.Equal(blackPng, AppServices.LoadCachedAiMask(b)!.Png);
        Assert.Equal(blackPng, ManualMaskStore.Load(b.SkyImage, bEdit.Id).Png);
        Assert.All(shared, hash => Assert.True(File.Exists(Path.Combine(vaultRoot, "Blobs", hash))));
        Assert.All(unique, hash => Assert.False(File.Exists(Path.Combine(vaultRoot, "Blobs", hash))));
    });

    [Fact]
    public void ReturningToAPhotoRearchivesAndRestoresIrradianceAndPvBesidePersonalFiles() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        using var source = new IrradianceWorkspaceController(service);
        var pv = new PvEvaluationService(source);
        var battery = PvSettingsDraft.Example().Parse();
        string batteryKey = AppData.Key(battery);
        var a = fixture.Settings;
        var ai = AppServices.LoadCachedAiMask(a)!;
        string aKey = AcceptedResultVault.Key(a);
        string aFolder = AppData.PathFor(Path.Combine("AcceptedResults", aKey));
        string pvFolder = Path.Combine(aFolder, "Pv", batteryKey);
        async Task ArchiveBoth()
        {
            source.Completed = await service.Evaluate(a, false, _ => { }, null, CancellationToken.None);
            source.State = UpdateState.UpToDate; source.RefreshSource();
            var current = source.Source.Snapshot!;
            var result = await pv.Evaluate(current, battery, CancellationToken.None);
            PvAcceptedVault.Archive(current, batteryKey, pv.Store, result);
        }
        await ArchiveBoth();
        File.WriteAllText(Path.Combine(aFolder, "my-note.txt"), "keep study");
        File.WriteAllText(Path.Combine(pvFolder, "my-note.txt"), "keep battery");
        string bPath = Path.Combine(fixture.Root, "B.png");
        using (var photo = new Mat(ai.Result.Height, ai.Result.Width, MatType.CV_8UC3, Scalar.Green)) Cv2.ImWrite(bPath, photo);
        var b = a with { SkyImage = bPath };
        await service.DiscardPhotoAsync(InputDependencies.Capture(a).Image, b, AppData.FileKey(bPath));
        source.DiscardPhotoResult();
        Assert.False(AcceptedResultVault.Exists(a));
        Assert.Null(service.TryRestoreAccepted(a));
        Assert.Equal(0, AcceptedResultVault.StorageUsage().Studies);
        // Users can also leave files with the same names the app normally writes.
        File.WriteAllText(Path.Combine(aFolder, "evaluation.json"), "my study export");
        File.WriteAllText(Path.Combine(pvFolder, "result.json"), "my battery export");
        await service.DiscardPhotoAsync(InputDependencies.Capture(b).Image, a, ai.PhotoSha256);
        string cache = AppData.PathFor(Path.Combine("mask-stages", AppServices.AiMaskKey(a, ai.PhotoSha256)));
        SkyMaskExporter.Export(ai.Result, cache, "Fresh inference fixture");
        await ArchiveBoth();
        Assert.True(AcceptedResultVault.Exists(a));
        Assert.Equal(1, AcceptedResultVault.StorageUsage().Studies);
        source.Completed = Assert.IsType<Evaluation>(service.TryRestoreAccepted(a)); source.RefreshSource();
        Assert.NotNull(PvAcceptedVault.Restore(source.Source.Snapshot!, batteryKey, pv.Store));
        Assert.Equal(1, AcceptedResultVault.ClearKnown());
        Assert.False(AcceptedResultVault.Exists(a));
        Assert.Equal("keep study", File.ReadAllText(Path.Combine(aFolder, "my-note.txt")));
        Assert.Equal("keep battery", File.ReadAllText(Path.Combine(pvFolder, "my-note.txt")));
        Assert.Equal("my study export", File.ReadAllText(Path.Combine(aFolder, "evaluation.json")));
        Assert.Equal("my battery export", File.ReadAllText(Path.Combine(pvFolder, "result.json")));
    });

    [Fact]
    public void InputSelectedFromTheOutgoingMaskFolderRemainsReadable() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var a = fixture.Settings;
        var ai = AppServices.LoadCachedAiMask(a)!;
        var edit = ManualMaskStore.Save(a.SkyImage, ai.Result, ai.AiAncestorSha256, null, "Edit", ai.Png, ai.Png);
        string path = ManualMaskStore.VariantPngPath(ai.PhotoSha256, edit.Id);
        var replacement = a with { SkyImage = path };
        await service.DiscardPhotoAsync(InputDependencies.Capture(a).Image, replacement, edit.PngSha256);
        Assert.Equal(ai.Png, File.ReadAllBytes(path));
        Assert.Empty(ManualMaskStore.List(ai.PhotoSha256));
    });

    [Fact]
    public void ReplacementNeedsTheVerifiedNewBytesAndCanRetryALockedOutgoingEdit() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var a = fixture.Settings; var original = InputDependencies.Capture(a).Image;
        var ai = AppServices.LoadCachedAiMask(a)!;
        var edit = ManualMaskStore.Save(a.SkyImage, ai.Result, ai.AiAncestorSha256, null, "Edit", ai.Png, ai.Png);
        string inputCopy = Path.Combine(fixture.Root, "replacement.png");
        File.Copy(a.SkyImage, inputCopy);
        var b = a with { SkyImage = inputCopy };
        await service.DiscardPhotoAsync(original, b, original.Content);
        Assert.Single(ManualMaskStore.List(original.Content));
        using (var other = new Mat(ai.Result.Height, ai.Result.Width, MatType.CV_8UC3, Scalar.Green)) Cv2.ImWrite(inputCopy, other);
        await Assert.ThrowsAsync<IOException>(() => service.DiscardPhotoAsync(original, b, original.Content));
        Assert.Single(ManualMaskStore.List(original.Content));
        string replacementHash = AppData.FileKey(inputCopy);
        using (File.Open(ManualMaskStore.VariantPngPath(original.Content, edit.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => service.DiscardPhotoAsync(original, b, replacementHash));
        Assert.Single(ManualMaskStore.List(original.Content));
        await service.DiscardPhotoAsync(original, b, replacementHash);
        Assert.Empty(ManualMaskStore.List(original.Content));
        Assert.Equal(replacementHash, AppData.FileKey(inputCopy));
    });
}
