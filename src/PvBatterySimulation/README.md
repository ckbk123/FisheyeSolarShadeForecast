# PV–battery simulation

Standalone backend stage consuming **final shaded plane-of-panel interval-mean irradiance**. It returns stored energy and end-of-hour state of charge (SoC) for the complete supplied period. The stage is available through C# and an offline command line; application controls and pipeline caching are a later integration step.

## Inputs and units

| Settings property | Meaning |
|---|---|
| `PanelAreaM2` | Active panel surface area in m²; finite, nonnegative. |
| `PanelEfficiency` | Fixed electrical efficiency as a fraction in (0, 1], e.g. 0.20. |
| `ConversionEfficiency` | Overall PV-to-system conversion efficiency in (0, 1], applied once to PV generation. |
| `HourlyLoadWh` | Exactly 24 finite, nonnegative values, indexed 0–23 by the study's local civil hour. Wh per nominal one-hour slot, numerically equal to average W in that slot. |
| `BatteryCapacityWh` | Accessible battery energy capacity in Wh; finite and positive. Convert Ah × nominal V to Wh before calling. |
| `InitialSoc` | Energy fraction in [0, 1] at the first selected interval's **start**. |

The dependency is `ShadedIrradianceSeries`: explicit `DateTimeOffset` start/end bounds, unique source interval IDs, nonnegative mean irradiance in **W/m²**, and an explicit study `TimeZoneId`. A timezone is timeline metadata, not a seventh energy-model setting. IANA IDs such as `Asia/Ho_Chi_Minh` and supported Windows IDs resolve through the operating system timezone database.

The series must be nonempty, ordered and exactly contiguous in UTC. Zero irradiance is valid, including night. Missing values, gaps, overlaps, duplicate IDs, negative/nonfinite values, zero-duration intervals and ambiguous timestamp text are errors. There is no interpolation, extrapolation, gap filling, or annual reset. Preserve any source labels and unclipped source bounds as optional provenance.

## Energy model

For each subinterval of elapsed duration Δt hours:

```text
PV energy = mean irradiance × panel area × panel efficiency × conversion efficiency × Δt
Load energy = hourly load slot × Δt
Balance = previous stored energy + PV energy − load energy
New stored energy = clamp(Balance, 0, battery capacity)
Unmet load = max(0, −Balance)
Curtailed energy = max(0, Balance − battery capacity)
End SoC (%) = 100 × new stored energy / battery capacity
```

PV supplies the load first; a surplus charges the battery and a deficit discharges it. Charge and discharge are ideal. The battery has no charge/discharge power limits, reserve floor, voltage curve, temperature dependence, ageing or self-discharge. Panel efficiency is fixed; the module adds no temperature, mismatch or spectral model. Conversion efficiency represents the PV supply path only; it does not reduce an existing battery's energy or multiply the load.

The calculation splits at source boundaries, local hour boundaries and timezone offset changes **before** updating the battery. This preserves saturation/depletion when subhour irradiance changes. Irradiance is constant within each provided interval; it cannot reconstruct unknown within-interval variability. Elapsed duration uses UTC. On DST days a skipped civil hour consumes nothing and a repeated hour repeats its load slot. Offsets distinguish repeated timestamps. Partial first/last hours, and fractional-hour offset transitions, remain explicitly marked output intervals.

Zero unmet load means that demand is met **over the supplied period under these assumptions**. It is not a guarantee against outages in future weather or at subinterval resolution. An exact end-of-interval SoC of zero can occur with no unmet load; use the unmet-energy diagnostics to identify actual simulated shortfalls.

## C# use

The core and file layer target .NET 8. The optional `PanelRun` adapter targets .NET 10 Windows x64, matching the shading library. No new production NuGet packages are introduced; the file layer reuses `ScientificWorkbook`.

```csharp
using SolarShade.PvBattery;
using SolarShade.PvBattery.Integration;
using SolarShade.PvBattery.IO;

var settings = new BatterySimulationSettings(
    PanelAreaM2: 2, PanelEfficiency: 0.20, ConversionEfficiency: 0.90,
    HourlyLoadWh: Enumerable.Repeat(25.0, 24).ToArray(),
    BatteryCapacityWh: 1000, InitialSoc: 0.5);

// panelRun is the existing final result from ShadingCorrectionModule.ApplyToPanel.
var result = PanelBatterySimulation.Compute(panelRun, settings, ct: cancellationToken);
foreach (var hour in result.Hours)
    Console.WriteLine($"{hour.End:O}: {hour.EndSocPercent:F2}%");

BatteryFiles.WriteCsv("battery-hourly.csv", result, cancellationToken);
BatteryFiles.WriteJson("battery-result.json", result, cancellationToken);
BatteryFiles.WriteXlsx("battery-hourly.xlsx", result, cancellationToken);

// Equivalent portable workbook path:
var irradiance = PanelWorkbookReader.Read("panel-shaded.xlsx", "Asia/Ho_Chi_Minh", cancellationToken);
var fromWorkbook = BatterySimulator.Compute(new(settings, irradiance), cancellationToken);
```

