using System.IO;
using System.Text.Json;
using SolarShade.Desktop;
using Xunit;

public sealed class DebugDataTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"solar-run-files-"+Guid.NewGuid().ToString("N"));
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
