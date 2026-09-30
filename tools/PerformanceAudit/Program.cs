using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SolarShade.Desktop;
using SolarShade.Camera;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;
using SolarShade.PvBattery;
using SolarShade.PvBattery.IO;
using SolarShade.PvBattery.Integration;
using SolarShade.SkyPhotoMasking;

internal static class Program
{
    static string output = "", installed = "", scenario = "";
    static readonly List<object> measurements = [];
    static readonly List<double> ticks = [];
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double previousTick;
    static T Measure<T>(string name, Func<T> action)
    {
        long allocated = GC.GetTotalAllocatedBytes(); var cpu = Process.GetCurrentProcess().TotalProcessorTime; var watch = Stopwatch.StartNew();
        T value = action();
        Log(new { Scenario = scenario, Name = name, Ms = watch.Elapsed.TotalMilliseconds, CpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated });
        return value;
    }
    static void Measure(string name, Action action) => Measure(name, () => { action(); return 0; });
    static void Log(object value) { measurements.Add(value); Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    [STAThread] static int Main(string[] args)
    {
        installed = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; ticks.Add(now - previousTick); previousTick = now; };
        timer.Start(); int exit = 0;
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Run(args.Length > 2 ? args[2] : "month");
            }
            catch (Exception ex) { Console.WriteLine(ex); exit = 1; }
            finally
            {
                File.WriteAllText(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
                timer.Stop(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run(); return exit;
    }
    static async Task<Evaluation> Evaluate(AppServices service, UserSettings settings, string name)
    {
        ticks.Clear(); previousTick = clock.Elapsed.TotalMilliseconds;
        var events = new List<object>(); var watch = Stopwatch.StartNew(); double last = 0;
        long allocation = GC.GetTotalAllocatedBytes();
        var evaluation = await service.Evaluate(settings, false, message => { double now = watch.Elapsed.TotalMilliseconds; events.Add(new { Message = message, AtMs = now, SincePreviousMs = now - last }); last = now; }, null, CancellationToken.None);
        double elapsed = watch.Elapsed.TotalMilliseconds; await Task.Delay(50);
        Log(new { Scenario = scenario, Name = name, Ms = elapsed, ReportedMs = evaluation.TotalMilliseconds, MaxDispatcherGapMs = ticks.DefaultIfEmpty().Max(), DispatcherGapsOver100Ms = ticks.Count(v => v > 100), AllocatedBytes = GC.GetTotalAllocatedBytes() - allocation, PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, Rows = evaluation.Run.Rows.Count, Events = events });
        return evaluation;
    }
    static async Task Run(string mode)
    {
        scenario = mode;
        AppData.Root = Path.Combine(output, "Data");
        string debug = Path.Combine(output, "Debug Data");
        if (mode == "legacy")
        {
            // Reproduce directory/manifest topology, not personal diagnostic contents.
            // All generated legacy trees are read-only so optional cleanup cannot erase them.
            Directory.CreateDirectory(debug);
            int count = 0, files = 0;
            foreach (string original in Directory.GetDirectories(Path.Combine(installed, "Debug Data")))
            {
                string manifest = Path.Combine(original, "run.json");
                if (!File.Exists(manifest) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(original), @"^(calculation|orientation|calibration)-\d{8}-\d{6}-\d{3}-[a-fA-F0-9]{32}$")) continue;
                string target = Path.Combine(debug, Path.GetFileName(original)); Directory.CreateDirectory(target);
                File.Copy(manifest, Path.Combine(target, "run.json"));
                foreach (string file in Directory.GetFiles(original, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(original, file);
                    if (relative == "run.json") continue;
                    string destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllText(destination, "fixture"); files++;
                }
                File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly); count++;
            }
            Log(new { Scenario = mode, Name = "legacy-fixture", Directories = count, Files = files });
            using var store = new DebugDataStore(debug);
            for (int i = 0; i < 6; i++) Measure("prepare-inputs-legacy-" + i, () => store.PrepareInputs(new UserSettings()));
            return;
        }
        var settings = PortablePaths.Map(AppData.Read<UserSettings>(Path.Combine(installed, "Example/settings.json"))!, p => PortablePaths.Resolve(p, installed)) with { Zone = "W. Europe Standard Time", UseSystemTimeZone = false };
        if (mode == "calibration")
        {
            using var calibrationService = new AppServices(debug);
            for (int i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew(); await calibrationService.Calibrate(settings);
                Log(new { Scenario = mode, Name = "calibration-all-stages", Ms = watch.Elapsed.TotalMilliseconds });
            }
            return;
        }
        if (mode == "cpu-mask")
        {
            foreach (int resolution in new[] { 1024, 512 })
            {
                using var masker = new SkyPhotoMasker(new() { ModelsDirectory = Path.Combine(AppContext.BaseDirectory, "SkyPhotoModels"), Acceleration = MaskAcceleration.Cpu, CpuThreads = 2, InputSize = resolution });
                for (int i = 0; i < 3; i++)
                {
                    var value = await Task.Run(() => masker.CreateMaskDetailed(settings.SkyImage, settings.Model));
                    Log(new { Scenario = mode, Name = "cpu-mask-" + resolution, Iteration = i, value.TotalMilliseconds, value.SessionLoadMilliseconds, value.InferenceMilliseconds, value.ExecutionProvider });
                }
            }
            return;
        }
        if (mode == "year")
        {
            settings = settings with { Start = new(2025, 1, 1), End = new(2025, 12, 31), ImportPath = Path.Combine(output, "synthetic-year.csv"), ImportWindow = 1, Zone = "UTC" };
            var sun = new SolarPositionModule(new(settings.Latitude, settings.Longitude, settings.Elevation));
            var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var csv = new StreamWriter(settings.ImportPath); csv.WriteLine("Timestamp,BHI,DHI");
            for (int i = 0; i < 8760; i++) { var t = start.AddHours(i); double up = sun.Calculate(t.AddMinutes(30)).Direction.Up; csv.WriteLine(FormattableString.Invariant($"{t:O},{600 * Math.Max(0, up)},{(up > 0 ? 100 : 0)}")); }
        }
        AppData.SaveSettings(settings);
        using var service = new AppServices(debug);
        var result = await Evaluate(service, settings, "update-first");
        for (int i = 0; i < 3; i++) result = await Evaluate(service, settings, "update-unchanged-" + i);
        for (int i = 0; i < 3; i++)
        {
            settings = settings with { PanelTilt = settings.PanelTilt + 1 };
            Measure("observe-panel-edit-" + i, () => service.ObserveInputs(settings));
            result = await Evaluate(service, settings, "update-panel-edit-" + i);
        }
        for (int i = 0; i < 3; i++)
        {
            Measure("recheck-source-and-artifacts", () => service.ObserveInputs(settings, refreshSources: true, verifyArtifacts: true));
            Measure("readiness-inspect", () => IrradianceReadiness.Inspect(service, result));
            Measure("prepare-inputs-no-legacy", () => service.PrepareInputs(settings));
        }
        var geometry = Field<SolarTimeline>(service, "geometry");
        var transposed = Field<PreparedTranspositionResult>(service, "transposedCache");
        var scene = Field<PanelSkyScene>(service, "scene");
        var mask = result.Mask!;
        var profile = CameraProfileFiles.Load(settings.ProfilePath, mask.Result.Width, mask.Result.Height, settings.CoverageAngle);
        var projection = new CalibratedSkyProjection(profile.Calibration, CameraPose.FromImageBottom(settings.BottomAzimuth, settings.CameraTilt, settings.CameraRoll), new(mask.Disk.CenterX, mask.Disk.CenterY, mask.Disk.Radius));
        var site = new SolarSite(settings.Latitude, settings.Longitude, settings.Elevation);
        var stageRoot = Path.Combine(output, "stage-probes"); Directory.CreateDirectory(stageRoot);
        for (int i = 0; i < 3; i++)
        {
            Measure("import-xlsx-or-csv", () => IrradianceDatasetFiles.Import(settings.ImportPath, TimeZoneSelection.Resolve(settings.Zone), TimeSpan.FromHours(1), settings.ImportWindow == 1 ? TimestampLabel.Start : TimestampLabel.End));
            Measure("solar-compute", () => new SolarPositionModule(site).PrepareIntervals(geometry.Dataset, settings.Substeps));
            Measure("solar-xlsx", () => SolarIntervalWorkbook.Export(geometry, site, Path.Combine(stageRoot, "solar")));
            Measure("sun-path-generate", () => SunPathOverlayGenerator.Generate(geometry, projection, TimeZoneSelection.Resolve(settings.Zone)));
            Measure("cardinal-generate", () => CardinalDirectionOverlayGenerator.Generate(projection));
            var newScene = Measure("visibility-scene", () => new PanelSkyScene(SkyShadingModule.DecodeMask(mask.Png), profile.Calibration, CameraPose.FromImageBottom(settings.BottomAzimuth, settings.CameraTilt, settings.CameraRoll), new(mask.Disk.CenterX, mask.Disk.CenterY, mask.Disk.Radius)));
            Measure("transposition-compute", () => TranspositionModule.ComputePrepared(geometry, new(settings.PanelTilt, settings.PanelAzimuth), settings.Isotropic ? DiffuseModel.Isotropic : DiffuseModel.HayDavies));
            Measure("transposition-xlsx", () => TranspositionModule.ExportPrepared(transposed, Path.Combine(stageRoot, "panel.xlsx")));
            Measure("shading-cold-directions", () => ShadingCorrectionModule.ApplyToPanel(transposed, newScene));
            Measure("shading-warm-directions", () => ShadingCorrectionModule.ApplyToPanel(transposed, scene));
            Measure("shading-xlsx-json", () => ShadingCorrectionModule.ExportPanel(result.Run, Path.Combine(stageRoot, "shading")));
            var cacheType = typeof(AppServices).Assembly.GetType("SolarShade.Desktop.CurrentValueCache")!;
            object cache = Activator.CreateInstance(cacheType, debug)!;
            foreach (var entry in new (string Name, object Value)[] { ("solar", geometry), ("panel", transposed), ("shading", result.Run) })
            {
                Measure("cache-write-" + entry.Name, () => cacheType.GetMethod("Write")!.MakeGenericMethod(entry.Value.GetType()).Invoke(cache, [entry.Name + "-probe", "benchmark", entry.Value]));
                Measure("cache-read-" + entry.Name, () => cacheType.GetMethod("Read")!.MakeGenericMethod(entry.Value.GetType()).Invoke(cache, [entry.Name + "-probe", "benchmark"]));
                Measure("cache-reject-" + entry.Name, () => cacheType.GetMethod("Read")!.MakeGenericMethod(entry.Value.GetType()).Invoke(cache, [entry.Name + "-probe", "different-key"]));
            }
            var battery = Measure("battery-compute", () => PanelBatterySimulation.Compute(result.Run, new(2, .2, .9, Enumerable.Repeat(30d, 24).ToArray(), 1000, .5), settings.Zone));
            Measure("battery-outputs", () => { BatteryFiles.WriteCsv(Path.Combine(stageRoot, "battery.csv"), battery); BatteryFiles.WriteJson(Path.Combine(stageRoot, "battery.json"), battery); BatteryFiles.WriteXlsx(Path.Combine(stageRoot, "battery.xlsx"), battery); });
            Measure("chart-irradiance-render", () => { var chart = new IrradianceChart(); chart.SetData(result.Run.Rows, TimeZoneSelection.Resolve(settings.Zone)); Render(chart); });
            Measure("chart-battery-render", () => { var chart = new PvSystemChart(); chart.SetResult(battery); Render(chart); });
            await Task.Delay(30);
        }
        Log(new { Scenario = scenario, Name = "output-sizes", DebugBytes = Directory.GetFiles(debug, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), CacheBytes = Directory.GetFiles(AppData.Root, "*.gz", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
    }
    static void Render(FrameworkElement element)
    {
        element.Measure(new Size(1100, 500)); element.Arrange(new Rect(0, 0, 1100, 500)); element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1100, 500, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
    }
}
