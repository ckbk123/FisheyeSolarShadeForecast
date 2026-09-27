# Solar positions and fisheye shading

Native C# module under `src/SolarPositionAndShading`, integrated into `SolarShade.sln`. Targets .NET 10 Windows x64, matching the existing Core/Camera projects. No Python, Excel installation, web service, model weights, or GPU is required at runtime.

## Module 1: site and timestamps → solar positions

```csharp
using SolarShade.Shading;

var site = new SolarSite(latitudeDegrees, longitudeDegrees, elevationMetres);
var solar = new SolarPositionModule(site);
var timestamps = ShadingWorkbook.ReadTimestamps(irradianceXlsx);
var positions = solar.ComputeToWorkbook(timestamps, "solar-positions.xlsx");
```

The first sheet has **Timestamp (local, UTC offset), Solar zenith (degrees), Solar azimuth (degrees)**, in that order. Every timestamp is exported, including night hours, for downstream panel-incidence work. `Calculate(timestamps)` returns the same typed `SolarPositionRow` data without file I/O. `Calculate(timestamp)` returns one `SolarAngles` value.

Positions use NOAA's compact Meeus solar equations, with a WGS84 observer-parallax correction to retain the elevation input. This replaces Astronomy Engine without changing the public API. Atmospheric refraction remains disabled. UTC approximates UT1, as in NOAA's calculator; this is not a full high-precision implementation of every algorithm in Meeus's book. Azimuth uses a stable vector/atan2 conversion. At exact zenith/nadir, where azimuth is undefined, zero is returned. UTC years are restricted to 1900-2100; see [validation](VALIDATION.md) for tested dates and measured differences. No astronomy package or network call is required at runtime.

Azimuth is clockwise from **true north**: N=0°, E=90°, S=180°, W=270°. Zenith is 0° vertically upward, 90° at the horizon. Site elevation is metres; latitude/longitude use degrees. Elevation is required in the API and is not inferred from the irradiance service's grid. The validation example provisionally uses **10.8° N, 106.7° E, elevation 0 m**; the site's actual elevation was not supplied.

`DateTimeOffset` defines the instant. File input retains its existing offset; the machine culture is never used to interpret an offset-less date. The supplied XLSX reader reads the irradiance extractor's three-column format. Numeric Excel dates or text without an offset are rejected because they cannot recover the original instant. DST fall-back hours remain distinct; duplicate/decreasing instants fail, while gaps remain gaps. Do not apply the time-zone offset twice.

For future in-memory composition, `IrradianceClient.FetchAsync` already returns `IrradianceResult.Samples`. Those samples carry UTC instants. If local-offset labels are wanted in a standalone solar workbook, convert the labels with the extractor's returned `TimeZoneId` before passing them to `ComputeToWorkbook`. No instant changes:

```csharp
var zone = TimeZoneInfo.FindSystemTimeZoneById(download.TimeZoneId);
var labels = download.Samples.Select(s => TimeZoneInfo.ConvertTime(s.TimestampUtc, zone)).ToArray();
solar.ComputeToWorkbook(labels, "solar-positions.xlsx");
```

## Module 2: solar sequence and mask → shading

```csharp
var input = ShadingWorkbook.ReadIrradiance(irradianceXlsx);
var sampling = input.SuggestedSampling
    ?? throw new InvalidOperationException("Choose the provider's averaging window explicitly.");
var sequence = solar.Prepare(input.Samples, sampling);

// Directly accepts the encoded PNG returned by SkyPhotoMasker.CreateMask(...).
var mask = SkyShadingModule.DecodeMask(maskPngBytes);
var calibration = ShadingFilePipeline.ReadCalibration(
    calibrationYaml, mask.Width, mask.Height, validatedMaximumIncidentAngleDegrees);
var pose = new CameraPose(); // image bottom south, top north; optical axis vertically up
var engine = new SkyShadingModule(mask, calibration, pose);
var shading = engine.Evaluate(sequence);
ShadingWorkbook.Write("shading.xlsx", shading, site, pose, "My camera profile and source data");
```

Reuse `engine` while the mask/calibration/pose/options are unchanged. It owns a snapshot of the pixel data and polynomial; `Evaluate` can run concurrently. `SolarSequence` preserves timestamp order and contains the label position plus sub-hour unit directions. A caller can construct a sequence from its own solar positions; `TimeSampling.Instant` with one integration direction per row represents instantaneous visibility. Gated `Prepare` omits solar calculations on zero-direct rows. The complete standalone solar workbook still calculates **one label position** for those rows as requested; the costly disk and sub-hour work remains skipped.

### One complete file-to-file call

```csharp
var run = ShadingFilePipeline.Compute(
    irradianceXlsx, maskPng, calibrationYaml,
    validatedMaximumIncidentAngleDegrees, site, "shading.xlsx");
// Writes shading.xlsx and shading-solar-positions.xlsx.
// run.Result contains factors; run.TotalMilliseconds includes input and BOTH workbook writes.
```

