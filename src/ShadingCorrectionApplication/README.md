# Shading correction

`SolarShade.ShadingCorrection` is a C# library targeting .NET 10 Windows x64, matching the shading module. Runtime calculation is in memory and needs no file paths or network calls.

For each timestamp, in horizontal W/m²:

```text
corrected direct  = DirectHorizontal  * (1 - direct shading factor)
corrected diffuse = DiffuseHorizontal * (1 - diffuse shading factor)
corrected total   = corrected direct + corrected diffuse
```

A factor of 0 leaves the component unchanged; 1 removes it completely.

## Use with the existing modules

Add a project reference to `SolarShade.ShadingCorrection/SolarShade.ShadingCorrection.csproj`.

```csharp
using SolarShade.ShadingCorrection;

// download is the successful IrradianceResult from IrradianceClient.FetchAsync.
// shading is the ShadingResult from SkyShadingModule.Evaluate or FileRun.Result.
var corrected = ShadingCorrectionModule.Apply(download.Samples, shading);
foreach (var sample in corrected)
{
    Console.WriteLine($"{sample.TimestampUtc:O}: direct={sample.DirectHorizontal}, " +
        $"diffuse={sample.DiffuseHorizontal}, total={sample.TotalHorizontal} W/m²");
}

// A single sample also accepts independent factors, including time-varying diffuse factors.
var one = ShadingCorrectionModule.Apply(download.Samples[0],
    directShadingFactor: 0.25, diffuseShadingFactor: 0.2);
```

The batch overload uses each `ShadingRow.Direct.ShadingFactor` and the current upstream module's shared `ShadingResult.Diffuse.ShadingFactor`. Rows match by `DateTimeOffset` instant, including equivalent timestamps with different offsets. Output retains the irradiance input order and timestamp labels. Missing, extra, or duplicate timestamps are rejected; no interpolation or time shift is applied. Empty matching inputs return an empty list.

Irradiance must be finite and nonnegative; known factors must be finite and within [0, 1]. Invalid values throw rather than being silently clamped. A null factor produces a null corrected component for positive irradiance. Zero incoming irradiance remains zero even with an unknown factor. Total is null if either corrected component is unknown. Coverage assumptions and interval averaging stay with the upstream shading module.

## Inclined panels and automatic Debug Data

`Apply` remains the horizontal API. Use `ApplyToPanel` for inclined panels after the irradiance,
solar timeline and transposition stages. It consumes the transposition library's direct,
isotropic-diffuse and circumsolar-diffuse contributions at each integration substep.

```csharp
var scene = new PanelSkyScene(mask, calibration,
    CameraPose.FromImageBottom(bottomBearing, cameraTilt, cameraRoll), imageDisk);
var run = ShadingCorrectionModule.ApplyToPanel(transposed, scene, debugDirectory, cancellationToken);
// UI displays these returned values without recomputing scientific summaries.
Console.WriteLine($"{run.BeforeEnergy} -> {run.AfterEnergy} kWh/m²; loss {run.LossPercent}%");
```

The stage writes `shading-visibility.xlsx`, `shading-correction-factors.xlsx`,
`panel-shaded.xlsx` and `panel-results.json` before returning. `ArtifactPaths` lists its output.
The computational overload omits the debug-directory argument; `ExportPanel` can persist that
same returned result later without repeating calculations. A null scene gives an unshaded
summary with unavailable shaded components; no shaded workbooks are invented.

Output rows retain source interval IDs, labels, source bounds and selected bounds. Energy
uses each selected interval's actual duration, supporting hourly, 15-minute and other explicit
source intervals without generating a new time series. Panel transmission uses **1=open,
0=blocked**, while the older horizontal `Apply` accepts **0=open, 1=blocked** shading factors.
Zero-baseline transmission remains null. Unknown sky is blocked in the main curve and open
in its upper bound. Only Hay-Davies and isotropic diffuse are supported; ground reflection
is excluded. The reusable scene caches sky rays and solar-disk moments across panel edits.

Standalone correction artifacts record the panel orientation, source metadata, source coordinates
when available, cadence and dataset fingerprint. Source coordinates are not a replacement for
the solar calculation site: retain the solar-stage artifacts (and application run manifest)
to identify the latitude, longitude and elevation actually used for solar geometry.

## Build and validate

```powershell
dotnet build src/ShadingCorrectionApplication/SolarShade.ShadingCorrection -c Release
dotnet test src/ShadingCorrectionApplication/Tests -c Release
```

The separate test project checks upstream timestamp matching, component correction, unknown visibility, invalid values, and mismatched inputs. Both projects are included in `SolarShade.sln`.
