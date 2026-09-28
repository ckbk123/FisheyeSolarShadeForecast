using System.Diagnostics;
using SolarShade.Calibration.Validation;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;
using SolarShade.SkyPhotoMasking;
using OpenCvSharp;

namespace SolarShade.Desktop;

public sealed record MaskAsset(byte[] OriginalPreview, byte[] MaskPreview, SkyMaskResult Result, string Description)
{
    public byte[] Png => Result.Png;
    public LensDisk Disk => Result.Disk;
}
public sealed record OrientationPreview(MaskAsset? Mask, CardinalDirectionOverlayResult? Cardinals, string DebugDirectory);

public sealed record Evaluation(PanelRun Run, IrradianceDataset Raw, UserSettings Settings, MaskAsset? Mask,
    string Note, double TotalMilliseconds, int PreparationCount)
{
    public string? DebugDirectory { get; init; }
    public SunPathOverlayResult? SunPath { get; init; }
    public CardinalDirectionOverlayResult? Cardinals { get; init; }
    public int CardinalGenerationCount { get; init; }
    public int SunPathGenerationCount { get; init; }
    public InputDependencies? Dependencies { get; init; }
    internal DebugDataRun? ManagedDebugRun { get; init; }
}

/// <summary>UI stage sequencing and cache coordination. All scientific work and artifacts are library-owned.</summary>
public sealed class AppServices : IDisposable
{
    public static readonly SemaphoreSlim NativeGate = new(1, 1);
    private readonly SemaphoreSlim evaluationGate = new(1, 1);
    private readonly object inputGate = new();
    private InputDependencies? currentInputs;
    private ArtifactGroup invalidDraft;
    private TrackedRun? activeRun, lastRun;
    private sealed class TrackedRun(DebugDataRun debug, InputDependencies inputs, CancellationTokenSource cancellation)
    {
        public DebugDataRun Debug { get; } = debug;
        public InputDependencies Inputs { get; } = inputs;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ArtifactGroup Dirty { get; set; }
    }
    private readonly string? debugRoot;
    private readonly Func<PreparedTranspositionResult, PanelSkyScene?, CancellationToken, PanelRun> correctPanel;
    private SkyPhotoMasker? masker;
    private string? maskerKey, maskKey, rawKey, sceneKey, geometryKey, profileKey, sunPathKey, cardinalKey;
    private MaskAsset? mask;
    private IrradianceDataset? raw;
    private CalibrationProfile? profile;
    private PanelSkyScene? scene;
    private SolarTimeline? geometry;
    private SunPathOverlayResult? sunPath;
    private CardinalDirectionOverlayResult? cardinals;
    public int CardinalGenerationCount { get; private set; }
    public int PreparationCount { get; private set; }
    public int SunPathGenerationCount { get; private set; }

    public AppServices(string? debugRoot = null,
        Func<PreparedTranspositionResult, PanelSkyScene?, CancellationToken, PanelRun>? correctPanel = null)
    {
        this.debugRoot = debugRoot;
        this.correctPanel = correctPanel ?? ShadingCorrectionModule.ApplyToPanel;
    }

    public ArtifactGroup ObserveInputs(UserSettings settings, IEnumerable<string>? invalidFields = null,
        bool refreshSources = false, bool verifyArtifacts = false, bool refreshWeather = false)
    {
        lock (inputGate)
        {
            var next = InputDependencies.Capture(settings, currentInputs, refreshSources);
            var forced = (invalidFields ?? []).Aggregate(ArtifactGroup.None, (a, f) => a | InputDependencies.ForField(f, settings));
            var changed = currentInputs == null ? ArtifactGroup.All : currentInputs.Difference(next);
            changed |= forced | invalidDraft;
            invalidDraft = forced; currentInputs = next;
            foreach (var run in new[] { lastRun, activeRun }.OfType<TrackedRun>().Distinct())
            {
                var dirty = run.Inputs.Difference(next) | forced | (refreshWeather ? InputDependencies.WeatherChain : ArtifactGroup.None);
                if (run != activeRun && verifyArtifacts) dirty |= InputDependencies.WithDependents(run.Debug.ChangedArtifacts(checkContents: true));
                changed |= dirty & ~run.Dirty;
                run.Dirty |= dirty;
                if (run == activeRun)
                {
                    if (run.Dirty != ArtifactGroup.None) run.Cancellation.Cancel();
                }
                else run.Debug.Invalidate(run.Dirty, run.Inputs.SourcePaths.Concat(next.SourcePaths));
            }
            return changed;
        }
    }