The wrapper checks the location recorded in the irradiance workbook against `site`. Optional arguments expose time sampling, camera pose, shading options, the measured lens disk, and cancellation. Files are published through individual temporary-file renames; each existing file is preserved if its own write fails. The two-file pair is not a transactional database commit. An open/locked target causes an exception, not a silent partial-success return. The computational APIs return typed results and throw descriptive errors for invalid inputs.

### Camera orientation and calibration

`CameraPose(ImageTopAzimuthDegrees=0, TiltDegrees=0, RollDegrees=0)` implements the agreed phone-face-down pose: camera upward, photo bottom south. Positive tilt moves the optical axis toward the indicated image-top bearing; roll rotates the image axes about that axis. In this upward-looking sky convention, east is image left and west is image right. Heading uses true north; sensor magnetic heading must be corrected by the application. `CameraPose.FromLegacy(psi, omega)` adapts the Python orientation convention exactly, including sign changes.

All coordinates are in the **EXIF-oriented full-resolution image**. No automatic resizing, cropping, EXIF re-rotation, principal-point substitution, or north detection is performed. Supply a calibration for the same lens configuration and resolution. The polynomial is the existing OmniCalib incident-angle→radius polynomial, ascending coefficients, radians, zero constant term. It is not replaced with an equidistant or OpenCV lens model. Nonmonotonic projection within the requested range is rejected.

The native solver's `calibration.yml` does not certify hemisphere coverage. Pass the maximum angle established by independent calibration validation. Do not use a polynomial turning point as evidence of measured coverage. The example uses **66.43°**, per `docs/CALIBRATION_VALIDATION.md`. The validator also supplies the `LensDisk` measurements recorded by the masking validator through `ImageDisk(centerX, centerY, radius)`. A production caller can pass those same three values from the masking module's detailed result. Outside the disk, image bounds, or validated incident angle is unobserved and **assumed blocked by default**, as requested for this application.

### Shading definition and coverage

`ShadingFactor = 0` means no loss; `1` means fully blocked. Multiply the corresponding **horizontal** irradiance by `1 - factor`. These are optical visibility factors: panel incidence is not mixed into them. The instantaneous visibility of a fully covered small disk is largely independent of the receiver, but an hourly averaged factor uses temporal incidence weights. A future inclined-panel implementation should use appropriate panel-incidence weighting for its averaging interval rather than treating the horizontal hourly factor as exact for every panel tilt.

`ShadingRow.Direct` also contains coverage and lower/upper loss bounds. By default `MissingCoveragePolicy.AssumeBlocked` assigns blocked sky to uncovered directions and returns the upper loss bound as `ShadingFactor`, for both direct and diffuse shading. Partially covered disks retain the measured contribution of covered directions; fully uncovered disks have factor 1. The lower bound assumes uncovered sky is visible; the upper assumes it is blocked. Coverage/status fields remain available. Explicit `MissingCoveragePolicy.ReportUnknown` restores null factors for incomplete coverage. No rays disappear from the denominator to inflate visibility.

Zero-direct rows remain in place with status `SkippedZeroDirect`, neutral factor 0, blank solar angles in the **shading** sheet, and coverage N/A (stored as 0). Their positions are present in the separate **solar positions** workbook. Positive irradiance with all integration rays below the geometric horizon yields `PositiveDirectBelowHorizon` and unknown loss, exposing a time/source inconsistency. The first row and rows after gaps are evaluated normally; no trajectory-outlier suppression is used.

### Solar disk and hourly integration

The default **angular radius is 0.25°**, diameter 0.5°. `ShadingOptions.SolarAngularRadiusDegrees` exposes alternatives including the legacy 2.5°. The latter is a broad circumsolar approximation, not the physical photospheric radius; neither setting models a variable circumsolar radiance distribution or solar limb darkening.

The implementation samples a spherical cap uniformly in solid angle (128 points by default), rotates full 3-D unit rays, applies the calibrated polynomial, and bilinearly samples the binary mask for subpixel stability. It never divides a ray by depth, folds a rearward ray forward, rounds geometry prematurely, or fills a full-image trapezoid per hour. A robust tangent basis handles zenith crossings. The debug contour uses the same spherical boundary/projector.

Hourly results use 60 midpoint time samples, with horizontal cosine weights and geometric-horizon clipping. This assumes approximately constant DNI within each supplied hourly interval. It cannot reconstruct unmeasured sub-hour cloud variability from hourly means. Sampling density is configurable and convergence is documented. A spatially filled strip is not used as a substitute for time integration.

Provider windows, while retaining the original label:

| Provider | Default recognized window |
|---|---|
| NASA POWER | Following hour; NASA explicitly documents start-of-hour labels |
| Open-Meteo, CAMS, Copernicus CDS | Preceding hour; interval-end metadata |
| NSRDB, Oikolab, unrecognized metadata | Caller explicitly chooses the interval or `Instant` |

