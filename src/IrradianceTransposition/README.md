# Unshaded panel irradiance

`SolarShade.Irradiance.Transposition` converts direct-horizontal (BHI) and diffuse-horizontal (DHI) irradiance into **direct and sky-diffuse irradiance on the front of a tilted panel**. Select Hay–Davies, Perez–Driesse (default), or an isotropic baseline. Panel tilt is 0° for horizontal and 90° for vertical. Azimuth is clockwise from **true north**: north 0°, east 90°, south 180°, west 270°.

This stage accounts for orientation and solar position. Images and shading factors are outside its inputs. Ground reflection, rear-side irradiance, glazing losses and electrical conversion are also outside these two output components. The original irradiance downloader and solar-position function remain unchanged.

## App integration

The production library targets .NET 8 and depends only on the existing irradiance contracts and the .NET base libraries. It accepts a solar-position callback to keep image/native dependencies out of the numerical module. The following example connects the existing NOAA/Meeus module and the downloaded data:

```csharp
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;

// download is a successful IrradianceResult from IrradianceClient.FetchAsync.
var service = IrradianceService.OpenMeteo; // must match download's provider
var solar = new SolarPositionModule(new SolarSite(10.8, 106.7, 0));
SunPosition Position(DateTimeOffset time)
{
    var geometric = solar.Calculate(time);
    // Hay/Perez use apparent zenith. Apply once; omit this adapter for already apparent angles.
    return SunPosition.FromGeometricNoaa(geometric.ZenithDegrees, geometric.AzimuthDegrees);
}

var panel = new PanelOrientation(TiltDegrees: 30, AzimuthDegrees: 0);
var window = SamplingWindow.ForService(service)
    ?? throw new ArgumentException("Choose this provider's actual averaging window explicitly.");
var options = new TranspositionOptions
{
    TimeZone = TimeZoneInfo.Local,
    Provenance = "Open-Meteo ERA5; 10.8 N, 106.7 E, 0 m; NOAA/Meeus with NOAA refraction."
};

var result = TranspositionModule.ComputeToWorkbook(
    download.Samples, panel, Position, window, "panel-irradiance.xlsx",
    DiffuseModel.PerezDriesse, options);
int status = result.Status; // 1: complete conversion AND export; 0: failure
Console.WriteLine(result.Message);

// Memory-only app use:
var data = TranspositionModule.Compute(download.Samples, panel, Position, window,
    DiffuseModel.HayDavies, options);

// Simple function returning only 0/1, including 0 for cancellation:
int exported = TranspositionModule.TryExport(download.Samples, panel, Position,
    window, "panel-hay.xlsx", DiffuseModel.HayDavies, options);
```

`Compute` and `ComputeToWorkbook` propagate cancellation; their ordinary validation failures return status 0 and no partial rows. An error message identifies a failing irradiance row and timestamp. A failed conversion/export preserves an existing output file. Use different paths for concurrent exports. A caller-supplied callback must return valid solar angles and should throw an explanatory exception if unavailable.

Each `TransposedSample` contains `Direct`, `SkyDiffuse`, their `Total` in W/m², and separate `DirectFactor` and `DiffuseFactor` relative to that row's horizontal inputs. Factors can exceed 1. A factor is null when its input component is zero. `EffectiveDni`, `MeanDaylightCosine` and `Flags` expose the interval calculation; diagnostics are null when they were not calculated, such as the exact horizontal identity path. Do not treat these transposition factors as shading factors constrained to [0, 1].

If native **instantaneous DNI** is available, bypass horizontal inversion:

```csharp
var poa = SkyTransposition.EvaluateDni(
    panel, new SunPosition(ZenithDegrees: 60, AzimuthDegrees: 180),
    dni: 800, dhi: 100, extraterrestrialDni: SkyTransposition.ExtraterrestrialDni(time),
    model: DiffuseModel.PerezDriesse);
```

An hourly mean DNI is not an instantaneous DNI. Integrate actual sub-hour samples when available; do not silently substitute an hourly mean into this instantaneous function. The current batch API consumes the extractor's BHI/DHI contract.

## Time and interval assumptions

