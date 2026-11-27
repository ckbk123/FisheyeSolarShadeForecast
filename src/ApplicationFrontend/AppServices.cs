using System.Diagnostics;
using SolarShade.Calibration.Validation;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;
using SolarShade.SkyPhotoMasking;
using OpenCvSharp;
using System.Security.Cryptography;

namespace SolarShade.Desktop;

public sealed record MaskAsset(byte[] OriginalPreview, byte[] MaskPreview, SkyMaskResult Result, string Description, MaskVariant? Variant = null)
{
    public required string PhotoSha256 { get; init; }
    public byte[] Png => Result.Png;
    public LensDisk Disk => Result.Disk;
    public string AiAncestorSha256 => Variant?.AiAncestorSha256 ?? Convert.ToHexString(SHA256.HashData(Png));

    public AppliedMaskEdge SuggestEdge(CalibrationResult measured, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        double measuredFull = 2 * measured.MaximumIncidentAngleDegrees;
        if (measuredFull > CalibrationRadialMap.MaximumRefinedFullDegrees)
            return new(measuredFull, CalibrationRadialMap.MaximumRefinedFullDegrees, null, false,
                "The lens calibration extends beyond the 270° study edge.");
        bool ai = Variant?.IsImported != true;
        var scan = MaskEdgeProbe.AnalyzeTo180(Png, measured.ImageWidth, measured.ImageHeight,
            measured.PrincipalPoint[0], measured.PrincipalPoint[1], measured.IncidentAngleToRadiusPolynomial,
            measured.MaximumIncidentAngleDegrees, Disk, ai ? Result.Crop : null, ct, ai ? Result.InputSize : 0);
        var proposal = scan.Proposal;
        var edge = new AppliedMaskEdge(scan.CalibratedFullDegrees, proposal.EstimatedLimitFullDegrees ?? scan.CalibratedFullDegrees,
            proposal.EstimatedLimitFullDegrees.HasValue ? proposal.BlackPercent : null, proposal.RadiusPixels.HasValue, proposal.Explanation);
        if (!scan.Reached180) return edge;
        var cap = scan.Samples[^1];
        return edge with { ObservedSkyAtCap = MaskEdgeProbe.SamplesPerRing - cap.UnknownSamples - cap.ObservedBlackSamples,
            UnknownAtCap = cap.UnknownSamples };
    }
}
public sealed record AppliedMaskEdge(double MeasuredFullDegrees, double EffectiveFullDegrees, int? BlackPercent,
    bool ObservedTerminalBand, string Explanation)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? ManualFullFovDegrees { get; init; }
    public int? ObservedSkyAtCap { get; init; }
    public int? UnknownAtCap { get; init; }
}

public sealed record OrientationPreview(MaskAsset? Mask, CardinalDirectionOverlayResult? Cardinals, string DebugDirectory)
{
    public AppliedMaskEdge? MaskEdge { get; init; }
}

public sealed record Evaluation(PanelRun Run, IrradianceDataset Raw, UserSettings Settings, MaskAsset? Mask,
    string Note, double TotalMilliseconds, int PreparationCount)
{
    public string? DebugDirectory { get; init; }
    public SunPathOverlayResult? SunPath { get; init; }
    public AppliedMaskEdge? MaskEdge { get; init; }
    public CardinalDirectionOverlayResult? Cardinals { get; init; }
    public int CardinalGenerationCount { get; init; }
    public int SunPathGenerationCount { get; init; }
    public InputDependencies? Dependencies { get; init; }
    internal DebugDataRun? ManagedDebugRun { get; init; }
}