For typed samples, `TimeSampling.ForService(service)` returns the supported convention or null. For imported files, `SuggestedSampling` uses their source/interval metadata. [NASA's time convention](https://power.larc.nasa.gov/docs/faqs/other/) resolves the supplied POWER export even though the older extractor's metadata only says “native label; no phase shift.” The source labels themselves are unchanged.

### Diffuse component

The current diffuse result is constant for a static mask and pose, under an **isotropic sky and horizontal receiver** assumption. It integrates visible sky with `cos(zenith) dΩ`, not raw white-pixel percentage, using 65,536 cosine-distributed hemisphere rays. It has the same coverage bounds and optional conservative policy as the direct result.

For the supplied mask and validated angular range, coverage is about 84.01%, giving diffuse loss bounds approximately **16.37–32.36%**. A single definitive factor cannot be measured from the unseen part of this photograph. Wider validated coverage or an explicit outside-view assumption is needed.

Research direction: the [Perez model](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.irradiance.perez.html) distinguishes isotropic, circumsolar and horizon components. Their weights depend on the sun and atmospheric conditions, so a diffuse shading factor can vary with time. That extension is intentionally not substituted for the agreed constant-isotropic model. It would also need a consistent definition of which circumsolar energy belongs to direct versus diffuse irradiance.

## Performance and hardware

The runtime checks CPU count and managed SIMD support. PNG up-filter reconstruction uses available SIMD widths with a scalar fallback; mask tiles use Vector128 when supported. Independent hours run on a bounded worker pool, defaulting to half the logical processors. Uniform white/black regions use a conservative geometric footprint bound to return the exact quadrature result without testing every disk ray. Boundary regions use the full calculation. Buffers, cap samples, camera rotations and constant diffuse integrals are prepared once per engine.

`ShadingHardwareDetector.Detect()` enumerates hardware DXGI adapters and probes D3D12 support without allocating a GPU device. It reports CPU/SIMD capabilities, adapter names and memory. The measured PC is a Ryzen 7 5700X / RTX 3070. **The shading calculation uses CPU/SIMD, not GPU compute.** CUDA, DirectML and an NPU are not required. Mask inference is already handled by the sibling library; this stage mainly performs small ray batches and irregular mask reads. No unmeasured GPU speedup is claimed. See [the official non-allocating D3D12 probe](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-d3d12createdevice).

Fifty trials were recorded: five implementation changes and 45 combinations of temporal samples, disk samples and worker counts. The defaults retain 60×128 quadrature for its tighter numerical error. The strict **every-call 100 ms target was not achieved**: first calls include device probing/JIT/library startup, and filesystem/OneDrive activity introduces variance. Later complete month calls reached roughly 66–91 ms in the final run; all 30 calls together had median 125 ms, P95 159 ms, and first-call time 835 ms. The 8,760-row synthetic annual file-to-file load took 347–441 ms initially and 497–580 ms in the final recheck. These include both output workbooks. Do not advertise only the shading kernel's few milliseconds as user-visible latency. The in-memory API avoids file round trips naturally in the future application.

Limited-worker and disabled-managed-SIMD tests cover fallbacks; they do not establish performance on a physical Core i3 or an older Windows installation. Full measurements, warm-up effects, failed file-lock trial and accuracy evidence are in [VALIDATION.md](VALIDATION.md).

## Build, validate and inspect

```powershell
dotnet build SolarShade.sln -c Release
dotnet test SolarShade.sln -c Release
dotnet run --project src/SolarPositionAndShading/Validator -c Release -- --repeats 30 --convergence --nasa
# Optional synthetic annual workload / resumed optimization sweep:
dotnet run --project src/SolarPositionAndShading/Validator -c Release -- --annual --no-images
dotnet run --project src/SolarPositionAndShading/Validator -c Release -- --sweep --no-images
```

The validator defaults to the B5 full-resolution mask. `--elevation`, `--workers`, `--disk-samples`, `--time-samples`, `--repeats`, `--output`, and `--no-images` configure its run. Results are under `Validator/results`:

- `shading-month.xlsx` and `shading-month-solar-positions.xlsx`: Open-Meteo May 2025, 744 rows.
- `nasa-shading-month.xlsx` and `nasa-shading-month-solar-positions.xlsx`: POWER May 2025, 744 rows.
- `noon-20260531-radius-0.25-full.jpg` / `...-detail.png`, and `...radius-2.50...`: red footprints on the original sky photo, plus 4× detail crops.
- JSON measurements, geometry, convergence and independent validation.

The example overlay is **31 May 2026, 12:00 UTC+07:00 civil time**, at the above assumed site/elevation and agreed north-up pose. It is separate from the May **2025** irradiance sample. It is not astronomical solar transit. The native camera profile is provisionally paired with the matching 3000×4000 example; actual lens pairing, location and heading have not been field-verified. The true-size solar footprints are about 7.18×7.10 pixels (0.25° radius) and 71.83×71.00 pixels (2.5° radius), not enlarged in the full images.

Production sources are in `SolarShade.Shading/`: `SolarPositions.cs`, `SkyShading.cs`, `ShadingWorkbook.cs`, `FilePipeline.cs`, `MaskPng.cs`, `Hardware.cs`. Independent validation and tests remain separate. Python is only a development oracle; dependencies are pinned in `Tools/requirements-validation.txt`. Astronomy Engine is MIT-licensed; retain `ASTRONOMY_ENGINE_LICENSE.txt`. Existing Core/Camera/Irradiance dependencies remain unchanged.

