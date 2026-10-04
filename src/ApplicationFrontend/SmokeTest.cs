using SolarShade.Irradiance;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using SolarShade.MaskEditor;
using Cv2 = OpenCvSharp.Cv2;
using ImreadModes = OpenCvSharp.ImreadModes;

namespace SolarShade.Desktop;

public static class SmokeTest
{
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);
    public static int Run(Application app, string output, bool portable = false)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        if (!portable) AppData.Root = Path.Combine(output, "app-data");
        var saved = portable ? AppData.ReadSettings() : null;
        bool startsExample = portable && (saved?.FirstRun ?? true);
        int exitCode = 1;
        bool timeZoneTest = Environment.GetEnvironmentVariable("SOLARSHADE_TEST_TIMEZONE") == "1";
        var simulatedSystemZone = TimeZoneSelection.CurrentSystemZone();
        if (timeZoneTest)
        {
            var example = PortablePaths.Example();
            // Retain legacy settings without a timezone-mode flag, but supply the image inputs
            // needed when the smoke test explicitly updates the loaded example.
            AppData.Write(AppData.PathFor("settings.json"), new { Zone = "SE Asia Standard Time", Latitude = 43.5, Longitude = 1.5,
                example.SkyImage, example.SkyFolder, example.ProfilePath, example.CoverageAngle, example.Model, example.Resolution });
        }
        var window = new MainWindow(timeZoneTest ? () => simulatedSystemZone : null, loadExampleOnFirstRun: portable);
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                double readyMs = Program.Startup.Elapsed.TotalMilliseconds;
                Capture(window, Path.Combine(output, "startup.png"));
                var timer = Stopwatch.StartNew();
                await Task.Delay(500);
                if (window.Completed != null || window.CardinalsForTest != null || window.UpdatingForTest)
                    throw new InvalidOperationException("Startup performed scientific work without Update results.");
                if (!portable) await window.LoadExample();
                await Task.Delay(500);
                if (window.Completed != null || window.ExportEnabledForTest)
                    throw new InvalidOperationException("Loading inputs calculated or enabled export.");
                window.Calculate();
                await Until(() => window.Completed != null, 120);
                var first = window.Completed!; double firstMs = timer.Elapsed.TotalMilliseconds;
                if (portable)
                {
                    if (!Path.GetFullPath(AppData.Root).Equals(Path.Combine(AppContext.BaseDirectory, "Data"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Portable state escaped the package.");
                    if (new[] { first.Settings.CalibrationFolder, first.Settings.SkyFolder, first.Settings.SkyImage, first.Settings.ProfilePath, first.Settings.ImportPath }.Any(Path.IsPathRooted)) throw new InvalidOperationException("Example paths were not saved relative to the executable.");
                    if (saved is { FirstRun: false } && (first.Settings.PanelTilt != saved.PanelTilt || first.Settings.PanelAzimuth != saved.PanelAzimuth)) throw new InvalidOperationException("Saved panel settings were lost after relocation.");
                    if (first.Raw.Source != "Imported: " + Path.GetFileName(first.Settings.ImportPath)) throw new InvalidOperationException("Example used an unexpected irradiance source.");
                }
                if (timeZoneTest && (first.Settings.Zone != simulatedSystemZone.Id || !first.Settings.UseSystemTimeZone))
                    throw new InvalidOperationException("Old saved settings or Load example overrode the automatic time zone.");
                if (first.SunPath is not { HasPaths: true } || !window.SunPathPublishedEarlyForTest)
                    throw new InvalidOperationException("The sun path was not published before the full calculation completed.");
                if (!File.Exists(Path.Combine(first.DebugDirectory!, "04-solar-positions", "sun-path-overlay.png")))
                    throw new InvalidOperationException("The native sun-path overlay artifact is missing.");
                var overlayFile = File.ReadAllBytes(Path.Combine(first.DebugDirectory!, "04-solar-positions", "sun-path-overlay.png"));
                if (!overlayFile.SequenceEqual(first.SunPath.Png)) throw new InvalidOperationException("The saved overlay differs from the displayed library result.");
                if (!File.ReadAllBytes(Path.Combine(first.DebugDirectory!, "02-sky-mask", "sky-mask.png")).SequenceEqual(first.Mask!.Png))
                    throw new InvalidOperationException("The sun-path preview changed the binary mask artifact.");
                if (first.Cardinals is not { HasMarkers: true } || !window.CardinalsVisibleForTest)
                    throw new InvalidOperationException("Cardinal overlay missing from original sky photograph.");
                if (!File.ReadAllBytes(Path.Combine(first.DebugDirectory!, "02-orientation", "cardinal-directions-overlay.png")).SequenceEqual(first.Cardinals.Png))
                    throw new InvalidOperationException("Saved cardinal overlay differs from the displayed library image.");
                foreach (string artifact in new[] { "cardinal-directions.xlsx", "cardinal-directions-details.json" })
                    if (!File.Exists(Path.Combine(first.DebugDirectory!, "02-orientation", artifact))) throw new InvalidOperationException("Missing cardinal artifact: " + artifact);
                var originalPhoto = Find<ImageOverlayPreview>(window, preview => preview is not MaskOverlayPreview);
                Capture(originalPhoto, Path.Combine(output, "original-photo-with-cardinals.png"));
                long orientationToggleRevision = window.OrientationRevisionForTest;
                int cardinalToggleCount = window.CardinalGenerationCountForTest;
                window.SetCardinalsVisibleForTest(false);
                if (window.CardinalsVisibleForTest || AppData.ReadSettings()?.ShowCardinalDirections != false)
                    throw new InvalidOperationException("Cardinal visibility was not hidden and persisted.");
                Capture(originalPhoto, Path.Combine(output, "original-photo-without-cardinals.png"));
                window.SetCardinalsVisibleForTest(true);
                if (!window.CardinalsVisibleForTest || !ReferenceEquals(first.Cardinals, window.CardinalsForTest))
                    throw new InvalidOperationException("Cardinal display toggle replaced or lost the cached overlay.");
                long toggleRevision = window.RevisionForTest;
                window.SetSunPathVisibleForTest(false);
                if (window.SunPathVisibleForTest || AppData.ReadSettings()?.ShowSunPath != false)
                    throw new InvalidOperationException("Sun-path visibility was not hidden and persisted.");
                window.SetSunPathVisibleForTest(true);
                if (!window.SunPathVisibleForTest) throw new InvalidOperationException("The sun-path preview is not visible.");
                await Task.Delay(450);
                if (window.Completed != first || window.RevisionForTest != toggleRevision || window.OrientationRevisionForTest != orientationToggleRevision || window.CardinalGenerationCountForTest != cardinalToggleCount)
                    throw new InvalidOperationException("The display-only toggle triggered scientific calculation.");
                Capture(window, Path.Combine(output, "example.png"));
                var chart = Find<System.Windows.FrameworkElement>(window, e => e is IrradianceChart) as IrradianceChart ?? throw new InvalidOperationException("Chart missing.");
                chart.Day(TimeZoneInfo.ConvertTime(first.Run.Rows[0].Start, TimeZoneSelection.Resolve(first.Settings.Zone)).Date);
                Capture(window, Path.Combine(output, "example-day.png"));
                chart.Reset();
                await VerifyPv(window, output);
                string firstExport = AppServices.Export(first, output);
                using (var summary = PdfSharp.Pdf.IO.PdfReader.Open(Path.Combine(firstExport, "Summary.pdf"), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    if (summary.PageCount != 1) throw new InvalidOperationException("Packaged PDF was not one page.");
                var exportedState = AppData.Read<DebugDataRun.Manifest>(Path.Combine(firstExport, "run.json"))!;
                foreach (var (file, hash) in exportedState.ArtifactHashes)
                    if (AppData.FileKey(Path.Combine(firstExport, file)) != hash) throw new InvalidOperationException("Export altered a debug artifact.");
                AppData.Write(Path.Combine(output, "export-result.json"), new { Passed = true, Folder = firstExport, OnePagePdf = true, ByteIdenticalArtifacts = exportedState.ArtifactHashes.Count });
                int beforeCount = first.PreparationCount;
                timer.Restart(); window.SetPanelForTest(first.Settings.PanelTilt == 55 ? 30 : 55, first.Settings.PanelAzimuth == 180 ? 0 : 180);
                if (!window.SunPathVisibleForTest) throw new InvalidOperationException("A valid sun path disappeared during a panel-only edit.");
                if (!window.CardinalsVisibleForTest || !ReferenceEquals(first.Cardinals, window.CardinalsForTest))
                    throw new InvalidOperationException("A panel-only edit cleared current cardinals.");
                window.Calculate();
                window.StopUpdate();
                await Until(() => !window.UpdatingForTest, 30);
                if (window.ExportEnabledForTest || window.Completed != first) throw new InvalidOperationException("Stop accepted results or left export enabled.");
                window.Calculate();
                await Until(() => window.Completed != first, 30);
                var second = window.Completed!;
                double editMs = timer.Elapsed.TotalMilliseconds;
                if (!ReferenceEquals(first.Cardinals, second.Cardinals) || second.CardinalGenerationCount != first.CardinalGenerationCount)
                    throw new InvalidOperationException("A panel-only edit recalculated cardinal directions.");
                if (second.PreparationCount != beforeCount) throw new InvalidOperationException("Panel edit unnecessarily regenerated visibility.");
                if (second.SunPathGenerationCount != first.SunPathGenerationCount || second.SunPath == null || !second.SunPath.Png.SequenceEqual(first.SunPath.Png))
                    throw new InvalidOperationException("Panel edit unnecessarily regenerated the sun path.");
                if (Math.Abs(second.Run.BeforeEnergy - first.Run.BeforeEnergy) < .001) throw new InvalidOperationException("Panel orientation did not update the graph.");
                if (second.Run.Rows.Any(r => r.AfterTotal is null || r.AfterTotal < 0 || r.AfterTotal > r.BeforeTotal + 1e-8)) throw new InvalidOperationException("Invalid shaded result.");
                Capture(window, Path.Combine(output, "panel-edited.png"));
                window.SetDiffuseModelForTest(!second.Settings.Isotropic);
                if (!window.SunPathVisibleForTest) throw new InvalidOperationException("A valid sun path disappeared during a diffuse-model edit.");
                window.Calculate();
                await Until(() => window.Completed != second, 30);
                var changedDiffuse = window.Completed!;
                if (changedDiffuse.SunPathGenerationCount != second.SunPathGenerationCount || changedDiffuse.SunPath == null || !changedDiffuse.SunPath.Png.SequenceEqual(second.SunPath.Png))
                    throw new InvalidOperationException("A diffuse-model edit regenerated the sun path.");
                window.SetCameraPoseForTest(changedDiffuse.Settings.BottomAzimuth == 180 ? 210 : 180, changedDiffuse.Settings.CameraTilt, changedDiffuse.Settings.CameraRoll);
                if (window.SunPathVisibleForTest || window.CardinalsVisibleForTest)
                    throw new InvalidOperationException("A stale overlay remained visible after a camera change.");
                window.Calculate();
                await Until(() => window.Completed != changedDiffuse, 30);
                var reprojected = window.Completed!;
                if (reprojected.PreparationCount != changedDiffuse.PreparationCount)
                    throw new InvalidOperationException("Camera reprojection unnecessarily regenerated solar positions.");
                if (reprojected.SunPathGenerationCount != second.SunPathGenerationCount + 1 || reprojected.SunPath == null || reprojected.SunPath.Png.SequenceEqual(second.SunPath.Png))
                    throw new InvalidOperationException("Camera pose did not reproject the cached solar path.");
                if (!window.SunPathVisibleForTest || !window.SunPathPublishedEarlyForTest)
                    throw new InvalidOperationException("The reprojected sun path was not shown early.");
                if (!window.CardinalsVisibleForTest || reprojected.CardinalGenerationCount != changedDiffuse.CardinalGenerationCount + 1 ||
                    reprojected.Cardinals == null || reprojected.Cardinals.Png.SequenceEqual(changedDiffuse.Cardinals!.Png))
                    throw new InvalidOperationException("Camera pose did not refresh cardinal directions exactly once.");
                Capture(window, Path.Combine(output, "sun-path-camera-edited.png"));
                long validCameraRevision = window.RevisionForTest;
                window.CommitFieldForTest("CameraTilt", "not-a-number");
                if (window.RevisionForTest <= validCameraRevision || window.SunPathVisibleForTest || window.CardinalsVisibleForTest)
                    throw new InvalidOperationException("An invalid camera commit did not reject stale callbacks and hide the sun path.");
                await Task.Delay(450);
                if (window.Completed != reprojected || window.SunPathVisibleForTest || window.CardinalsVisibleForTest)
                    throw new InvalidOperationException("Invalid camera input allowed an old result or overlay to publish.");
                Capture(window, Path.Combine(output, "sun-path-invalid-camera.png"));
                window.CommitFieldForTest("CameraTilt", reprojected.Settings.CameraTilt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                window.Calculate();
                await Until(() => window.Completed != reprojected, 30);
                var correctedCamera = window.Completed!;
                if (!window.SunPathVisibleForTest || correctedCamera.SunPathGenerationCount != reprojected.SunPathGenerationCount || correctedCamera.PreparationCount != reprojected.PreparationCount)
                    throw new InvalidOperationException("Corrected camera input did not restore the valid cached sun path.");
                if (!window.CardinalsVisibleForTest || correctedCamera.CardinalGenerationCount != reprojected.CardinalGenerationCount ||
                    correctedCamera.Cardinals == null || !correctedCamera.Cardinals.Png.SequenceEqual(reprojected.Cardinals!.Png))
                    throw new InvalidOperationException("Corrected camera input failed to restore cached cardinal directions.");
                AppData.Write(Path.Combine(output, "cardinal-ui-result.json"), new { Passed = true, NoAutomaticPreview = true,
                    OriginalColoredPhotoOverlay = true, ToggleIsDisplayOnly = true, PanelEditReusesOverlay = true,
                    PoseEditRegeneratesExactlyOnce = true, InvalidPoseClearsImmediately = true, RecoveryReusesCache = true,
                    ManualUpdateRequired = true,
                    NativePngMatchesDisplayedResult = true, FirstGeneration = first.CardinalGenerationCount,
                    PoseGeneration = reprojected.CardinalGenerationCount, FirstDebugDirectory = first.DebugDirectory });
                AppData.Write(Path.Combine(output, "sun-path-ui-result.json"), new { Passed = true, EarlyPublication = true,
                    ToggleIsDisplayOnly = true, PanelEditReusedOverlay = true, DiffuseModelEditReusedOverlay = true, CameraEditReusedSolarTimeline = true,
                    InvalidCameraCommitInvalidatedRevision = true, CorrectedCameraInputRestoredOverlay = true,
                    FirstGeneration = first.SunPathGenerationCount, PanelEditGeneration = second.SunPathGenerationCount,
                    CameraEditGeneration = reprojected.SunPathGenerationCount, ReprojectedDebugDirectory = reprojected.DebugDirectory });
                if (timeZoneTest)
                {
                    var changes = new List<object>();
                    foreach (string zoneId in new[] { "Romance Standard Time", "SE Asia Standard Time" })
                    {
                        if (simulatedSystemZone.Id == zoneId) continue;
                        var previous = window.Completed!;
                        simulatedSystemZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                        window.RefreshSystemTimeZone();
                        window.Calculate();
                        await Until(() => window.Completed != previous, 60);
                        var current = window.Completed!;
                        if (previous.Cardinals == null || current.Cardinals == null || !previous.Cardinals.Png.SequenceEqual(current.Cardinals.Png) ||
                            previous.CardinalGenerationCount != current.CardinalGenerationCount)
                            throw new InvalidOperationException("A time-zone edit regenerated cardinal directions.");
                        if (current.Settings.Zone != zoneId || current.Raw.TimeZoneId != zoneId) throw new InvalidOperationException("A system time-zone change left stale data or graph settings.");
                        var firstLocal = TimeZoneInfo.ConvertTime(current.Run.Rows[0].Start, simulatedSystemZone);
                        if (firstLocal.Date != current.Settings.Start || firstLocal.TimeOfDay != TimeSpan.Zero)
                            throw new InvalidOperationException("The recalculated period does not start at local midnight.");
                        var shared = previous.Run.Rows.Join(current.Run.Rows, r => r.Start, r => r.Start, (a, b) => (a, b)).ToArray();
                        if (shared.Length < 600 || shared.Any(p => Math.Abs(p.a.BeforeTotal - p.b.BeforeTotal) > 1e-8))
                            throw new InvalidOperationException("Changing time zones reinterpreted UTC data or changed irradiance at the same instant.");
                        changes.Add(new { Zone = zoneId, FirstLocal = firstLocal.ToString("O"), current.Run.Rows.Count, SharedInstants = shared.Length });
                        Capture(window, Path.Combine(output, $"timezone-{zoneId}.png"));
                    }
                    AppServices.Export(window.Completed!, Path.Combine(output, "paris-export"));
                    window.SetTimeZoneForTest(false, "Romance Standard Time");
                    var manualPrevious = window.Completed;
                    window.Calculate();
                    await Until(() => window.Completed != manualPrevious, 30);
                    var manual = window.Completed!;
                    simulatedSystemZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                    window.RefreshSystemTimeZone();
                    await Task.Delay(700);
                    if (window.Completed != manual || manual.Settings.UseSystemTimeZone || manual.Settings.Zone != "Romance Standard Time")
                        throw new InvalidOperationException("A manual study time zone was overwritten by Windows.");
                    var selector = Find<System.Windows.Controls.ComboBox>(window, c => c.DisplayMemberPath == nameof(TimeZoneInfo.DisplayName));
                    var options = selector.Items.Cast<TimeZoneInfo>().ToArray();
                    if (options.Zip(options.Skip(1)).Any(p => p.First.BaseUtcOffset > p.Second.BaseUtcOffset) || options.Count(z => z.BaseUtcOffset == TimeSpan.FromHours(7)) < 2)
                        throw new InvalidOperationException("Time-zone choices are not sorted by offset or omit regions sharing an offset.");
                    selector.BringIntoView(); selector.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (selector.Template.FindName("PART_Popup", selector) is not System.Windows.Controls.Primitives.Popup { Child: FrameworkElement popup })
                        throw new InvalidOperationException("Time-zone dropdown did not open.");
                    Capture(popup, Path.Combine(output, "timezone-selector.png"));
                    selector.IsDropDownOpen = false;
                    Capture(window, Path.Combine(output, "timezone-controls.png"));
                    var fixedZone = TimeZoneSelection.FixedOffset(TimeSpan.FromHours(1));
                    var beforeFixed = window.Completed;
                    window.SetTimeZoneForTest(false, fixedZone.Id);
                    window.Calculate();
                    await Until(() => window.Completed != beforeFixed, 30);
                    var fixedResult = window.Completed!;
                    if (fixedResult.Settings.Zone != fixedZone.Id || fixedResult.Raw.TimeZoneId != fixedZone.Id || TimeZoneSelection.Resolve(fixedResult.Settings.Zone).GetUtcOffset(fixedResult.Run.Rows[0].Start) != TimeSpan.FromHours(1))
                        throw new InvalidOperationException("A fixed offset was not propagated to the calculation.");
                    AppServices.Export(fixedResult, Path.Combine(output, "fixed-offset-export"));
                    Capture(window, Path.Combine(output, "timezone-fixed.png"));
                    AppData.Write(Path.Combine(output, "timezone-result.json"), new { Passed = true, InitialSystemZone = first.Settings.Zone, Changes = changes, ManualOverridePreserved = true, FixedOffsetSupported = true });
                }
                var modelHashes = new Dictionary<string, string>();
                foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith("Models.")))
                { using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!; modelHashes[resource] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
                string[] dependencies = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => new[] { "coreclr.dll", "onnxruntime.dll", "DirectML.dll", "OpenCvSharpExtern.dll", "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll" }.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase)).Select(m => m.FileName).ToArray();
                if (Environment.GetEnvironmentVariable("SOLARSHADE_CALIBRATION_IMAGES") is { Length: > 0 } calibrationImages)
                {
                    var calibration = await window.CalibrateForTest(new UserSettings { CalibrationFolder = calibrationImages });
                    if (calibration.Profile.Calibration.UsedImageCount < 3 || !File.Exists(calibration.Path)) throw new InvalidOperationException("Calibration integration failed.");
                    var beforeCalibrationUpdate = window.Completed;
                    window.SetProfileForTest(calibration.Path); window.Calculate();
                    await Until(() => window.Completed != beforeCalibrationUpdate, 120);
                    if (!window.ExportEnabledForTest || !File.Exists(calibration.Path)) throw new InvalidOperationException("Durable calibration did not survive the next update.");
                    AppServices.Export(window.Completed!, Path.Combine(output, "calibrated-export"));
                    AppData.Write(Path.Combine(output, "calibration-result.json"), new { calibration.Details, calibration.Profile, calibration.Path });
                }
                if (Environment.GetEnvironmentVariable("SOLARSHADE_TEST_LIVE") == "1")
                {
                    var live = new List<object>();
                    for (int provider = 0; provider < 2; provider++)
                    {
                        var settings = timeZoneTest
                            ? new UserSettings { Provider = provider, Latitude = 43.5, Longitude = 1.5, Start = new(2023, 3, 2), End = new(2023, 3, 2), Zone = "Romance Standard Time" }
                            : new UserSettings { Provider = provider, Start = new(2025, 5, 15), End = new(2025, 5, 15), Zone = "SE Asia Standard Time" };
                        var data = await AppServices.Fetch(settings, true, CancellationToken.None);
                        var selected = IrradianceDatasets.SelectCalendarRange(data, settings.Start, settings.End, TimeZoneSelection.Resolve(settings.Zone)).Intervals;
                        if (selected.Count != 24) throw new InvalidOperationException("Live API window did not select one complete day.");
                        var selectedZone = TimeZoneSelection.Resolve(settings.Zone);
                        var firstStart = selected[0].Start;
                        var localStart = TimeZoneInfo.ConvertTime(firstStart, selectedZone);
                        if (timeZoneTest && (localStart.Offset != TimeSpan.FromHours(1) || localStart.TimeOfDay != TimeSpan.Zero)) throw new InvalidOperationException("Live Paris data did not start at 00:00 +01:00.");
                        live.Add(new { data.Source, SelectedRows = selected.Count, data.FetchedUtc, data.TimestampConvention, settings.Zone, FirstIntervalLocal = localStart.ToString("O") });
                    }
                    AppData.Write(Path.Combine(output, "live-providers.json"), live);
                }
                await VerifyMaskEditor(window, output);
                AppData.Write(Path.Combine(output, "smoke-result.json"), new { Passed = true, Portable = portable, LoadedExampleWithoutCalculation = startsExample, RestoredSavedSettings = portable && !startsExample, ExecutableDirectory = AppContext.BaseDirectory, WorkingDirectory = Environment.CurrentDirectory, DataDirectory = AppData.Root, FirstSettings = first.Settings, UiReadyMilliseconds = readyMs, FirstRunMilliseconds = firstMs, ExplicitPanelUpdateMilliseconds = editMs, FirstDebugDirectory = first.DebugDirectory, SecondDebugDirectory = second.DebugDirectory, NativeCadence = first.Raw.NativeCadence, FirstRows = first.Run.Rows.Count, first.Run.BeforeEnergy, first.Run.AfterEnergy, First = first.TotalMilliseconds, Second = second.TotalMilliseconds, second.PreparationCount, second.Run.SkyCoverage, ModelHashes = modelHashes, LoadedDependencies = dependencies, Models = Directory.Exists(AppData.PathFor("models-v1")) ? Directory.GetFiles(AppData.PathFor("models-v1")).Select(Path.GetFileName).ToArray() : [] });
                exitCode = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "smoke-error.txt"), ex.ToString()); }
            finally { app.Shutdown(exitCode); }
        };
        app.Run(window); return exitCode;
    }
    private static T Find<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        var pending = new Queue<DependencyObject>(); pending.Enqueue(parent);
        while (pending.TryDequeue(out var node))
        {
            if (node is T value && match(value)) return value;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        throw new InvalidOperationException("The application control is missing: " + typeof(T).Name);
    }
    private static async Task Until(Func<bool> ready, int seconds)
    { var end = DateTime.UtcNow.AddSeconds(seconds); while (!ready()) { if (DateTime.UtcNow > end) throw new TimeoutException("Application calculation did not complete. See app-data/errors.log."); await Task.Delay(100); } }
    private static async Task VerifyPv(MainWindow window, string output)
    {
        if (!window.Workspaces.Select("pv")) throw new InvalidOperationException("Completed irradiance did not unlock PV Autonomy.");
        var pv = window.PvAutonomy.Controller;
        if (pv.Result != null) throw new InvalidOperationException("Tab selection calculated PV automatically.");
        pv.SetDraft(PvSettingsDraft.Example()); await pv.EvaluateAsync();
        if (!pv.IsCurrent || pv.Result == null) throw new InvalidOperationException("PV evaluation failed: " + pv.Status);
        var direct = SolarShade.PvBattery.Integration.PanelBatterySimulation.Compute(window.Completed!.Run, pv.Draft.Parse(), window.Completed.Settings.Zone);
        if (!pv.Result.Hours.SequenceEqual(direct.Hours)) throw new InvalidOperationException("UI results differ from direct backend.");
        Capture(window, Path.Combine(output, "pv-full-period.png"));
        window.PvAutonomy.Chart.Day(pv.Result.Hours[0].Start.Date);
        Capture(window, Path.Combine(output, "pv-day.png"));
        double width = window.Width, height = window.Height;
        window.Width = 1080; window.Height = 720; window.UpdateLayout();
        foreach (double scale in new[] { 1d, 1.25, 1.5 }) Capture(window, Path.Combine(output, $"pv-minimum-{scale:0.00}.png"), scale);
        window.Width = width; window.Height = height; window.UpdateLayout();
        var export = await pv.ExportAsync(output);
        if (export == null) throw new InvalidOperationException("PV export failed: " + pv.Status);
        AppData.Write(Path.Combine(output, "pv-ui-result.json"), new { Passed = true, HourCount = pv.Result.Hours.Count, DirectBackendMatch = true,
            Export = export, pv.Result.Summary, SettingsRestored = AppData.Key(PvSettingsDraft.Restore(AppData.PathFor("pv-settings.json")).Parse()) == AppData.Key(pv.Draft.Parse()) });
        pv.SetDraft(pv.Draft with { Capacity = "" });
        if (pv.CanEvaluate || pv.CanExport || !window.ExportEnabledForTest) throw new InvalidOperationException("Invalid PV input affected upstream export or remained current.");
        window.Workspaces.Select("irradiance");
    }

    private static async Task VerifyMaskEditor(MainWindow window, string output)
    {
        window.Workspaces.Select("irradiance");
        var source = window.Completed?.Mask ?? throw new InvalidOperationException("The mask editor has no AI source.");
        if (source.Variant != null) throw new InvalidOperationException("Mask editor smoke test must begin with the AI mask.");
        using var original = Cv2.ImDecode(source.Png, ImreadModes.Grayscale);
        int x = (int)Math.Round(source.Disk.CenterX), y = (int)Math.Round(source.Disk.CenterY);
        byte value = original.At<byte>(y, x) == 255 ? (byte)0 : (byte)255;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var edit = Find<Button>(window, b => Equals(b.Content, "Edit mask"));
        _ = window.Dispatcher.BeginInvoke(() =>
        {
            MaskEditorWindow? editor = null;
            try
            {
                editor = Application.Current.Windows.OfType<MaskEditorWindow>().Single();
                if (editor.Owner != window || IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(window).Handle))
                    throw new InvalidOperationException("The secondary editor did not block the main study window.");
                Capture(editor, Path.Combine(output, "mask-editor-fit.png"));
                var maskOpacity = Find<Slider>(editor, s => s.Minimum == 0 && s.Maximum == 100);
                maskOpacity.Value = 30;
                Find<Slider>(editor, s => s.Maximum == 800).Value = 175;
                Find<Slider>(editor, s => s.Maximum == 300).Value = 25;
                Capture(editor, Path.Combine(output, "mask-editor-window.png"));
                editor.PaintStrokeForSmoke(x, y, 25, value);
                maskOpacity.Value = 65;
                Capture(editor, Path.Combine(output, "mask-editor-painted.png"));
                Find<TextBox>(editor, t => t.Text.StartsWith("Edited ", StringComparison.Ordinal)).Text = "Smoke edited mask";
                Find<Button>(editor, b => Equals(b.Content, "Save as new mask"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                completion.SetResult();
            }
            catch (Exception ex)
            {
                editor?.CloseForSmoke(); completion.SetException(ex);
            }
        }, DispatcherPriority.ApplicationIdle);
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await completion.Task;
        string? id = window.Irradiance.Controller.Settings.SelectedMaskId;
        if (id == null || window.UpdateStateForTest == UpdateState.UpToDate || window.ExportEnabledForTest)
            throw new InvalidOperationException("Saving an edit did not select it and invalidate the previous result.");
        Capture(window, Path.Combine(output, "mask-editor-selected.png"));
        window.Calculate(); await Until(() => window.UpdateStateForTest == UpdateState.UpToDate, 90);
        var accepted = window.Completed!;
        if (accepted.Mask?.Variant?.Id != id || !window.ExportEnabledForTest)
            throw new InvalidOperationException("Edited mask did not reach the accepted calculation.");
        string exported = AppServices.Export(accepted, Path.Combine(output, "mask-editor-export"));
        string provenance = File.ReadAllText(Path.Combine(exported, "02-sky-mask", "mask-provenance.json"));
        if (!provenance.Contains(id, StringComparison.Ordinal) || !provenance.Contains("Edited", StringComparison.Ordinal))
            throw new InvalidOperationException("Edited mask provenance was lost from the export.");
        var maskPicker = Find<ComboBox>(window, c => c.Items.Count > 1 &&
            c.Items[0]?.ToString()?.StartsWith("AI mask", StringComparison.Ordinal) == true);
        maskPicker.SelectedIndex = 0;
        await window.InputsSettled;
        if (window.Irradiance.Controller.Settings.SelectedMaskId != null || !window.ExportEnabledForTest ||
            window.Completed?.Mask?.Variant != null)
            throw new InvalidOperationException("Choosing the AI mask did not restore its accepted result.");
        Capture(window, Path.Combine(output, "mask-editor-ai-return.png"));
        maskPicker.SelectedIndex = 1;
        await window.InputsSettled;
        if (window.Irradiance.Controller.Settings.SelectedMaskId != id || !window.ExportEnabledForTest ||
            window.Completed?.Mask?.Variant?.Id != id)
            throw new InvalidOperationException("Returning to the edited mask did not restore its accepted result.");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var editor = Application.Current.Windows.OfType<MaskEditorWindow>().Single();
                if (IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(window).Handle))
                    throw new InvalidOperationException("The editor did not block main controls on reopening.");
                Find<Button>(editor, b => Equals(b.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                cancelled.SetResult();
            }
            catch (Exception ex) { cancelled.SetException(ex); }
        }, DispatcherPriority.ApplicationIdle);
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await cancelled.Task;
        await window.InputsSettled;
        if (window.Irradiance.Controller.Settings.SelectedMaskId != id || !window.ExportEnabledForTest)
            throw new InvalidOperationException("Cancel changed the selected mask or calculation.");
        AppData.Write(Path.Combine(output, "mask-editor-result.json"), new { Passed = true, Modal = true,
            OpacityAdjusted = true, ZoomAdjusted = true, BrushAdjusted = true, VariantId = id,
            SelectedInCalculation = true, ExportIdentifiesEdit = true, DropdownRestoredAiAndEdit = true,
            CancelPreservedStudy = true });
    }

    private static void Capture(FrameworkElement window, string path, double scale = 1)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}

