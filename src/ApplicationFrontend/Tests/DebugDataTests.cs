using System.IO;
using System.Text.Json;
using SolarShade.Desktop;
using Xunit;

public sealed class DebugDataTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"solar-run-files-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public void RecoveringAnInterruptedLegacyMaskPublicationDeletesItsUnregisteredPngAndKeepsNotes()
    {
        var settings = new UserSettings();
        string directory = Path.Combine(root, "02-sky-mask");
        string mask = Path.Combine(directory, "sky-mask.png"), note = Path.Combine(directory, "field-note.txt");
        using (var store = new DebugDataStore(root))
        {
            var run = new DebugDataRun(store, settings, "calculation");
            run.BeginStage("02-sky-mask", "Computed", typeof(DebugDataTests));
            Directory.CreateDirectory(directory);
            File.WriteAllText(mask, "moved by a previous version before it updated its manifest");
            File.WriteAllText(note, "observer's note");
        }
        using var recovered = new DebugDataStore(root);
        recovered.PrepareInputs(settings);
        Assert.False(File.Exists(mask));
        Assert.Equal("observer's note", File.ReadAllText(note));
    }

    [Fact]
    public void LockedPvArtifactLeavesItsSourceManifestForRetry()
    {
        using var store = new DebugDataStore(root);
        string directory = Directory.CreateDirectory(Path.Combine(root, "07-battery")).FullName;
        const string owner = "SolarShade.DependentResult.v1";
        File.WriteAllText(Path.Combine(directory, ".owner"), owner);
        string note = Path.Combine(directory, "field-note.txt"), battery = Path.Combine(directory, "battery-hourly.xlsx");
        File.WriteAllText(note, "personal"); File.WriteAllText(battery, "output");
        string manifest = Path.Combine(directory, "module.json");
        AppData.Write(manifest, new DependentResultStore.Manifest(owner, "site-a", "source", "settings", "software", new()));
        using (File.Open(battery, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => DependentResultStore.DiscardPhotoPublication(store, "site-a"));
        Assert.True(File.Exists(manifest));
        DependentResultStore.DiscardPhotoPublication(store, "site-a");
        Assert.False(File.Exists(battery)); Assert.False(File.Exists(manifest));
        Assert.Equal("personal", File.ReadAllText(note));
    }

    [Fact]
    public void PhotoSwitchRetiresOnlyItsInterruptedPvPublicationInAMixedFolder()
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "07-battery")).FullName;
        const string owner = "SolarShade.DependentResult.v1";
        File.WriteAllText(Path.Combine(directory, ".owner"), owner);
        string note = Path.Combine(directory, "field-note.txt"), output = Path.Combine(directory, "battery-hourly.csv");
        string publishing = Path.Combine(directory, ".module-publishing.json");
        File.WriteAllText(note, "personal");
        AppData.Write(publishing, new DependentResultStore.Manifest(owner, "site-a", "source", "settings", "software", new()));
        File.WriteAllText(output, "moved before the program closed");
        using var recovered = new DebugDataStore(root);
        DependentResultStore.DiscardPhotoPublication(recovered, "other-site");
        Assert.True(File.Exists(output)); Assert.True(File.Exists(publishing));
        DependentResultStore.DiscardPhotoPublication(recovered, "site-a");
        Assert.False(File.Exists(output)); Assert.False(File.Exists(publishing));
        Assert.Equal("personal", File.ReadAllText(note));
    }
    [Fact]
    public void CannotMarkAnUnfinishedRunComplete()
    {
        using var run=new DebugDataRun(new(),root:root);
        Assert.Throws<InvalidOperationException>(()=>run.Complete());
    }
    [Fact]
    public void FailedRunPreservesStageStatusArtifactsAndInputFingerprint()
    {
        using var run=new DebugDataRun(new(),root:root);
        run.RecordInput("weather",new { Sha256="test-fingerprint" });
        string first=run.BeginStage("03-irradiance","Imported",typeof(DebugDataTests));
        string output=Path.Combine(first,"output.txt");File.WriteAllText(output,"actual library artifact");
        run.CompleteStage("03-irradiance",[output]);
        run.BeginStage("04-solar-positions","Computed",typeof(DebugDataTests));
        run.Status("Failed","example error");
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(run.DirectoryPath,"run.json")));
        var stages=json.RootElement.GetProperty("Stages");
        Assert.Equal("Complete",stages.GetProperty("03-irradiance").GetProperty("Status").GetString());
        Assert.Equal("Failed",stages.GetProperty("04-solar-positions").GetProperty("Status").GetString());
        Assert.Equal("Pending",stages.GetProperty("05-transposition").GetProperty("Status").GetString());
        Assert.Equal("test-fingerprint",json.RootElement.GetProperty("Inputs").GetProperty("weather").GetProperty("Sha256").GetString());
        Assert.Equal("actual library artifact",File.ReadAllText(Path.Combine(run.DirectoryPath,"03-irradiance","output.txt")));
    }
    [Fact]
    public void StageCannotReportMissingOrExternalArtifacts()
    {
        using var run=new DebugDataRun(new(),root:root);
        string stage=run.BeginStage("03-irradiance","Imported",typeof(DebugDataTests));
        Assert.Throws<IOException>(()=>run.CompleteStage("03-irradiance",[Path.Combine(stage,"missing.xlsx")]));
        string outside=Path.Combine(root,"outside.txt");File.WriteAllText(outside,"outside");
        Assert.Throws<IOException>(()=>run.CompleteStage("03-irradiance",[outside]));
    }
    [Fact]
    public void ExportRejectsDestinationInsideSourceRunBeforeCreatingIt()
    {
        using var run=new DebugDataRun(new(),"calibration",root);
        run.Skip("01-calibration","Test fixture");run.Complete();
        string destination=Path.Combine(run.DirectoryPath,"nested-export");
        Assert.Throws<ArgumentException>(()=>DebugDataRun.CopyCompleted(run.DirectoryPath,destination));
        Assert.False(Directory.Exists(destination));
        Assert.Throws<ArgumentException>(()=>DebugDataRun.CopyCompleted(run.DirectoryPath,run.DirectoryPath));
    }
    [Fact]
    public async Task AtomicManifestReplacementToleratesBriefReaderLock()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "locked.json");
        AppData.Write(path, new { Status = "Running" });
        Task update;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            update = Task.Run(() => AppData.Write(path, new { Status = "Complete" }));
            await Task.Delay(100);
            Assert.False(update.IsCompleted);
        }
        await update;
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Complete", saved.RootElement.GetProperty("Status").GetString());
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
