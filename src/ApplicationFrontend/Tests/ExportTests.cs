using System.IO;
using System.Text.Json;
using PdfSharp.Pdf.IO;
using SolarShade.Desktop;
using SolarShade.ShadingCorrection;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class ExportTests
{
    private static Task<Evaluation> Calculate(AppServices service, UserSettings settings) => service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
    private static void NoPublishedExport(string root)
    { Assert.False(Directory.Exists(Path.Combine(root, "export"))); Assert.Empty(Directory.GetDirectories(root, ".solarshade-export-*")); }

    [Fact]
    public void CompleteManifestWithSkippedStagesCannotBypassTheExportService()
    {
        using var fixture = new Fixture();
        using (var run = new DebugDataRun(fixture.Settings, root: fixture.Debug))
        {
            foreach (string stage in DebugDataRun.AllStages) run.Skip(stage, "Absent input");
            run.Complete(); Assert.True(run.IsCurrent); Assert.False(run.HasCompleteDataset);
            Assert.Throws<InvalidOperationException>(() => run.CopyCurrent(Path.Combine(fixture.Root, "export")));
        }
        Assert.Throws<InvalidOperationException>(() => DebugDataRun.CopyCompleted(fixture.Debug, Path.Combine(fixture.Root, "export")));
        NoPublishedExport(fixture.Root);
    }
    [Fact]
    public async Task OpenWorkbookAndBlockedDestinationRejectWithoutOverwritingUserFiles()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        using (var locked = new FileStream(Path.Combine(fixture.Debug, "06-shading/panel-shaded.xlsx"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Assert.Throws<InvalidOperationException>(() => result.ManagedDebugRun!.CopyCurrent(Path.Combine(fixture.Root, "export")));
        NoPublishedExport(fixture.Root);
        result = await Calculate(service, fixture.Settings);
        string blocker = Path.Combine(fixture.Root, "blocked"); File.WriteAllText(blocker, "user file");
        Assert.Throws<IOException>(() => result.ManagedDebugRun!.CopyCurrent(Path.Combine(blocker, "export")));
        Assert.Equal("user file", File.ReadAllText(blocker));
    }
    [Theory][InlineData(0)][InlineData(2)]
    public async Task MissingOrMultipagePdfCannotBePublished(int pages)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        Assert.ThrowsAny<Exception>(() => SnapshotExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "export"), (folder, _, _) => {
            if (pages == 0) return;
            using var pdf = new PdfSharp.Pdf.PdfDocument(); for (int i = 0; i < pages; i++) pdf.AddPage(); pdf.Save(Path.Combine(folder, "Summary.pdf"));
        }));
        NoPublishedExport(fixture.Root);
    }

    [Fact]
    public async Task CopiesAllArtifactsExactlyAndUsesSavedSettingsWithoutRecalculation()
    {
        using var fixture = new Fixture(); int calls = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) => { calls++; return ShadingCorrectionModule.ApplyToPanel(input, scene, ct); });
        var result = await Calculate(service, fixture.Settings);
        result.Settings.PanelTilt = 89; // Public evaluation objects are not the saved successful-update authority.
        string exported = AppServices.Export(result, Path.Combine(fixture.Root, "exports"));
        var state = AppData.Read<DebugDataRun.Manifest>(Path.Combine(exported, "run.json"))!;
        Assert.Equal(fixture.Settings.PanelTilt, state.Settings.PanelTilt); Assert.Equal(1, calls);
        foreach (var (path, hash) in state.ArtifactHashes)
        {
            Assert.Equal(hash, AppData.FileKey(Path.Combine(exported, path)));
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Debug, path)), File.ReadAllBytes(Path.Combine(exported, path)));
        }
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Debug, "run.json")), File.ReadAllBytes(Path.Combine(exported, "run.json")));
        using var pdf = PdfReader.Open(Path.Combine(exported, "Summary.pdf"), PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages); Assert.Contains(state.Id, pdf.Info.Subject);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(exported, "export.json")));
        Assert.Equal(AppData.FileKey(Path.Combine(exported, "Summary.pdf")), receipt.RootElement.GetProperty("SummarySha256").GetString());
        Assert.Equal("Complete", receipt.RootElement.GetProperty("Status").GetString());
        Assert.Equal(state.ArtifactHashes.Count + 3, Directory.GetFiles(exported, "*", SearchOption.AllDirectories).Length);
    }
    [Theory]
    [InlineData("01-calibration/camera-profile.json")]
    [InlineData("02-sky-mask/sky-mask.png")]
    [InlineData("03-irradiance/horizontal-irradiance.xlsx")]
    [InlineData("06-shading/panel-shaded.xlsx")]
    public async Task MissingRequiredArtifactsRejectExport(string relative)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        File.Delete(Path.Combine(fixture.Debug, relative));
        Assert.False(result.ManagedDebugRun!.HasCompleteDataset);
        Assert.Throws<InvalidOperationException>(() => result.ManagedDebugRun.CopyCurrent(Path.Combine(fixture.Root, "export")));
        NoPublishedExport(fixture.Root);
    }
    [Fact]
    public async Task PdfFailureCleansUpAndKeepsCurrentDatasetUsable()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        Assert.Throws<IOException>(() => SnapshotExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "export"), (folder, _, _) => {
            File.WriteAllText(Path.Combine(folder, "Summary.pdf"), "partial"); throw new IOException("Simulated full disk during PDF generation.");
        }));
        NoPublishedExport(fixture.Root); Assert.True(service.IsCurrent(result));
        Assert.True(Directory.Exists(AppServices.Export(result, Path.Combine(fixture.Root, "retry"))));
    }
    [Theory][InlineData("source")][InlineData("artifact")][InlineData("edit")][InlineData("new update")]
    public async Task ChangesDuringPdfGenerationPreventPublication(string change)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var export = Task.Run(() => SnapshotExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "export"), (folder, state, time) => {
            entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException(); SummaryPdf.Write(folder, state, time);
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
            if (change == "source") File.AppendAllText(fixture.Settings.ImportPath, "\n");
            if (change == "artifact") File.AppendAllText(Path.Combine(fixture.Debug, "01-calibration/calibration.yml"), "\n");
            if (change == "edit") service.ObserveInputs(fixture.Settings with { PanelTilt = 80 });
            if (change == "new update") await Calculate(service, fixture.Settings with { PanelTilt = 80 });
        }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => export);
        NoPublishedExport(fixture.Root);
    }
    [Fact]
    public async Task LockedSourceAndExistingDestinationLeaveNoPartialExport()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        using (var locked = new FileStream(fixture.Settings.ImportPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<Exception>(() => result.ManagedDebugRun!.CopyCurrent(Path.Combine(fixture.Root, "export")));
        NoPublishedExport(fixture.Root);
        result = await Calculate(service, fixture.Settings);
        string destination = Path.Combine(fixture.Root, "export"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "user.txt"), "preserve");
        Assert.Throws<IOException>(() => result.ManagedDebugRun!.CopyCurrent(destination));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(destination, "user.txt")));
        Assert.Single(Directory.GetFiles(destination));
    }
    [Theory][InlineData(0)][InlineData(100)]
    public async Task ZeroBaselineAndZeroLossAreExportable(double irradiance)
    {
        using var fixture = new Fixture();
        using var service = new AppServices(fixture.Debug, (input, scene, ct) => {
            var actual = ShadingCorrectionModule.ApplyToPanel(input, scene, ct);
            return actual with { Rows = actual.Rows.Select(row => row with { BeforeDirect = 0, BeforeDiffuse = irradiance, AfterDirect = 0, AfterDiffuse = irradiance }).ToArray() };
        });
        var result = await Calculate(service, fixture.Settings);
        Assert.Equal(result.Run.BeforeEnergy, result.Run.AfterEnergy);
        string folder = AppServices.Export(result, fixture.Root);
        using var pdf = PdfReader.Open(Path.Combine(folder, "Summary.pdf"), PdfDocumentOpenMode.Import); Assert.Single(pdf.Pages);
        if (irradiance == 0) Assert.Null(result.Run.LossPercent); else Assert.Equal(0, result.Run.LossPercent);
    }
    [Fact]
    public async Task LongUnicodeNamesAndFallbackSettingsFitOnePage()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string name = "Dữ liệu bầu trời " + new string('w', 130) + ".csv";
        string weather = Path.Combine(fixture.Root, name); File.Copy(fixture.Settings.ImportPath, weather);
        var result = await Calculate(service, fixture.Settings with { ImportPath = weather });
        string folder = AppServices.Export(result, fixture.Root);
        using var pdf = PdfReader.Open(Path.Combine(folder, "Summary.pdf"), PdfDocumentOpenMode.Import); Assert.Single(pdf.Pages);
        Assert.Equal(weather, AppData.Read<DebugDataRun.Manifest>(Path.Combine(folder, "run.json"))!.Settings.ImportPath);
        // Optional retained fixture for visual QA; ordinary test runs leave no artifacts.
        if (Environment.GetEnvironmentVariable("SOLARSHADE_PDF_QA") is { Length: > 0 } qa)
        { Directory.CreateDirectory(qa); File.Copy(Path.Combine(folder, "Summary.pdf"), Path.Combine(qa, "long-unicode-summary.pdf"), true); }
    }
}