`PanelBatterySimulation.FromPanelRun` exposes the series adapter separately. Missing shaded totals fail; before-shading irradiance and the upper uncertainty bound are never substituted. `Compute` and all adapters support cancellation and throw on invalid input. No incomplete result is returned as success. Inputs and returned collections are immutable snapshots.

Workbook import accepts the existing `Panel results` sheet with `Shaded total on panel (W/m²)`, `Interval ID`, `Timestamp`, `Source start/end` and `Interval start/end` columns. It follows worksheet relationships and supports inline/shared strings. ISO timestamps require an offset. Numeric Excel dates, formulas/cached formula values and older exports without explicit bounds are rejected. The caller supplies the study timezone explicitly; source workbook metadata is retained for inspection and a SHA-256 file digest identifies the workbook.

## Results and reproducibility

- `Hours`: local start/end with offsets, partial-hour flag, start/end stored Wh, end SoC percent, generated/load/served/unmet/curtailed Wh.
- `Steps`: finer simulation intervals with source IDs, irradiance, average load and the same energy audit quantities.
- `Summary`: total energies, initial/final storage, minimum SoC (including the initial value and all substeps), count of output intervals containing shortfall, unmet-energy fraction (null for zero total load), and first shortfall **interval start**, not an interpolated outage instant.
- `Input`, `ModelVersion`, `Assumptions` and `InputFingerprint`: input snapshot and SHA-256 over model version and serialized inputs. Identical requests yield identical fingerprints. Different provenance, offsets or source transport can produce different fingerprints for numerically equivalent runs. Timezone rules come from the host; use the same timezone database/runtime for reproducible historical conversions.

CSV contains hourly values with invariant numeric formatting. XLSX contains hourly values, settings/summary/provenance and all 24 load slots. JSON is the full audit artifact, including original intervals, substeps and outputs. Each file is replaced atomically after successful writing; the three separate export calls are not a transaction. Cancellation or failure leaves that file's previous version intact. Excel permits 1,048,575 data rows per sheet; use JSON/CSV or split the study for longer workbook outputs. Calculation and exports retain the selected series and results in memory, with storage proportional to interval count.

## Offline checks

See the [implementation and validation record](VALIDATION.md) for verified results and scope.

From the repository root:

```powershell
dotnet test src/PvBatterySimulation/Tests -c Release
dotnet test src/PvBatterySimulation/IntegrationTests -c Release
dotnet run --project src/PvBatterySimulation/Validator -c Release -- --output artifacts/pv-battery
dotnet run --project src/PvBatterySimulation/Validator -c Release -- --request src/PvBatterySimulation/Fixtures/two-day-request.json --expected src/PvBatterySimulation/Fixtures/two-day-expected.json --output artifacts/pv-battery-json
dotnet run --project src/PvBatterySimulation/Validator -c Release -- --panel-xlsx "path/to/panel-shaded.xlsx" --settings src/PvBatterySimulation/Fixtures/example-settings.json --time-zone Asia/Ho_Chi_Minh --output artifacts/pv-battery-workbook
```

The bundled fixture has a 100 Wh battery initially full, 5 W constant load and 20 W PV from 06:00 to 18:00 for two days. The expected trace is hand calculated: first-day energy falls to 70 Wh at 06:00 and refills at 08:00; second-day energy falls to 40 Wh and refills at 10:00. Both days end at 70 Wh. Across 48 hours, generation is 480 Wh, load is 240 Wh, curtailment is 270 Wh and unmet load is zero. Every hourly storage value is frozen in `two-day-expected.json`.

The validator checks a separately written decimal balance recurrence against every substep, hourly aggregation, coverage and conservation; optionally it checks a frozen expected trace. The decimal oracle is intended for physical system magnitudes within the decimal numeric range. Its acceptance tolerance is 1e-7 Wh plus 1e-9 of the expected magnitude. Exit codes: 0 passed, 1 failed/invalid input/export error, 130 cancelled.

Tests additionally cover efficiency application, empty/full batteries, exact depletion, recovery, no daily reset, shortfall accounting, subhour saturation, leap years, minute cadence, DST (including Lord Howe's half-hour changes), UTC-offset equivalence, study-local load alignment, malformed inputs, immutable snapshots, file transport and the real shading exporter. These are software/numerical checks. Literature comparison, industrial-tool benchmarking and physical calibration were explicitly deferred for this implementation.
