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
public sealed class AppServices : IIrradianceService
{
    internal const string MaskCacheSchema = "mask-orchestration-v1";
    // Bump the orchestration schema when scientific sequencing changes. UI/PV builds must not evict upstream caches.
    internal static string SoftwareFingerprint => AppData.Key(new { Schema = "irradiance-orchestration-v1", Libraries = new[] { typeof(CalibrationWorkflow), typeof(CameraProfileFiles),
        typeof(IrradianceClient), typeof(TranspositionModule), typeof(SolarPositionModule), typeof(ShadingCorrectionModule), typeof(SkyPhotoMasker) }.Select(AppData.LibraryVersion).ToArray() });
    public static readonly SemaphoreSlim NativeGate = new(1, 1);
    private readonly SemaphoreSlim evaluationGate = new(1, 1);
    private readonly object inputGate = new();
    private InputDependencies? currentInputs;
    private ArtifactGroup invalidDraft;
    private TrackedRun? activeRun;
    private DebugDataStore? storage;
    private DebugDataStore Storage => storage ??= new(debugRoot ?? DebugDataRun.DefaultRoot);
    public UserSettings PrepareInputs(UserSettings settings) { lock (inputGate) { ObjectDisposedException.ThrowIf(disposeRequested, this); return Storage.PrepareInputs(settings); } }
    private CurrentValueCache Values => new(debugRoot ?? DebugDataRun.DefaultRoot);
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
    private PreparedTranspositionResult? transposedCache;
    private string? transposedKey, resultKey;
    private PanelRun? resultCache;
    private bool disposeRequested, disposed;
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
            if (activeRun is { } run)
            {
                var dirty = run.Inputs.Difference(next) | forced | (refreshWeather ? InputDependencies.WeatherChain : ArtifactGroup.None);
                changed |= dirty & ~run.Dirty;
                run.Dirty |= dirty;
                if (run.Dirty != ArtifactGroup.None) run.Cancellation.Cancel();
            }
            else if (Storage.Current is { } debug)
            {
                var dirty = debug.Differences(next) | forced | (refreshWeather ? InputDependencies.WeatherChain : ArtifactGroup.None);
                if (verifyArtifacts) dirty |= InputDependencies.WithDependents(debug.ChangedArtifacts(true));
                changed |= dirty & ~debug.Invalidated;
                debug.Invalidate(dirty, next.SourcePaths);
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

    private readonly Dictionary<string, DependentResultStore> dependentStores = new(StringComparer.Ordinal);
    public DependentResultStore CreateDependentStore(string folder, string software, params string[] requiredFiles)
    {
        lock (inputGate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            if (!dependentStores.TryGetValue(folder, out var dependent))
            {
                dependent = new(Storage, folder, software, requiredFiles, WithCurrentSource);
                dependentStores.Add(folder, dependent);
            }
            return dependent;
        }
    }
    private void WithCurrentSource(IrradianceSnapshot source, Action action)
    {
        // Maintain the established input -> storage lock order. Optional publication cannot race upstream invalidation.
        lock (inputGate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            lock (Storage.Gate)
            {
                if (!IsCurrent(source.Evaluation) || source.Evaluation.ManagedDebugRun?.HasCompleteDataset != true)
                    throw new InvalidOperationException("Solar Irradiance changed. Update it before using dependent results.");
                using var inputs = SnapshotExport.LockInputs(source.Settings);
                source.Evaluation.ManagedDebugRun.ExportState();
                action();
            }
        }
    }

    public async Task<Evaluation> Evaluate(UserSettings s, bool refresh, Action<string> progress,
        Action<MaskAsset>? onMask, CancellationToken ct, Action<SunPathOverlayResult?>? onSunPath = null, Action<CardinalDirectionOverlayResult?>? onCardinals = null)
    {
        await evaluationGate.WaitAsync(ct);
        DebugDataRun? debug = null;
        TrackedRun? tracked = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = linked.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            s = PrepareInputs(s);
            var persisted = PortablePaths.Map(s, path => PortablePaths.Store(path));
            s = PortablePaths.Map(s, path => PortablePaths.Resolve(path));
            InputDependencies dependencies;
            lock (inputGate)
            {
                ObserveInputs(s, refreshSources: true, verifyArtifacts: true, refreshWeather: refresh);
                dependencies = InputDependencies.Capture(s);
                debug = new(Storage, persisted); tracked = new(debug, dependencies, linked); activeRun = tracked;
            }
            var timer = Stopwatch.StartNew();
            var (activeMask, activeProfile, activeCardinals) = await PrepareImageStages(s, debug, progress, onMask, onCardinals, ct);
            var zone = TimeZoneSelection.Resolve(s.Zone);
            string nextRaw = dependencies.Keys[ArtifactGroup.Irradiance];
            debug.RecordInput("irradiance-request", new { Fingerprint = nextRaw });
            bool reuseRaw = raw != null && rawKey == nextRaw && !refresh;
            string rawDirectory = debug.BeginStage("03-irradiance", reuseRaw ? "Reused in-memory dataset" : string.IsNullOrEmpty(s.ImportPath) ? "Provider or validated disk cache" : "Imported", typeof(IrradianceClient));
            if (!reuseRaw)
            {
                IrradianceDataset next;
                if (!refresh && !debug.NeedsWrite(ArtifactGroup.Irradiance))
                {
                    next = await Task.Run(() => IrradianceDatasetFiles.Import(Path.Combine(debug.DirectoryPath, "03-irradiance", "horizontal-irradiance.xlsx"),
                        TimeZoneInfo.Utc, TimeSpan.Zero, TimestampLabel.Explicit, ct), ct);
                    debug.Origin("03-irradiance", "Reused published source dataset");
                }
                else if (string.IsNullOrEmpty(s.ImportPath))
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
            var rawArtifacts = debug.NeedsWrite(ArtifactGroup.Irradiance)
                ? new[] { (await Task.Run(() => IrradianceDatasetFiles.Export(raw!, Path.Combine(rawDirectory, "horizontal-irradiance.xlsx"), ct), ct)).OutputPath! } : [];
            var selected = IrradianceDatasets.SelectCalendarRange(raw!, s.Start, s.End, zone);
            debug.RecordInput("source-dataset", new { Fingerprint = IrradianceDatasets.Fingerprint(raw!), raw!.Source, raw.FetchedUtc });
            debug.CompleteStage("03-irradiance", rawArtifacts);

            var site = new SolarSite(s.Latitude, s.Longitude, s.Elevation);
            string nextGeometry = AppData.Key(new { Dataset = IrradianceDatasets.Fingerprint(selected), site, s.Substeps,
                Library = AppData.LibraryVersion(typeof(SolarPositionModule)), IrradianceLibrary = AppData.LibraryVersion(typeof(IrradianceDatasets)),
                AngleLibrary = AppData.LibraryVersion(typeof(SunPosition)) });
            if (!refresh && (geometry == null || geometryKey != nextGeometry))
            { geometry = await Task.Run(() => Values.Read<SolarTimeline>("solar", nextGeometry), ct); geometryKey = nextGeometry; }
            debug.RecordInput("solar-timeline", new { Fingerprint = nextGeometry });
            bool reuseGeometry = geometry != null && geometryKey == nextGeometry && !refresh;
            string solarDirectory = debug.BeginStage("04-solar-positions", reuseGeometry ? "Reused cached solar timeline" : "Computed", typeof(SolarPositionModule));
            if (!reuseGeometry)
            {
                progress("Preparing solar positions for source intervals");
                var next = await Task.Run(() => new SolarPositionModule(site).PrepareIntervals(selected, s.Substeps, ct), ct);
                ct.ThrowIfCancellationRequested(); geometry = next; geometryKey = nextGeometry; PreparationCount++;
                await Task.Run(() => Values.Write("solar", nextGeometry, next), ct);
            }
            SunPathOverlayResult? activeSunPath = null;
            if (activeMask != null && activeProfile != null)
            {
                string nextSunPath = AppData.Key(new { Solar = nextGeometry, profileKey,
                    activeMask.Result.Width, activeMask.Result.Height, activeMask.Disk,
                    s.BottomAzimuth, s.CameraTilt, s.CameraRoll, s.Zone,
                    Library = AppData.LibraryVersion(typeof(SunPathOverlayGenerator)) });
                if (sunPath == null || sunPathKey != nextSunPath)
                { sunPath = await Task.Run(() => Values.Read<SunPathOverlayResult>("sun-path", nextSunPath), ct); sunPathKey = nextSunPath; }
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
                    await Task.Run(() => Values.Write("sun-path", nextSunPath, next), ct);
                }
                activeSunPath = sunPath;
            }
            ct.ThrowIfCancellationRequested();
            onSunPath?.Invoke(activeSunPath);
            // Present the already-rendered debug image before writing large numerical diagnostics.
            var solarArtifacts = new List<string>();
            if (activeSunPath != null && debug.NeedsWrite(ArtifactGroup.SunPath))
                solarArtifacts.AddRange(await Task.Run(() => SunPathOverlayExporter.Export(activeSunPath, solarDirectory, ct), ct));
            if (debug.NeedsWrite(ArtifactGroup.Solar)) solarArtifacts.AddRange(await Task.Run(() => SolarIntervalWorkbook.Export(geometry!, site, solarDirectory, ct), ct));
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
            string nextTransposed = AppData.Key(new { Solar = nextGeometry, s.PanelTilt, s.PanelAzimuth, s.Isotropic });
            if (!refresh && (transposedCache == null || transposedKey != nextTransposed))
            { transposedCache = await Task.Run(() => Values.Read<PreparedTranspositionResult>("panel", nextTransposed), ct); transposedKey = nextTransposed; }
            if (transposedCache == null || transposedKey != nextTransposed || refresh)
            {
                var next = await Task.Run(() => TranspositionModule.ComputePrepared(geometry!, new(s.PanelTilt, s.PanelAzimuth),
                    s.Isotropic ? DiffuseModel.Isotropic : DiffuseModel.HayDavies, ct: ct), ct);
                ct.ThrowIfCancellationRequested(); transposedCache = next; transposedKey = nextTransposed;
                await Task.Run(() => Values.Write("panel", nextTransposed, next), ct);
            }
            var panelArtifacts = debug.NeedsWrite(ArtifactGroup.Transposition)
                ? new[] { (await Task.Run(() => TranspositionModule.ExportPrepared(transposedCache, Path.Combine(transpositionDirectory, "panel-unshaded.xlsx"), ct), ct)).OutputPath! } : [];
            debug.CompleteStage("05-transposition", panelArtifacts);

            string correctionDirectory = debug.BeginStage("06-shading", scene == null ? "Shading unavailable; unshaded summary only" : "Computed", typeof(ShadingCorrectionModule));
            progress("Applying panel shading and saving library results");
            string nextResult = AppData.Key(new { Panel = nextTransposed, Camera = dependencies.Keys[ArtifactGroup.Orientation] });
            if (!refresh && (resultCache == null || resultKey != nextResult))
            { resultCache = await Task.Run(() => Values.Read<PanelRun>("shading", nextResult), ct); resultKey = nextResult; }
            if (resultCache == null || resultKey != nextResult || refresh)
            {
                var next = await Task.Run(() => correctPanel(transposedCache, scene, ct), ct);
                ct.ThrowIfCancellationRequested(); resultCache = next; resultKey = nextResult;
                await Task.Run(() => Values.Write("shading", nextResult, next), ct);
            }
            var result = resultCache;
            debug.CompleteStage("06-shading", debug.NeedsWrite(ArtifactGroup.Shading)
                ? await Task.Run(() => ShadingCorrectionModule.ExportPanel(result, correctionDirectory, ct), ct) : [], skipped: scene == null);
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
                        activeRun = null;
                        // Native/export calls have finished: no obsolete writer can recreate removed files.
                        tracked.Debug.Invalidate(tracked.Dirty, tracked.Inputs.SourcePaths.Concat(currentInputs?.SourcePaths ?? []));
                        tracked.Debug.Finish();
                    }
                }
            }
            finally { ReleaseOperation(); }
        }
    }

    /// <summary>Image orientation is independent of the irradiance source, dates, site and panel.</summary>
    public async Task<OrientationPreview> PrepareOrientation(UserSettings settings, Action<string> progress,
        Action<MaskAsset>? onMask, CancellationToken ct, Action<CardinalDirectionOverlayResult?>? onCardinals = null)
    {
        await evaluationGate.WaitAsync(ct);
        DebugDataRun? debug = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); ct = linked.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            settings = PrepareInputs(settings);
            var persisted = PortablePaths.Map(settings, path => PortablePaths.Store(path));
            var resolved = PortablePaths.Map(settings, path => PortablePaths.Resolve(path));
            lock (inputGate)
            {
                ObserveInputs(settings, refreshSources: true, verifyArtifacts: true);
                debug = new(Storage, persisted, "orientation"); activeRun = new(debug, InputDependencies.Capture(settings), linked);
            }
            var (activeMask, _, overlay) = await PrepareImageStages(resolved, debug, progress, onMask, onCardinals, ct);
            ct.ThrowIfCancellationRequested(); debug.Complete();
            return new(activeMask, overlay, debug.DirectoryPath);
        }
        catch (Exception ex) { debug?.Status(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message); throw; }
        finally
        {
            try { lock (inputGate) { if (activeRun != null) { debug!.Invalidate(activeRun.Dirty, activeRun.Inputs.SourcePaths); activeRun = null; } debug?.Finish(); } }
            finally { ReleaseOperation(); }
        }
    }

    private async Task<(MaskAsset? Mask, CalibrationProfile? Profile, CardinalDirectionOverlayResult? Cardinals)> PrepareImageStages(
        UserSettings s, DebugDataRun debug, Action<string> progress, Action<MaskAsset>? onMask,
        Action<CardinalDirectionOverlayResult?>? onCardinals, CancellationToken ct)
    {
            MaskAsset? activeMask = null;
            if (!string.IsNullOrEmpty(s.SkyImage))
            {
                string nextMask = AppData.Key(new { Image = AppData.FileKey(s.SkyImage), s.Model, s.Resolution, s.CenteredDisk,
                    Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppServices.MaskCacheSchema });
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
                debug.CompleteStage("02-sky-mask", debug.NeedsWrite(ArtifactGroup.Mask) ? await Task.Run(() => SkyMaskExporter.Export(activeMask.Result, directory, maskOrigin, ct), ct) : []);
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
                debug.CompleteStage("01-calibration", debug.NeedsWrite(ArtifactGroup.Profile) ? await Task.Run(() => CameraProfileFiles.Export(activeProfile, directory, ct), ct) : []);
            }
            else debug.Skip("01-calibration", "Effective calibration requires a selected profile and sky photograph");

            CardinalDirectionOverlayResult? activeCardinals = null;
            if (activeMask != null && activeProfile != null)
            {
                string nextCardinal = AppData.Key(new { Image = AppData.FileKey(s.SkyImage), profileKey,
                    activeMask.Result.Width, activeMask.Result.Height, activeMask.Disk,
                    s.BottomAzimuth, s.CameraTilt, s.CameraRoll,
                    Library = AppData.LibraryVersion(typeof(CardinalDirectionOverlayGenerator)) });
                if (cardinals == null || cardinalKey != nextCardinal)
                { cardinals = await Task.Run(() => Values.Read<CardinalDirectionOverlayResult>("cardinals", nextCardinal), ct); cardinalKey = nextCardinal; }
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
                    await Task.Run(() => Values.Write("cardinals", nextCardinal, next), ct);
                }
                activeCardinals = cardinals;
                ct.ThrowIfCancellationRequested(); onCardinals?.Invoke(activeCardinals);
                debug.CompleteStage("02-orientation", debug.NeedsWrite(ArtifactGroup.Orientation) ? await Task.Run(() => CardinalDirectionOverlayExporter.Export(activeCardinals!, directory, ct), ct) : []);
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

    public async Task<(CalibrationProfile Profile, string Path, string Details)> Calibrate(UserSettings s)
    {
        await evaluationGate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        DebugDataRun? debug = null;
        try
        {
            s = PrepareInputs(s);
            if (DebugDataStore.Within(Storage.Root, AppData.Root)) throw new IOException("Data must be outside Debug Data to preserve calibration profiles.");
            await NativeGate.WaitAsync();
            try
            {
            return await Task.Run(() =>
            {
                lock (inputGate)
                {
                    Storage.Current?.Invalidate(ArtifactGroup.Profile | InputDependencies.PoseChain, InputDependencies.Capture(s).SourcePaths);
                    debug = new DebugDataRun(Storage, PortablePaths.Map(s, path => PortablePaths.Store(path)), "calibration");
                    activeRun = new(debug, InputDependencies.Capture(s), cancellation);
                }
                try
                {
                    string directory = debug.BeginStage("01-calibration", "Computed", typeof(CalibrationWorkflow));
                    string sourceDirectory = PortablePaths.Resolve(s.CalibrationFolder);
                    debug.RecordInput("calibration-files", Directory.EnumerateFiles(sourceDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => new { File = Path.GetFileName(path), Fingerprint = AppData.FileKey(path) }).ToArray());
                    var stage = CalibrationWorkflow.Calibrate(sourceDirectory, directory,
                        CheckerboardDetectionSettings.CreateFastDefault() with { InnerColumns = s.Columns, InnerRows = s.Rows, SquareSizeMillimetres = s.SquareMm }, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    string durable = DebugDataStore.Preserve(directory, "Profiles");
                    lock (inputGate)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        debug.CompleteStage("01-calibration", stage.Artifacts);
                        debug.SetCalibrationProfile(Path.Combine(durable, "camera-profile.json")); debug.Complete();
                    }
                    var calibration = stage.Profile.Calibration;
                    return (stage.Profile, Path.Combine(durable, "camera-profile.json"),
                        $"{calibration.UsedImageCount} boards · RMSE {calibration.RmsError:F2} px · fitted coverage {calibration.MaximumIncidentAngleDegrees:F2}°");
                }
                catch (Exception ex) { debug.Status(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message); throw; }
            });
            }
            finally { NativeGate.Release(); }
        }
        finally
        {
            try { lock (inputGate) { if (activeRun != null) { debug!.Invalidate(activeRun.Dirty, activeRun.Inputs.SourcePaths); activeRun = null; } debug?.Finish(); } }
            finally { ReleaseOperation(); }
        }
    }

    public static string Export(Evaluation evaluation, string directory)
    {
        if (evaluation.DebugDirectory == null) throw new InvalidOperationException("The result has no completed library artifacts.");
        if (evaluation.Dependencies == null || evaluation.ManagedDebugRun == null) throw new InvalidOperationException("Result provenance is unavailable; update results before exporting.");
        string destination = Path.Combine(directory, $"scenario-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        evaluation.ManagedDebugRun.CopyCurrent(destination);
        return destination;
    }
    private void ReleaseOperation()
    {
        lock (inputGate) { evaluationGate.Release(); if (disposeRequested) DisposeResources(); }
    }
    private void DisposeResources()
    { if (disposed) return; disposed = true; masker?.Dispose(); storage?.Dispose(); }
    public void Dispose()
    {
        lock (inputGate) { disposeRequested = true; activeRun?.Cancellation.Cancel(); if (evaluationGate.CurrentCount > 0) DisposeResources(); }
    }
}