/// <summary>UI stage sequencing and cache coordination. All scientific work and artifacts are library-owned.</summary>
public sealed class AppServices : IIrradianceService
{
    internal const string MaskCacheSchema = "mask-orchestration-v2";
    // Bump the orchestration schema when scientific sequencing changes. UI/PV builds must not evict upstream caches.
    internal static string SoftwareFingerprint => AppData.Key(new { Schema = "irradiance-orchestration-v3", Libraries = new[] { typeof(CalibrationWorkflow), typeof(CameraProfileFiles),
        typeof(IrradianceClient), typeof(TranspositionModule), typeof(SolarPositionModule), typeof(ShadingCorrectionModule), typeof(SkyPhotoMasker) }.Select(AppData.LibraryVersion).ToArray() });
    public static readonly SemaphoreSlim NativeGate = new(1, 1);
    private readonly SemaphoreSlim evaluationGate = new(1, 1);
    private readonly object inputGate = new();
    private InputDependencies? currentInputs;
    private ArtifactGroup invalidDraft;
    private TrackedRun? activeRun;
    private DebugDataStore? storage;
    private DebugDataStore Storage => storage ??= new(debugRoot ?? DebugDataRun.DefaultRoot);
    public UserSettings PrepareInputs(UserSettings settings) { lock (inputGate) { ObjectDisposedException.ThrowIf(disposeRequested, this); return Storage.PrepareInputs(WeatherSourceSelection.Normalize(settings)); } }
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
    private AppliedMaskEdge? profileEdge;
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
        bool refreshSources = false, bool verifyArtifacts = false, bool refreshWeather = false) =>
        ObserveInputs(settings, out _, invalidFields, refreshSources, verifyArtifacts, refreshWeather);

    public ArtifactGroup ObserveInputs(UserSettings settings, out InputDependencies observed, IEnumerable<string>? invalidFields = null,
        bool refreshSources = false, bool verifyArtifacts = false, bool refreshWeather = false)
    {
        using var timing = PerformanceTrace.Phase("input-observation", verifyArtifacts ? "content-verification" : "settings");
        lock (inputGate)
        {
            var next = InputDependencies.Capture(settings, currentInputs, refreshSources);
            var forced = (invalidFields ?? []).Aggregate(ArtifactGroup.None, (a, f) => a | InputDependencies.ForField(f, settings));
            var changed = currentInputs == null ? ArtifactGroup.All : currentInputs.Difference(next);
            changed |= forced | invalidDraft;
            invalidDraft = forced; observed = currentInputs = next;
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
                if (verifyArtifacts) dirty |= InputDependencies.WithDependents(debug.ChangedArtifacts(true), settings);
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
    public Evaluation? TryRestoreAccepted(UserSettings settings)
    {
        lock (inputGate)
        {
            if (activeRun != null || evaluationGate.CurrentCount == 0 || WeatherSourceSelection.Problem(settings) != null) return null;
            var restored = AcceptedResultVault.Restore(Storage, settings);
            if (restored == null) return null;
            var result = restored.Value.Evaluation;
            MaskAsset? asset = null;
            if (!string.IsNullOrEmpty(settings.SkyImage))
            {
                var maskResult = SkyMaskExporter.Load(Path.Combine(Storage.Root, "02-sky-mask"));
                MaskVariant? variant = null;
                if (settings.SelectedMaskId != null)
                {
                    var selected = ManualMaskStore.Load(settings.SkyImage, settings.SelectedMaskId);
                    if (!selected.Png.SequenceEqual(maskResult.Png)) throw new InvalidDataException("Restored mask does not match the selected edit.");
                    variant = selected.Variant;
                }
                string photoPath = PortablePaths.Resolve(settings.SkyImage);
                string photoSha256 = result.Dependencies!.Image.Content;
                using var source = File.OpenRead(photoPath);
                VerifyMaskPhoto(source, photoSha256);
                using var original = Cv2.ImRead(photoPath, ImreadModes.Color);
                using var binary = Cv2.ImDecode(maskResult.Png, ImreadModes.Grayscale);
                asset = new(Preview(original), Preview(binary), maskResult,
                    variant == null ? $"{maskResult.Width} × {maskResult.Height} · {maskResult.Model} · AI mask"
                        : variant.Description, variant) { PhotoSha256 = photoSha256 };
            }
            currentInputs = InputDependencies.Capture(settings);
            accepted = result with { Mask = asset };
            mask = asset; maskKey = null;
            return accepted;
        }
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

    public Task<Evaluation> Evaluate(UserSettings s, bool refresh, Action<string> progress,
        Action<MaskAsset>? onMask, CancellationToken ct, Action<SunPathOverlayResult?>? onSunPath = null, Action<CardinalDirectionOverlayResult?>? onCardinals = null)
    {
        var snapshot = s with { };
        var elapsed = Stopwatch.StartNew();
        return Task.Run(async () =>
        {
            using var trace = PerformanceTrace.Operation("irradiance-update");
            var value = await EvaluateCore(snapshot, refresh, progress, onMask, ct, onSunPath, onCardinals);
            var timed = value with { TotalMilliseconds = elapsed.Elapsed.TotalMilliseconds };
            IrradianceReadiness.Inspect(this, timed); // Build the immutable downstream snapshot on the worker.
            return timed;
        }, ct);
    }
    private Evaluation? accepted;
    private async Task<Evaluation> EvaluateCore(UserSettings s, bool refresh, Action<string> progress,
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
            WeatherSourceSelection.RequireValid(s);
            s = PrepareInputs(s);
            var persisted = PortablePaths.Map(s, path => PortablePaths.Store(path));
            s = PortablePaths.Map(s, path => PortablePaths.Resolve(path));
            InputDependencies dependencies;
            lock (inputGate)
            {
                ObserveInputs(s, refreshSources: true, verifyArtifacts: true, refreshWeather: refresh);
                dependencies = currentInputs!;
                if (!refresh && accepted is { } prior && IsCurrent(prior) && prior.ManagedDebugRun?.HasCompleteDataset == true)
                { ct.ThrowIfCancellationRequested(); return prior; }
                debug = new(Storage, persisted, verifiedInputs: dependencies); tracked = new(debug, dependencies, linked); activeRun = tracked;
            }
            var timer = Stopwatch.StartNew();
            var (activeMask, activeProfile, activeCardinals, appliedEdge) = await PrepareImageStages(s, debug, progress, onMask, onCardinals, ct, dependencies);
            var zone = TimeZoneSelection.Resolve(s.Zone);
            string nextRaw = dependencies.Keys[ArtifactGroup.Irradiance];
            debug.RecordInput("irradiance-request", new { Fingerprint = nextRaw });
            bool reuseRaw = raw != null && rawKey == nextRaw && !refresh;
            string rawDirectory = debug.BeginStage("03-irradiance", reuseRaw ? "Reused in-memory dataset" : WeatherSourceSelection.UsesServices(s) ? "Provider or validated disk cache" : "Imported", typeof(IrradianceClient));
            if (!reuseRaw)
            {
                IrradianceDataset next;
                if (!refresh && !debug.NeedsWrite(ArtifactGroup.Irradiance))
                {
                    next = await Task.Run(() => IrradianceDatasetFiles.Import(Path.Combine(debug.DirectoryPath, "03-irradiance", "horizontal-irradiance.xlsx"),
                        TimeZoneInfo.Utc, TimeSpan.Zero, TimestampLabel.Explicit, ct), ct);
                    debug.Origin("03-irradiance", "Reused published source dataset");
                }
                else if (WeatherSourceSelection.UsesServices(s))
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
            var period = StudyPeriod.FromSource(s, raw!);
            var selected = IrradianceDatasets.Select(raw!, period.Start, period.End);
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
                            EquipmentOrientation.ToCameraPose(s),
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
                        EquipmentOrientation.ToCameraPose(s),
                        new(activeMask.Disk.CenterX, activeMask.Disk.CenterY, activeMask.Disk.Radius),
                        isObservedExtensionPixel: activeProfile.Calibration.LinearExtensionStartDegrees.HasValue && activeMask.Variant?.IsImported != true
                            ? (x, y) => MaskEdgeProbe.IsObservedPixel(x, y, activeMask.Disk, activeMask.Result.Crop, activeMask.Result.InputSize)
                            : null), ct);
                    ct.ThrowIfCancellationRequested(); scene = next; sceneKey = nextScene;
                }
            }
            else { scene = null; sceneKey = null; }

            string transpositionDirectory = debug.BeginStage("05-transposition", "Computed", typeof(TranspositionModule));
            progress("Calculating irradiance on the panel");
            var panel = new PanelOrientation(s.PanelTilt, s.PanelAzimuth);
            // Rotating a horizontal panel changes its marked edge and saved study, but not its receiver normal.
            // Share the numerical rows while keeping the requested orientation in every exported stage.
            double numericalAzimuth = s.PanelTilt == 0 && s.PanelAzimuth is >= 0 and <= 360 ? 180 : s.PanelAzimuth;
            string nextTransposed = AppData.Key(new { Solar = nextGeometry, s.PanelTilt, PanelAzimuth = numericalAzimuth, s.Isotropic });
            // Transposition is cheaper to compute than to deserialize its duplicated solar timeline.
            if (transposedCache == null || transposedKey != nextTransposed || refresh)
            {
                var next = await Task.Run(() => TranspositionModule.ComputePrepared(geometry!, panel,
                    s.Isotropic ? DiffuseModel.Isotropic : DiffuseModel.HayDavies, ct: ct), ct);
                ct.ThrowIfCancellationRequested(); transposedCache = next; transposedKey = nextTransposed;
            }
            else if (transposedCache.Panel != panel)
                transposedCache = transposedCache with { Panel = panel };
            var panelArtifacts = debug.NeedsWrite(ArtifactGroup.Transposition)
                ? new[] { (await Task.Run(() => TranspositionModule.ExportPrepared(transposedCache, Path.Combine(transpositionDirectory, "panel-unshaded.xlsx"), ct), ct)).OutputPath! } : [];
            debug.CompleteStage("05-transposition", panelArtifacts);

            string correctionDirectory = debug.BeginStage("06-shading", scene == null ? "Shading unavailable; unshaded summary only" : "Computed", typeof(ShadingCorrectionModule));
            progress("Applying panel shading and saving library results");
            string nextResult = AppData.Key(new { Panel = nextTransposed, Camera = dependencies.Keys[ArtifactGroup.Orientation], Mask = dependencies.Keys[ArtifactGroup.Mask] });
            if (!refresh && (resultCache == null || resultKey != nextResult))
            { resultCache = await Task.Run(() => Values.Read<PanelRun>("shading", nextResult), ct); resultKey = nextResult; }
            if (resultCache == null || resultKey != nextResult || refresh)
            {
                var next = await Task.Run(() => correctPanel(transposedCache, scene, ct), ct);
                ct.ThrowIfCancellationRequested(); resultCache = next; resultKey = nextResult;
                await Task.Run(() => Values.Write("shading", nextResult, next), ct);
            }
            var result = resultCache.Panel.HasValue && resultCache.Panel != panel
                ? resultCache with { Panel = panel } : resultCache;
            debug.CompleteStage("06-shading", debug.NeedsWrite(ArtifactGroup.Shading)
                ? await Task.Run(() => ShadingCorrectionModule.ExportPanel(result, correctionDirectory, ct), ct) : [], skipped: scene == null);
            lock (inputGate)
            {
                tracked.Dirty |= dependencies.Difference(InputDependencies.Capture(s));
                if (tracked.Dirty != ArtifactGroup.None) throw new OperationCanceledException("Inputs changed during the update.", ct);
                ct.ThrowIfCancellationRequested(); debug.Complete(period);
            }
            string note = scene == null ? "Add a calibrated camera profile and sky photo for shading." : "Unknown sky is treated as blocked. No ground reflection. Camera coverage may be provisional.";
            accepted = new(result, raw!, persisted, activeMask, note, timer.Elapsed.TotalMilliseconds, PreparationCount)
                { DebugDirectory = debug.DirectoryPath, SunPath = activeSunPath, SunPathGenerationCount = SunPathGenerationCount, Cardinals = activeCardinals, CardinalGenerationCount = CardinalGenerationCount,
                    MaskEdge = appliedEdge, Dependencies = dependencies, ManagedDebugRun = debug };
            try { if (debug.HasCompleteDataset) AcceptedResultVault.Archive(accepted); }
            catch (Exception archiveError) { try { File.AppendAllText(AppData.PathFor("errors.log"), $"{DateTimeOffset.Now:O} Could not archive accepted result: {archiveError}{Environment.NewLine}"); } catch { } }
            return accepted;
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
    public Task<OrientationPreview> PrepareOrientation(UserSettings settings, Action<string> progress,
        Action<MaskAsset>? onMask, CancellationToken ct, Action<CardinalDirectionOverlayResult?>? onCardinals = null)
    {
        var snapshot = settings with { };
        return Task.Run(() => PrepareOrientationCore(snapshot, progress, onMask, ct, onCardinals), ct);
    }
    private async Task<OrientationPreview> PrepareOrientationCore(UserSettings settings, Action<string> progress,
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
            InputDependencies dependencies;
            lock (inputGate)
            {
                ObserveInputs(settings, out dependencies, refreshSources: true, verifyArtifacts: true);
                debug = new(Storage, persisted, "orientation", verifiedInputs: dependencies); activeRun = new(debug, dependencies, linked);
            }
            var (activeMask, _, overlay, edge) = await PrepareImageStages(resolved, debug, progress, onMask, onCardinals, ct, dependencies);
            ct.ThrowIfCancellationRequested(); debug.Complete();
            return new(activeMask, overlay, debug.DirectoryPath) { MaskEdge = edge };
        }
        catch (Exception ex) { debug?.Status(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message); throw; }
        finally
        {
            try { lock (inputGate) { if (activeRun != null) { debug!.Invalidate(activeRun.Dirty, activeRun.Inputs.SourcePaths); activeRun = null; } debug?.Finish(); } }
            finally { ReleaseOperation(); }
        }
    }

    private async Task<(MaskAsset? Mask, CalibrationProfile? Profile, CardinalDirectionOverlayResult? Cardinals, AppliedMaskEdge? Edge)> PrepareImageStages(
        UserSettings s, DebugDataRun debug, Action<string> progress, Action<MaskAsset>? onMask,
        Action<CardinalDirectionOverlayResult?>? onCardinals, CancellationToken ct, InputDependencies inputs)
    {
            MaskAsset? activeMask = null;
            if (!string.IsNullOrEmpty(s.SkyImage))
            {
                string photoSha256 = inputs.Image.Content;
                string nextMask = s.SelectedMaskId == null ? AiMaskKey(s, photoSha256) : inputs.Keys[ArtifactGroup.Mask];
                debug.RecordInput("sky-mask", new { Fingerprint = nextMask, PhotoSha256 = photoSha256 });
                string directory = debug.BeginStage("02-sky-mask", "Preparing selected sky mask", typeof(SkyPhotoMasker));
                if (mask == null || maskKey != nextMask) progress("Preparing sky mask");
                string maskOrigin;
                (activeMask, maskOrigin) = await LoadStudyMask(s, photoSha256, nextMask, ct);
                debug.Origin("02-sky-mask", maskOrigin);
                debug.CompleteStage("02-sky-mask", debug.NeedsWrite(ArtifactGroup.Mask) ? await Task.Run(() =>
                {
                    var files = SkyMaskExporter.Export(activeMask.Result, directory, maskOrigin, ct).ToList();
                    string pixelConvention = "255 = open sky; 0 = obstruction or outside lens; no automatic inversion";
                    if (activeMask.Variant?.Source == "Edited")
                    {
                        pixelConvention = "255 = open sky; 0 = obstruction. Edits cover the full oriented image. Projection is limited by the active angular range, lens disk and image. In an AI-derived extension, only the nearest classified pixel is observed. Inside calibration the mask is sampled bilinearly.";
                        string detailsPath = Path.Combine(directory, "mask-details.json");
                        var details = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(detailsPath))!.AsObject();
                        details["PixelConvention"] = pixelConvention;
                        AppData.Write(detailsPath, details);
                    }
                    string provenance = Path.Combine(directory, "mask-provenance.json");
                    AppData.Write(provenance, new { Source = activeMask.Variant?.Source ?? "AI", activeMask.PhotoSha256,
                        VariantId = activeMask.Variant?.Id, Label = activeMask.Variant?.Label,
                        AiAncestorSha256 = activeMask.Variant?.IsImported == true ? null : activeMask.AiAncestorSha256,
                        ImportedFileName = activeMask.Variant?.ImportedFileName,
                        ImportedFileSha256 = activeMask.Variant?.ImportedFileSha256,
                        ImportedAncestorSha256 = activeMask.Variant?.ImportedAncestorSha256,
                        ImportInverted = activeMask.Variant?.ImportInverted,
                        PixelConvention = pixelConvention,
                        ParentVariantId = activeMask.Variant?.ParentId,
                        ActiveMaskSha256 = Convert.ToHexString(SHA256.HashData(activeMask.Png)) });
                    files.Add(provenance); return files;
                }, ct) : []);
                onMask?.Invoke(activeMask);
            }
            else debug.Skip("02-sky-mask", "No sky photograph selected");

            CalibrationProfile? activeProfile = null;
            if (!string.IsNullOrEmpty(s.ProfilePath) && activeMask != null)
            {
                string nextProfile = AppData.Key(new { File = AppData.FileKey(s.ProfilePath), s.CoverageAngle,
                    activeMask.Result.Width, activeMask.Result.Height, Library = AppData.LibraryVersion(typeof(CameraProfileFiles)) });
                if (s.ApparentFullFovDegrees is double apparent)
                    nextProfile = AppData.Key(new { Source = nextProfile, ApparentFullFovDegrees = apparent });
                else
                    nextProfile = AppData.Key(new { Source = nextProfile, SelectedMask = maskKey, Threshold = MaskEdgeProbe.ProposalBlackPercent,
                        Probe = AppData.LibraryVersion(typeof(MaskEdgeProbe)) });
                debug.RecordInput("camera-profile", new { Fingerprint = nextProfile });
                string directory = debug.BeginStage("01-calibration", "Loaded effective camera profile", typeof(CameraProfileFiles));
                if (profileKey != nextProfile || profile == null)
                {
                    var next = await Task.Run(() => ResolveStudyProfile(s, activeMask, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    (profile, profileEdge) = next;
                    profileKey = nextProfile;
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
                    progress("Drawing the calibrated sky grid");
                    var next = await Task.Run(() => CardinalDirectionOverlayGenerator.Generate(
                        new CalibratedSkyProjection(activeProfile.Calibration,
                            EquipmentOrientation.ToCameraPose(s),
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
            var edge = activeProfile == null ? null : profileEdge;
            debug.SetMaskEdge(edge);
            return (activeMask, activeProfile, activeCardinals, edge);
    }

    private static (CalibrationProfile Profile, AppliedMaskEdge? Edge) ResolveStudyProfile(UserSettings s, MaskAsset mask, CancellationToken ct)
    {
        var source = CameraProfileFiles.Load(s.ProfilePath, mask.Result.Width, mask.Result.Height,
            s.CoverageAngle > 0 ? s.CoverageAngle : null);
        var measured = CalibrationRadialMap.MeasuredProfile(source.Calibration);
        if (!ReferenceEquals(measured, source.Calibration))
            source = source with { Calibration = measured, CoverageNote = "Measured portion of the saved effective profile." };
        if (s.ApparentFullFovDegrees is double apparent)
        {
            var selected = CalibrationRadialMap.WithApparentFullFov(measured, apparent);
            string explanation = FormattableString.Invariant($"User-selected apparent full FOV {apparent:0.###}°. Measured full FOV {2 * measured.MaximumIncidentAngleDegrees:0.###}°.");
            return (source with { Calibration = selected, CoverageNote = source.CoverageNote + " " + explanation },
                new AppliedMaskEdge(2 * measured.MaximumIncidentAngleDegrees, apparent, null, false, explanation) { ManualFullFovDegrees = apparent });
        }
        var edge = mask.SuggestEdge(measured, ct);
        var calibration = edge.EffectiveFullDegrees < edge.MeasuredFullDegrees
            ? CalibrationRadialMap.WithApparentFullFov(measured, edge.EffectiveFullDegrees)
            : CalibrationRadialMap.ExtendTo(measured, edge.EffectiveFullDegrees / 2);
        string note = FormattableString.Invariant($" Proposed edge: {edge.EffectiveFullDegrees:0.##}° full FOV. ") + edge.Explanation;
        return (source with { Calibration = calibration, CoverageNote = source.CoverageNote + note }, edge);
    }

    public static Task<IrradianceDataset> Fetch(UserSettings s, bool refresh, CancellationToken ct) => Task.Run(async () => (await FetchWithOrigin(s with { }, refresh, ct)).Dataset, ct);

    private static async Task<(IrradianceDataset Dataset, string Origin)> FetchWithOrigin(UserSettings s, bool refresh, CancellationToken ct)
    {
        if (!WeatherSourceSelection.UsesServices(s)) throw new InvalidOperationException("Data services are disabled in Import Own Excel mode.");
        WeatherSourceSelection.RequireValid(s);
        string key = AppData.Key(new { s.Start, s.End, s.Longitude, s.Latitude, s.Provider, s.Zone, Library = AppData.LibraryVersion(typeof(IrradianceClient)) });
        string path = AppData.PathFor(Path.Combine("weather-stages", key + ".xlsx"));
        if (!refresh && File.Exists(path)) return (IrradianceDatasetFiles.Import(path, TimeZoneInfo.Utc, TimeSpan.Zero, TimestampLabel.Explicit, ct), "Reused validated disk weather cache");
        var service = s.Provider == 0 ? IrradianceService.NasaPower : IrradianceService.OpenMeteo;
        var data = await new IrradianceClient(new() { TimeZone = TimeZoneSelection.Resolve(s.Zone) }).FetchDatasetAsync(
            DateOnly.FromDateTime(s.Start), DateOnly.FromDateTime(s.End), s.Longitude, s.Latitude, service, ct);
        ct.ThrowIfCancellationRequested(); IrradianceDatasetFiles.Export(data, path, ct); return (data, "Fetched from irradiance service");
    }

    /// <summary>Prepare just the selected photo's mask for edge refinement without publishing an update.</summary>
    public async Task<MaskAsset> PrepareEdgeMask(UserSettings settings, CancellationToken ct)
    {
        var study = settings with { SkyImage = PortablePaths.Resolve(settings.SkyImage) };
        await evaluationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (inputGate) ObjectDisposedException.ThrowIf(disposeRequested, this);
            string photoSha256 = await Task.Run(() => AppData.FileKey(study.SkyImage), ct).ConfigureAwait(false);
            string? key = study.SelectedMaskId == null ? AiMaskKey(study, photoSha256) : null;
            return (await LoadStudyMask(study, photoSha256, key, ct).ConfigureAwait(false)).Asset;
        }
        finally { ReleaseOperation(); }
    }

    internal static string AiMaskKey(UserSettings s, string photoSha256, string? library = null, string package = MaskCacheSchema) =>
        AppData.Key(new { Image = photoSha256, s.Model, s.Resolution, s.CenteredDisk, Library = library ?? AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = package });

    private sealed record MaskCacheSource(string Owner, string Key, string PhotoSha256);
    private const string MaskCacheOwner = "SolarShade.MaskStage.v1";

    public SourceIdentity? LastRecordedSkyPhoto(string path)
    {
        lock (inputGate) return Storage.Current?.LastRecordedSkyPhoto(path);
    }

    public Task DiscardPhotoAsync(SourceIdentity previousPhoto, UserSettings replacement, string expectedPhotoSha256) => Task.Run(async () =>
    {
        if (!PhotoStorageCleanup.IsHash(previousPhoto.Content) || !PhotoStorageCleanup.IsHash(expectedPhotoSha256))
            throw new ArgumentException("Both sky photos must have verified identities.");
        previousPhoto = previousPhoto with { Path = PortablePaths.Resolve(previousPhoto.Path) };
        await evaluationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (inputGate)
            {
                var prepared = PrepareInputs(replacement);
                using var replacementFile = File.OpenRead(PortablePaths.Resolve(prepared.SkyImage));
                var inputs = InputDependencies.Capture(prepared);
                if (inputs.Image.Content != expectedPhotoSha256) throw new IOException("The new sky photo changed. Select it again.");
                if (expectedPhotoSha256 == previousPhoto.Content) return;
                var cleanup = new PhotoStorageCleanup(previousPhoto, inputs.SourcePaths);
                var libraries = new HashSet<string> { AppData.LibraryVersion(typeof(SkyPhotoMasker)) };
                var run = AppData.Read<DebugDataRun.Manifest>(Path.Combine(Storage.Root, "run.json"));
                if (run?.Stages.TryGetValue("02-sky-mask", out var stage) == true && Guid.TryParse(stage.LibraryVersion, out _))
                    libraries.Add(stage.LibraryVersion);
                var discardArchives = AcceptedResultVault.PreparePhotoRemoval(cleanup, libraries);
                DiscardAiStages(cleanup, libraries);
                ManualMaskStore.DiscardPhoto(cleanup);
                discardArchives();
                if (Storage.Current is { } current) current.DiscardPhotoArtifacts(previousPhoto, prepared, inputs);
                else Values.DiscardPhotoResults();
                currentInputs = inputs; accepted = null; mask = null; maskKey = null;
                profile = null; profileEdge = null; scene = null; cardinals = null; sunPath = null; resultCache = null;
            }
        }
        finally { ReleaseOperation(); }
    });

    private static void DiscardAiStages(PhotoStorageCleanup cleanup, HashSet<string> libraries)
    {
        var legacyKeys = new HashSet<string>();
        foreach (string library in libraries)
        foreach (var model in Enum.GetValues<SkyModel>())
        foreach (int resolution in new[] { 512, 1024 })
        foreach (bool centered in new[] { false, true })
        foreach (string package in new[] { "mask-orchestration-v1", MaskCacheSchema })
            legacyKeys.Add(AiMaskKey(new() { Model = model, Resolution = resolution, CenteredDisk = centered }, cleanup.PhotoSha256, library, package));
        foreach (string folder in PhotoStorageCleanup.Directories(AppData.PathFor("mask-stages")))
        {
            string key = Path.GetFileName(folder);
            if (!PhotoStorageCleanup.IsHash(key)) continue;
            var source = PhotoStorageCleanup.Read<MaskCacheSource>(Path.Combine(folder, "photo-source.json"));
            bool match = source == null ? legacyKeys.Contains(key) :
                source.Owner == MaskCacheOwner && source.Key == key && source.PhotoSha256 == cleanup.PhotoSha256;
            if (!match) continue;
            cleanup.RemoveFile(folder, "sky-mask.png"); cleanup.RemoveFile(folder, "mask-details.json"); cleanup.RemoveFile(folder, "photo-source.json");
            PhotoStorageCleanup.RemoveIfEmpty(folder);
        }
    }

    private static void VerifyMaskPhoto(Stream photo, string expectedSha256)
    {
        if (Convert.ToHexString(SHA256.HashData(photo)) != expectedSha256)
            throw new IOException("The sky photo changed while preparing its mask. Select the current photo again.");
    }

    private async Task<(MaskAsset Asset, string Origin)> LoadStudyMask(UserSettings s, string photoSha256, string? key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (key != null && mask != null && maskKey == key) return (mask, "Reused in-memory mask");
        await NativeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // The classifier and preview decoder both reopen this path. Keep the verified bytes stable for both.
                using var source = File.OpenRead(s.SkyImage);
                VerifyMaskPhoto(source, photoSha256);
                SkyMaskResult result;
                MaskVariant? variant = null;
                string origin;
                if (s.SelectedMaskId != null)
                {
                    (variant, byte[] edited) = ManualMaskStore.Load(s.SkyImage, s.SelectedMaskId);
                    if (!ManualMaskStore.IsCompatible(variant, s))
                        throw new InvalidDataException("This edited mask was made with different AI model, resolution or disk settings. Select its original settings or choose another mask.");
                    result = variant.BaseMask with { Png = edited };
                    origin = variant.Description;
                }
                else
                {
                    string cache = AppData.PathFor(Path.Combine("mask-stages", key!));
                    if (File.Exists(Path.Combine(cache, "sky-mask.png")) && File.Exists(Path.Combine(cache, "mask-details.json")))
                    { result = SkyMaskExporter.Load(cache); origin = "Reused validated disk mask cache"; }
                    else
                    {
                        string modelDirectory = AppData.EnsureModel(s.Model);
                        string modelKey = AppData.Key(new { modelDirectory, s.Resolution, s.CenteredDisk });
                        if (modelKey != maskerKey)
                        {
                            var nextMasker = new SkyPhotoMasker(new() { ModelsDirectory = modelDirectory, InputSize = s.Resolution,
                                DiskDetection = s.CenteredDisk ? DiskDetection.Centered : DiskDetection.Auto,
                                Acceleration = Environment.GetEnvironmentVariable("SOLARSHADE_FORCE_CPU") == "1" ? MaskAcceleration.Cpu : MaskAcceleration.Auto });
                            masker?.Dispose();
                            masker = nextMasker;
                            maskerKey = modelKey;
                        }
                        result = masker!.CreateMaskDetailed(s.SkyImage, s.Model);
                        origin = "Computed by SkyPhotoMasker";
                        // The metadata is the completion marker. A failed write can leave a PNG
                        // for the next attempt, but never a partially published pair.
                        string metadata = Path.Combine(cache, "mask-details.json");
                        if (File.Exists(metadata) && !File.Exists(Path.Combine(cache, "sky-mask.png"))) File.Delete(metadata);
                        AppData.Write(Path.Combine(cache, "photo-source.json"), new MaskCacheSource(MaskCacheOwner, key!, photoSha256));
                        SkyMaskExporter.Export(result, cache, "Computed");
                    }
                }
                ct.ThrowIfCancellationRequested();
                using var image = Cv2.ImRead(s.SkyImage, ImreadModes.Color);
                using var binary = Cv2.ImDecode(result.Png, ImreadModes.Grayscale);
                if (image.Empty() || image.Width != result.Width || image.Height != result.Height)
                    throw new InvalidDataException("The mask dimensions must match the selected oriented photo.");
                var asset = new MaskAsset(Preview(image), Preview(binary), result,
                    variant == null ? $"{result.Width} × {result.Height} · {result.Model} · {result.ExecutionProvider} · AI mask"
                        : $"{result.Width} × {result.Height} · {variant.Description}", variant) { PhotoSha256 = photoSha256 };
                return (Asset: asset, Origin: origin);
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (key != null) { mask = loaded.Asset; maskKey = key; }
            return loaded;
        }
        finally { NativeGate.Release(); }
    }

    public static MaskAsset? LoadCachedAiMask(UserSettings settings)
    {
        if (string.IsNullOrEmpty(settings.SkyImage)) return null;
        string photo = PortablePaths.Resolve(settings.SkyImage);
        if (!File.Exists(photo)) return null;
        using var source = File.OpenRead(photo);
        string photoSha256 = Convert.ToHexString(SHA256.HashData(source));
        string key = AiMaskKey(settings, photoSha256);
        string cache = AppData.PathFor(Path.Combine("mask-stages", key));
        if (!File.Exists(Path.Combine(cache, "sky-mask.png")) || !File.Exists(Path.Combine(cache, "mask-details.json"))) return null;
        var result = SkyMaskExporter.Load(cache);
        using var photoPixels = Cv2.ImRead(photo, ImreadModes.Color);
        using var maskPixels = Cv2.ImDecode(result.Png, ImreadModes.Grayscale);
        if (photoPixels.Empty() || photoPixels.Width != result.Width || photoPixels.Height != result.Height)
            throw new InvalidDataException("The cached AI mask no longer matches the selected photo.");
        return new(Preview(photoPixels), Preview(maskPixels), result,
            $"{result.Width} × {result.Height} · {result.Model} · {result.ExecutionProvider} · AI mask") { PhotoSha256 = photoSha256 };
    }

    public static byte[] Preview(Mat source)
    {
        using var small = new Mat(); double scale = Math.Min(1, 1000.0 / Math.Max(source.Width, source.Height));
        Cv2.Resize(source, small, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
        Cv2.ImEncode(".png", small, out var bytes); return bytes;
    }

    public Task<(CalibrationProfile Profile, string Path, string Details)> Calibrate(UserSettings s)
    {
        var snapshot = s with { };
        return Task.Run(() => CalibrateCore(snapshot));
    }
    private async Task<(CalibrationProfile Profile, string Path, string Details)> CalibrateCore(UserSettings s)
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
                catch (Exception ex)
                {
                    debug.Status(ex is OperationCanceledException ? "Cancelled" : "Failed", ex.Message);
                    if (ex is CalibrationReviewException review)
                        throw new CalibrationReviewException(review.Message,
                            DebugDataStore.Preserve(review.DiagnosticsDirectory, "CalibrationReviews"), review);
                    throw;
                }
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
    public static string Export(Evaluation evaluation, string directory, DataExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options.Validate();
        if (evaluation.DebugDirectory == null) throw new InvalidOperationException("The result has no completed library artifacts.");
        if (evaluation.Dependencies == null || evaluation.ManagedDebugRun == null) throw new InvalidOperationException("Result provenance is unavailable; update results before exporting.");
        string destination = Path.Combine(directory, $"scenario-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        IrradianceDataExport.Publish(evaluation.ManagedDebugRun, destination, options: options);
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