    public bool IsCurrent(Evaluation evaluation)
    {
        lock (inputGate) return invalidDraft == ArtifactGroup.None && evaluation.Dependencies != null && currentInputs != null
            && evaluation.Dependencies.Difference(currentInputs) == ArtifactGroup.None
            && evaluation.ManagedDebugRun?.IsCurrent == true;
    }

    public async Task<Evaluation> Evaluate(UserSettings s, bool refresh, Action<string> progress,
        Action<MaskAsset>? onMask, CancellationToken ct, Action<SunPathOverlayResult?>? onSunPath = null, Action<CardinalDirectionOverlayResult?>? onCardinals = null)
    {
        var persisted = PortablePaths.Map(s, path => PortablePaths.Store(path));
        s = PortablePaths.Map(s, path => PortablePaths.Resolve(path));
        await evaluationGate.WaitAsync(ct);
        DebugDataRun? debug = null;
        TrackedRun? tracked = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = linked.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            ObserveInputs(s, refreshSources: true, verifyArtifacts: true, refreshWeather: refresh);
            var dependencies = InputDependencies.Capture(s);
            debug = new(persisted, root: debugRoot);
            lock (inputGate) { tracked = new(debug, dependencies, linked); activeRun = tracked; }
            var timer = Stopwatch.StartNew();
            var (activeMask, activeProfile, activeCardinals) = await PrepareImageStages(s, debug, progress, onMask, onCardinals, ct);
            var zone = TimeZoneSelection.Resolve(s.Zone);
            string nextRaw = AppData.Key(new { s.ImportPath, File = string.IsNullOrEmpty(s.ImportPath) ? "" : AppData.FileKey(s.ImportPath),
                s.ImportWindow, s.ImportIntervalMinutes, s.Zone, s.Start, s.End, s.Provider, s.Latitude, s.Longitude,
                Library = AppData.LibraryVersion(typeof(IrradianceClient)) });
            debug.RecordInput("irradiance-request", new { Fingerprint = nextRaw });
            bool reuseRaw = raw != null && rawKey == nextRaw && !refresh;
            string rawDirectory = debug.BeginStage("03-irradiance", reuseRaw ? "Reused in-memory dataset" : string.IsNullOrEmpty(s.ImportPath) ? "Provider or validated disk cache" : "Imported", typeof(IrradianceClient));
            if (!reuseRaw)
            {
                IrradianceDataset next;
                if (string.IsNullOrEmpty(s.ImportPath))
                {
                    progress("Retrieving irradiance and its source intervals");
                    var fetched = await FetchWithOrigin(s, refresh, ct);
                    next = fetched.Dataset; debug.Origin("03-irradiance", fetched.Origin);
                }
                else
                {
                    progress("Reading irradiance and interval metadata");
                    var label = s.ImportWindow switch { 1 => TimestampLabel.Start, 2 => TimestampLabel.Center, _ => TimestampLabel.End };
                    next = await Task.Run(() => IrradianceDatasetFiles.Import(s.ImportPath, zone,
                        TimeSpan.FromMinutes(s.ImportIntervalMinutes), label, ct), ct);
                }
                ct.ThrowIfCancellationRequested(); raw = next; rawKey = nextRaw;
            }
            var exportedRaw = await Task.Run(() => IrradianceDatasetFiles.Export(raw!, Path.Combine(rawDirectory, "horizontal-irradiance.xlsx"), ct), ct);
            var selected = IrradianceDatasets.SelectCalendarRange(raw!, s.Start, s.End, zone);
            debug.RecordInput("source-dataset", new { Fingerprint = IrradianceDatasets.Fingerprint(raw!), raw!.Source, raw.FetchedUtc });
            debug.CompleteStage("03-irradiance", [exportedRaw.OutputPath!]);

            var site = new SolarSite(s.Latitude, s.Longitude, s.Elevation);
            string nextGeometry = AppData.Key(new { Dataset = IrradianceDatasets.Fingerprint(selected), site, s.Substeps,
                Library = AppData.LibraryVersion(typeof(SolarPositionModule)), IrradianceLibrary = AppData.LibraryVersion(typeof(IrradianceDatasets)),
                AngleLibrary = AppData.LibraryVersion(typeof(SunPosition)) });
            debug.RecordInput("solar-timeline", new { Fingerprint = nextGeometry });
            bool reuseGeometry = geometry != null && geometryKey == nextGeometry && !refresh;
            string solarDirectory = debug.BeginStage("04-solar-positions", reuseGeometry ? "Reused cached solar timeline" : "Computed", typeof(SolarPositionModule));
            if (!reuseGeometry)
            {
                progress("Preparing solar positions for source intervals");
                var next = await Task.Run(() => new SolarPositionModule(site).PrepareIntervals(selected, s.Substeps, ct), ct);
                ct.ThrowIfCancellationRequested(); geometry = next; geometryKey = nextGeometry; PreparationCount++;
            }
            SunPathOverlayResult? activeSunPath = null;
            if (activeMask != null && activeProfile != null)
            {
                string nextSunPath = AppData.Key(new { Solar = nextGeometry, profileKey,
                    activeMask.Result.Width, activeMask.Result.Height, activeMask.Disk,
                    s.BottomAzimuth, s.CameraTilt, s.CameraRoll, s.Zone,
                    Library = AppData.LibraryVersion(typeof(SunPathOverlayGenerator)) });
                bool reused = sunPath != null && sunPathKey == nextSunPath;
                debug.RecordInput("sun-path-overlay", new { Fingerprint = nextSunPath, Origin = reused ? "Reused in-memory overlay" : "Computed" });
                if (!reused)
                {
                    progress("Drawing sun paths on the calibrated image");
                    var next = await Task.Run(() => SunPathOverlayGenerator.Generate(geometry!,
                        new CalibratedSkyProjection(activeProfile.Calibration,
                            CameraPose.FromImageBottom(s.BottomAzimuth, s.CameraTilt, s.CameraRoll),
                            new(activeMask.Disk.CenterX, activeMask.Disk.CenterY, activeMask.Disk.Radius)), zone, ct: ct), ct);
                    ct.ThrowIfCancellationRequested(); sunPath = next; sunPathKey = nextSunPath; SunPathGenerationCount++;
                }
                activeSunPath = sunPath;
            }
            ct.ThrowIfCancellationRequested();
            onSunPath?.Invoke(activeSunPath);
            // Present the already-rendered debug image before writing large numerical diagnostics.
            var solarArtifacts = new List<string>();
            if (activeSunPath != null)
                solarArtifacts.AddRange(await Task.Run(() => SunPathOverlayExporter.Export(activeSunPath, solarDirectory, ct), ct));
            solarArtifacts.AddRange(await Task.Run(() => SolarIntervalWorkbook.Export(geometry!, site, solarDirectory, ct), ct));
            debug.CompleteStage("04-solar-positions", solarArtifacts);

            if (activeMask != null && activeProfile != null)
            {
                string nextScene = AppData.Key(new { maskKey, profileKey, s.BottomAzimuth, s.CameraTilt, s.CameraRoll,
                    Library = AppData.LibraryVersion(typeof(PanelSkyScene)) });
                debug.RecordInput("camera-scene", new { Fingerprint = nextScene });
                if (scene == null || sceneKey != nextScene)
                {
                    progress("Preparing panel visibility");
                    var next = await Task.Run(() => new PanelSkyScene(SkyShadingModule.DecodeMask(activeMask.Png), activeProfile.Calibration,
                        CameraPose.FromImageBottom(s.BottomAzimuth, s.CameraTilt, s.CameraRoll),
                        new(activeMask.Disk.CenterX, activeMask.Disk.CenterY, activeMask.Disk.Radius)), ct);
                    ct.ThrowIfCancellationRequested(); scene = next; sceneKey = nextScene;
                }
            }
            else { scene = null; sceneKey = null; }

            string transpositionDirectory = debug.BeginStage("05-transposition", "Computed", typeof(TranspositionModule));
            progress("Calculating irradiance on the panel");
            var transposed = await Task.Run(() => TranspositionModule.ComputePrepared(geometry!, new(s.PanelTilt, s.PanelAzimuth),
                s.Isotropic ? DiffuseModel.Isotropic : DiffuseModel.HayDavies, ct: ct), ct);
            var exportedPanel = await Task.Run(() => TranspositionModule.ExportPrepared(transposed, Path.Combine(transpositionDirectory, "panel-unshaded.xlsx"), ct), ct);
            debug.CompleteStage("05-transposition", [exportedPanel.OutputPath!]);

            string correctionDirectory = debug.BeginStage("06-shading", scene == null ? "Shading unavailable; unshaded summary only" : "Computed", typeof(ShadingCorrectionModule));
            progress("Applying panel shading and saving library results");
            var result = await Task.Run(() => correctPanel(transposed, scene, ct), ct);
            debug.CompleteStage("06-shading", await Task.Run(() => ShadingCorrectionModule.ExportPanel(result, correctionDirectory, ct), ct), skipped: scene == null);
            lock (inputGate)
            {
                tracked.Dirty |= dependencies.Difference(InputDependencies.Capture(s));
                if (tracked.Dirty != ArtifactGroup.None) throw new OperationCanceledException("Inputs changed during the update.", ct);
                ct.ThrowIfCancellationRequested(); debug.Complete();
            }
            string note = scene == null ? "Add a calibrated camera profile and sky photo for shading." : "Unknown sky is treated as blocked. No ground reflection. Camera coverage may be provisional.";
            return new(result, raw!, persisted, activeMask, note, timer.Elapsed.TotalMilliseconds, PreparationCount)
                { DebugDirectory = debug.DirectoryPath, SunPath = activeSunPath, SunPathGenerationCount = SunPathGenerationCount, Cardinals = activeCardinals, CardinalGenerationCount = CardinalGenerationCount,
                    Dependencies = dependencies, ManagedDebugRun = debug };
        }
        catch (Exception ex)
        {
            try { debug?.Status(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message); }
            catch (Exception writeError) { throw new AggregateException("Calculation and Debug Data status write failed.", ex, writeError); }
            throw;
        }
        finally
        {
            try
            {
                lock (inputGate)
                {
                    if (tracked != null)
                    {
                        activeRun = null; lastRun = tracked;
                        // Native/export calls have finished: no obsolete writer can recreate removed files.
                        tracked.Debug.Invalidate(tracked.Dirty, tracked.Inputs.SourcePaths.Concat(currentInputs?.SourcePaths ?? []));
                    }
                }
            }
            finally { evaluationGate.Release(); }
        }
    }

    /// <summary>Image orientation is independent of the irradiance source, dates, site and panel.</summary>
    public async Task<OrientationPreview> PrepareOrientation(UserSettings settings, Action<string> progress,
        Action<MaskAsset>? onMask, CancellationToken ct, Action<CardinalDirectionOverlayResult?>? onCardinals = null)
    {
        var persisted = PortablePaths.Map(settings, path => PortablePaths.Store(path));
        var resolved = PortablePaths.Map(settings, path => PortablePaths.Resolve(path));
        await evaluationGate.WaitAsync(ct);
        DebugDataRun? debug = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            debug = new(persisted, "orientation", debugRoot);
            var (activeMask, _, overlay) = await PrepareImageStages(resolved, debug, progress, onMask, onCardinals, ct);
            ct.ThrowIfCancellationRequested(); debug.Complete();
            return new(activeMask, overlay, debug.DirectoryPath);
        }
        catch (Exception ex) { debug?.Status(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message); throw; }
        finally { evaluationGate.Release(); }
    }

    private async Task<(MaskAsset? Mask, CalibrationProfile? Profile, CardinalDirectionOverlayResult? Cardinals)> PrepareImageStages(
        UserSettings s, DebugDataRun debug, Action<string> progress, Action<MaskAsset>? onMask,
        Action<CardinalDirectionOverlayResult?>? onCardinals, CancellationToken ct)
    {
            MaskAsset? activeMask = null;
            if (!string.IsNullOrEmpty(s.SkyImage))
            {
                string nextMask = AppData.Key(new { Image = AppData.FileKey(s.SkyImage), s.Model, s.Resolution, s.CenteredDisk,
                    Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppData.LibraryVersion(typeof(Program)) });
                debug.RecordInput("sky-mask", new { Fingerprint = nextMask });
                bool reusedMask = mask != null && maskKey == nextMask;
                string maskOrigin = reusedMask ? "Reused in-memory mask" : "Computed or loaded validated mask cache";
                string directory = debug.BeginStage("02-sky-mask", maskOrigin, typeof(SkyPhotoMasker));
                if (!reusedMask)
                {
                    progress("Preparing sky mask");
                    await NativeGate.WaitAsync(ct);
                    try
                    {
                        var next = await Task.Run(() =>
                        {
                            string cache = AppData.PathFor(Path.Combine("mask-stages", nextMask));
                            SkyMaskResult result;
                            if (File.Exists(Path.Combine(cache, "sky-mask.png")) && File.Exists(Path.Combine(cache, "mask-details.json")))
                            { result = SkyMaskExporter.Load(cache); maskOrigin = "Reused validated disk mask cache"; }
                            else
                            {
                                string modelDirectory = AppData.EnsureModel(s.Model);
                                string key = AppData.Key(new { modelDirectory, s.Resolution, s.CenteredDisk });
                                if (key != maskerKey)
                                {
                                    masker?.Dispose();
                                    masker = new(new() { ModelsDirectory = modelDirectory, InputSize = s.Resolution,
                                        DiskDetection = s.CenteredDisk ? DiskDetection.Centered : DiskDetection.Auto,
                                        Acceleration = Environment.GetEnvironmentVariable("SOLARSHADE_FORCE_CPU") == "1" ? MaskAcceleration.Cpu : MaskAcceleration.Auto });
                                    maskerKey = key;
                                }
                                result = masker!.CreateMaskDetailed(s.SkyImage, s.Model); maskOrigin = "Computed by SkyPhotoMasker";
                                SkyMaskExporter.Export(result, cache, "Computed");
                            }
                            using var image = Cv2.ImRead(s.SkyImage, ImreadModes.Color);
                            using var binary = Cv2.ImDecode(result.Png, ImreadModes.Grayscale);
                            return new MaskAsset(Preview(image), Preview(binary), result,
                                $"{result.Width} × {result.Height} · {result.Model} · {result.ExecutionProvider}");
                        }, CancellationToken.None);
                        ct.ThrowIfCancellationRequested(); mask = next; maskKey = nextMask;
                    }
                    finally { NativeGate.Release(); }
                }
                activeMask = mask!; debug.Origin("02-sky-mask", maskOrigin);
                debug.CompleteStage("02-sky-mask", await Task.Run(() => SkyMaskExporter.Export(activeMask.Result, directory, maskOrigin, ct), ct));
                onMask?.Invoke(activeMask);
            }
            else debug.Skip("02-sky-mask", "No sky photograph selected");

            CalibrationProfile? activeProfile = null;
            if (!string.IsNullOrEmpty(s.ProfilePath) && activeMask != null)
            {
                string nextProfile = AppData.Key(new { File = AppData.FileKey(s.ProfilePath), s.CoverageAngle,
                    Width = activeMask?.Result.Width, Height = activeMask?.Result.Height, Library = AppData.LibraryVersion(typeof(CameraProfileFiles)) });
                debug.RecordInput("camera-profile", new { Fingerprint = nextProfile });
                string directory = debug.BeginStage("01-calibration", "Loaded effective camera profile", typeof(CameraProfileFiles));
                if (profileKey != nextProfile || profile == null)
                {
                    var next = await Task.Run(() => CameraProfileFiles.Load(s.ProfilePath, activeMask?.Result.Width ?? 0,
                        activeMask?.Result.Height ?? 0, s.CoverageAngle > 0 ? s.CoverageAngle : null), ct);
                    ct.ThrowIfCancellationRequested(); profile = next; profileKey = nextProfile;
                }
                activeProfile = profile;
                debug.CompleteStage("01-calibration", await Task.Run(() => CameraProfileFiles.Export(activeProfile, directory, ct), ct));
            }
            else debug.Skip("01-calibration", "Effective calibration requires a selected profile and sky photograph");

            CardinalDirectionOverlayResult? activeCardinals = null;
            if (activeMask != null && activeProfile != null)
            {
                string nextCardinal = AppData.Key(new { Image = AppData.FileKey(s.SkyImage), profileKey,
                    activeMask.Result.Width, activeMask.Result.Height, activeMask.Disk,
                    s.BottomAzimuth, s.CameraTilt, s.CameraRoll,
                    Library = AppData.LibraryVersion(typeof(CardinalDirectionOverlayGenerator)) });
                bool reused = cardinals != null && cardinalKey == nextCardinal;
                debug.RecordInput("cardinal-directions", new { Fingerprint = nextCardinal });
                string directory = debug.BeginStage("02-orientation", reused ? "Reused in-memory orientation overlay" : "Computed", typeof(CardinalDirectionOverlayGenerator));
                if (!reused)
                {
                    progress("Drawing cardinal directions on the sky photograph");
                    var next = await Task.Run(() => CardinalDirectionOverlayGenerator.Generate(
                        new CalibratedSkyProjection(activeProfile.Calibration,
                            CameraPose.FromImageBottom(s.BottomAzimuth, s.CameraTilt, s.CameraRoll),
                            new(activeMask.Disk.CenterX, activeMask.Disk.CenterY, activeMask.Disk.Radius)), ct: ct), ct);
                    ct.ThrowIfCancellationRequested(); cardinals = next; cardinalKey = nextCardinal; CardinalGenerationCount++;
                }
                activeCardinals = cardinals;
                ct.ThrowIfCancellationRequested(); onCardinals?.Invoke(activeCardinals);
                debug.CompleteStage("02-orientation", await Task.Run(() => CardinalDirectionOverlayExporter.Export(activeCardinals!, directory, ct), ct));
            }
            else
            {
                debug.Skip("02-orientation", "Cardinal directions require a calibrated sky photograph");
                ct.ThrowIfCancellationRequested(); onCardinals?.Invoke(null);
            }
            return (activeMask, activeProfile, activeCardinals);
    }

    public static async Task<IrradianceDataset> Fetch(UserSettings s, bool refresh, CancellationToken ct) => (await FetchWithOrigin(s, refresh, ct)).Dataset;

    private static async Task<(IrradianceDataset Dataset, string Origin)> FetchWithOrigin(UserSettings s, bool refresh, CancellationToken ct)
    {
        string key = AppData.Key(new { s.Start, s.End, s.Longitude, s.Latitude, s.Provider, s.Zone, Library = AppData.LibraryVersion(typeof(IrradianceClient)) });
        string path = AppData.PathFor(Path.Combine("weather-stages", key + ".xlsx"));
        if (!refresh && File.Exists(path)) return (IrradianceDatasetFiles.Import(path, TimeZoneInfo.Utc, TimeSpan.Zero, TimestampLabel.Explicit, ct), "Reused validated disk weather cache");
        var service = s.Provider == 0 ? IrradianceService.NasaPower : IrradianceService.OpenMeteo;
        var data = await new IrradianceClient(new() { TimeZone = TimeZoneSelection.Resolve(s.Zone) }).FetchDatasetAsync(
            DateOnly.FromDateTime(s.Start), DateOnly.FromDateTime(s.End), s.Longitude, s.Latitude, service, ct);
        ct.ThrowIfCancellationRequested(); IrradianceDatasetFiles.Export(data, path, ct); return (data, "Fetched from irradiance service");
    }

    public static byte[] Preview(Mat source)
    {
        using var small = new Mat(); double scale = Math.Min(1, 1000.0 / Math.Max(source.Width, source.Height));
        Cv2.Resize(source, small, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
        Cv2.ImEncode(".png", small, out var bytes); return bytes;
    }

    public static async Task<(CalibrationProfile Profile, string Path, string Details)> Calibrate(UserSettings s)
    {
        await NativeGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var debug = new DebugDataRun(PortablePaths.Map(s, path => PortablePaths.Store(path)), "calibration");
                try
                {
                    string directory = debug.BeginStage("01-calibration", "Computed", typeof(CalibrationWorkflow));
                    string sourceDirectory = PortablePaths.Resolve(s.CalibrationFolder);
                    debug.RecordInput("calibration-files", Directory.EnumerateFiles(sourceDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => new { File = Path.GetFileName(path), Fingerprint = AppData.FileKey(path) }).ToArray());
                    var stage = CalibrationWorkflow.Calibrate(sourceDirectory, directory,
                        CheckerboardDetectionSettings.CreateFastDefault() with { InnerColumns = s.Columns, InnerRows = s.Rows, SquareSizeMillimetres = s.SquareMm });
                    debug.CompleteStage("01-calibration", stage.Artifacts); debug.Complete();
                    var calibration = stage.Profile.Calibration;
                    return (stage.Profile, Path.Combine(directory, "camera-profile.json"),
                        $"{calibration.UsedImageCount} boards · RMSE {calibration.RmsError:F2} px · fitted coverage {calibration.MaximumIncidentAngleDegrees:F2}°");
                }
                catch (Exception ex) { debug.Status("Failed", ex.Message); throw; }
            });
        }
        finally { NativeGate.Release(); }
    }

    public static void Export(Evaluation evaluation, string directory)
    {
        if (evaluation.DebugDirectory == null) throw new InvalidOperationException("The result has no completed library artifacts.");
        if (evaluation.Dependencies == null || evaluation.ManagedDebugRun == null) throw new InvalidOperationException("Result provenance is unavailable; update results before exporting.");
        var sourceChanges = evaluation.Dependencies.Difference(InputDependencies.Capture(evaluation.Settings));
        if (sourceChanges != ArtifactGroup.None)
        {
            evaluation.ManagedDebugRun.Invalidate(sourceChanges, evaluation.Dependencies.SourcePaths);
            throw new InvalidOperationException("A source file changed. Update results before exporting.");
        }
        evaluation.ManagedDebugRun.CopyCurrent(Path.Combine(directory, $"scenario-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
    }
    public void Dispose() { masker?.Dispose(); evaluationGate.Dispose(); }
}