Both models share direct beam `DNI × max(0, cos(AOI))`, zero when the Sun is below the apparent horizon. Hay–Davies separates isotropic and circumsolar diffuse; Perez–Driesse additionally represents horizon brightening/darkening and uses continuous quadratic splines. Their diffuse output is not a simple geometric sky-area fraction. See [pvlib Hay–Davies](https://pvlib-python.readthedocs.io/en/v0.15.2/reference/generated/pvlib.irradiance.haydavies.html) and [Perez–Driesse](https://pvlib-python.readthedocs.io/en/v0.15.2/reference/generated/pvlib.irradiance.perez_driesse.html).

For interval means, the batch implementation assumes constant DNI during the daylight portion and constant DHI throughout the interval. With equally spaced midpoint samples and `mu = max(0, cos(zenith))`, it calculates:

```
effective DNI = supplied mean BHI / mean(mu)
mean direct POA = effective DNI × mean(daylight × max(0, cos(AOI)))
mean sky diffuse POA = mean(selected sky model at each substep)
```

The default is 60 midpoint samples per hour. This resolves geometry, not cloud changes within the hour. The choice of temporal irradiance shape is an engineering approximation, not part of the original empirical Hay/Perez formulas. [pvlib's interval-average example](https://pvlib-python.readthedocs.io/en/v0.15.2/gallery/irradiance-transposition/plot_interval_transposition_error.html) explains why provider labels cannot be used as instantaneous positions without considering the interval.

`SamplingWindow.ForService` uses NASA POWER's following hour and Open-Meteo/CAMS/CDS's preceding hour, matching the existing solar module. NSRDB and Oikolab require an explicit window. Arbitrary offsets/durations and instantaneous data are supported. Timestamps must be strictly increasing and unique as UTC instants; gaps are not filled. Supply the original provider convention even after converting its label to local time.

Solar positions exported only at the hourly labels are insufficient for 60-point integration. The callback recomputes positions at the actual quadrature instants. The validator saves those exact instants and angles in compressed audit files for debugging.

`FromGeometricNoaa` applies the published [NOAA refraction approximation](https://gml.noaa.gov/grad/solcalc/calcdetails.html) explicitly. It is an approximate standard-atmosphere adapter, without measured local pressure/temperature. For an atmospheric model using measured conditions, supply already apparent angles. Extraterrestrial DNI uses Spencer's distance correction, a 1366.1 W/m² solar constant, and UTC day-of-year; relative air mass uses Kasten–Young 1989.

## Boundaries and diagnostics

- Positive BHI with mean daylight cosine below `MinimumMeanCosine` (default 1e-6), or inferred DNI above `MaximumInferredDni` (default 1500 W/m²), fails the complete batch. The bound is a configurable plausibility check, not a universal physical cutoff. No beam is capped or silently discarded.
- Daylight substeps above 85° zenith set `LowSun`. The instantaneous model keeps pvlib's published denominator floors: 0.01745 for Hay–Davies, cos(85°) for Perez–Driesse; Perez's F1 is clipped to [0, 0.9]. These regularizations can make raw instantaneous diffuse differ from DHI even on a horizontal surface near the horizon.
- The horizontal-to-horizontal batch case returns the supplied BHI and DHI exactly. No angular transposition or DNI inference is needed there. This is an explicit boundary condition, separate from the raw instantaneous model.
- Below-horizon substeps with positive interval DHI use the isotropic sky view factor `(1 + cos(tilt)) / 2` and set `NightDiffuseIsotropic`. This includes the twilight part of a sunrise/sunset interval; it does not imply the whole row is nighttime. It avoids evaluating daylight-only anisotropic formulas below the horizon.
- Zero irradiance returns zero without evaluating the solar callback. Diffuse-only daylight rows still evaluate solar position, because Perez–Driesse uses it even when DNI is zero.

XLSX contains exactly three columns: **timestamp, direct on panel, sky diffuse on panel**. Values retain full numeric precision and display two decimals. Timestamps are ISO 8601 text with machine-local UTC offsets and fractional-second precision, preserving the actual instant across DST. Model, orientation, interval, assumptions, diagnostic counts and caller provenance are in document properties. Per-row flags remain available in memory and in validator audits.

The existing shading-correction module currently uses horizontal irradiance/factors. These new panel values have their own types and headers: later shading integration must account for the tilted receiver and chosen diffuse distribution.

## Performance and validation

The hot path uses scalar double-precision math, cached panel trigonometry, pooled solar-position buffers, and streamed XLSX XML. It requires no GPU, AVX or Excel/Python installation. The default callback execution is serial, allowing stateful callbacks. Set `MaxDegreeOfParallelism = 0` for a conservative CPU/memory-based choice **only when the callback is thread safe**, or choose a positive worker count. Output order stays deterministic. The validator's stateless solar callback enables automatic parallelism.

```powershell
dotnet test src/IrradianceTransposition/Tests -c Release
dotnet run --project src/IrradianceTransposition/Validator -c Release
```

The validator targets the existing .NET 10 Windows solar integration; the numerical tests and library target .NET 8. It uses cached May 2025 NASA POWER and Open-Meteo inputs, so it needs no network or credentials. Default receiver: 30° tilt toward north at the input's HCM City coordinates. Optional positional arguments: input folder, output folder, tilt, azimuth, substeps. See [validation evidence and sample workbooks](VALIDATION.md).

Python is used only to create reference fixtures and independently verify exports:

```powershell
# In an optional development venv with pvlib==0.15.2 and openpyxl:
python src/IrradianceTransposition/Validator/Tools/pvlib_reference.py generate src/IrradianceTransposition/Tests/Fixtures/pvlib-0.15.2.json
python src/IrradianceTransposition/Validator/Tools/pvlib_reference.py verify src/IrradianceTransposition/Validator/results/may2025
```

Matching pvlib verifies the implementation; it does not establish field accuracy in HCM City. Retain [the third-party notice](THIRD_PARTY_NOTICES.md) when distributing the library.
