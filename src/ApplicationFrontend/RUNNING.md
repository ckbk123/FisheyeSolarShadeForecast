# Running SolarShade

Extract the complete package, then launch `APPLICATION.exe`. Keep `Example`, `Data` and `Debug Data` alongside it. `Deliverable.zip` contains a clean first-run package. All four segmentation models are embedded; no Python, Excel installation or model download is required.

## Workflow

1. Select checkerboard photos, set inner-corner columns/rows and one square's side in millimetres, then **Calibrate**. The library saves native `calibration.yml`, reusable `camera-profile.json`, validation and solver diagnostics in `Debug Data/01-calibration`. A durable copy is retained in `Data/Profiles`; subsequent updates cannot delete the selected profile.
2. Select a sky photo and segmentation model. The physical lens setup and oriented image dimensions must match calibration. **Load profile** accepts the profile JSON or native OmniCalib YAML. YAML requires an explicit maximum incident half-angle based on calibration evidence; fitted coverage is provisional.
3. Set camera pose, site/elevation, time zone and dates. Dates include the complete end day. The editable example defaults are not GPS measurements.
4. Use NASA POWER/Open-Meteo or **Import XLSX / CSV**, then **Update results**. An import remains selected until **Use API**. Loading the example or editing inputs does not start calculations or generate debug files.
5. Adjust inputs freely, then click **Update results** again. The app reuses matching data, masks, solar geometry and visibility work. **Refresh data & update** explicitly refreshes provider data or rereads the selected import; it does not switch an import to API mode.
6. **Export results** is enabled only after a successful complete shaded calculation matching the current interface inputs. It copies every verified diagnostic file byte-for-byte, adds a one-page **Summary.pdf** and an integrity receipt (`export.json`), and publishes a new scenario folder at the chosen destination. The PDF uses settings from the successful update. Baseline-only or skipped-stage results cannot be exported.

The bottom-left indicator combines color and text: **red** means inputs need attention or an update failed; **yellow** means an update is needed, running, or stopped; **green** means complete results are up to date. Green appears after success, never merely after clicking Update. Invalid or missing inputs disable Update; a processing failure can be retried. Stop cancels publication, although active native work may need to finish before another update can start. An edit made during processing supersedes that result.

Typing a relevant new value immediately disables export, even before leaving the field. Previous graphs and summaries are dimmed and labeled when stale. Graph navigation, overlay visibility and future checkerboard setup do not invalidate calculated results. Changes in the automatic Windows time zone require another explicit update.

Edits remove affected diagnostic files and hide stale previews. Camera rotation preserves the mask and numerical solar positions but removes orientation, sun-path and shading files. Panel edits preserve camera previews and upstream files. Repeated edits do not regenerate diagnostics; even reverting to the original value requires Update to restore removed files. Selected source files are monitored and their contents rechecked on activation, Update and Export. Replacing a file under the same name cannot silently retain current results. If Excel holds an affected workbook open, close it and retry Update.

Debug Data holds one current set. Explicit exports are separate snapshots that later updates do not overwrite. Export validates source contents and artifact hashes before copying and before publication. A missing or locked file, a changed input, or PDF failure produces an error; any remaining interrupted temporary folder is clearly named `.solarshade-export-*.partial` and is never reported as complete. Close the affected file and retry. PDF generation is bundled with the application; no PDF printer is needed.

Both curves show irradiance received by the selected panel before/after obstruction shading, in W/m². Summary cards integrate the actual represented durations in kWh/m². Electrical PV power, ground reflection and rear-side irradiance are outside this calculation.

## PV Autonomy

The application has two independent tabs: **Solar Irradiance** and **PV Autonomy**. Complete an explicit irradiance update first. The PV tab unlocks only for a current, complete shaded dataset covering the selected study dates. If source inputs or files change, PV evaluation/export is blocked until the source is updated and the system is explicitly evaluated again.

In PV Autonomy, enter the 24 hourly consumption values on the left in **Wh per nominal hour**. The profile repeats by study-local hour; DST days can contain 23 or 25 actual hours. Paste exactly 24 tab/newline-separated values or fill every slot with a constant. Blank/invalid inputs remain visible and block calculation. **Example system** deliberately loads illustrative values; it does not calculate.

Enter panel area (m²), panel efficiency (%), conversion efficiency (%), battery capacity (Wh) and initial charge (%). Initial charge applies at the start of the entire study. The existing backend computes the full interval; navigation does not reset charge. Battery charging/discharging is ideal, all supplied capacity is accessible, and the model omits power limits, ageing and temperature effects.

Click **Evaluate system**. The shared chart shows load demand and available PV energy on the same Wh axis, with end-of-hour battery charge on the fixed 0–100% axis. Available PV is after panel/conversion efficiencies and before curtailment. Red marks identify intervals containing unmet load, even when their end charge recovered. Hover or open **Hourly values** for explicit interval bounds, offsets, partial-hour flags and values. **Model & totals** shows full energy totals, assumptions and the first shortfall interval. “No unmet load during this study” does not guarantee supply in unseen weather.

**Export PV results** copies verified CSV, JSON and XLSX files plus a scoped manifest to a new scenario folder. JSON includes the full irradiance input, system settings and substeps. The existing irradiance export and PDF remain independent. PV settings persist in `Data/pv-settings.json`; restart restores drafts, not accepted results. Optional managed outputs occupy `Debug Data/07-battery` under the same storage lease as irradiance; altering them blocks PV export without damaging the irradiance study.

## Cardinal-direction preview

**Show cardinal directions** places magenta N/E/S/W labels and short ticks over the original colour photograph. The shading library projects the four constant compass bearings through the same calibration and camera pose used for sun paths and shading. The ticks identify the visible ends of these bearing lines, not necessarily the horizon: the horizon can be outside the calibrated lens coverage. Labels remain upright.

The preview depends on image geometry, effective calibration, heading, tilt and roll. Relevant edits clear stale markers; Update results generates their replacement. During an update the preview is available before irradiance retrieval. Weather, dates, time zone, panel settings and the visibility toggle do not regenerate its geometry. It cannot establish whether the entered camera heading matches the real photograph.

At the default upward pose with zero roll and image top facing North, the shared projection places N at the top, E on the left, S at the bottom and W on the right. A roll rotates this arrangement; it does not mirror it. No independent E/W swap is applied to the compass.

`02-orientation` contains the exact transparent overlay PNG, a compact marker-coordinate XLSX and metadata generated by the library. Missing calibration or image leaves the overlay unavailable. The source photograph and segmentation mask are not modified.

## Sun-path preview

**Show sun path** places semi-transparent yellow daily tracks over the black-and-white mask for the studied period. It uses the same solar positions, camera pose and calibrated projection as shading. Tracks also appear over obstructions so you can assess the alignment. Positions outside the calibrated view are omitted, and unrelated day/night segments are never joined.

The toggle changes only visibility. Panel and diffuse-model edits reuse the overlay; camera pose, calibration, image geometry and solar timeline changes update it. Invalid camera/site/date entries hide the previous overlay until corrected. A compatible calibration and sky photo are required.

The library saves the transparent PNG, compact projected-vertex XLSX and metadata in `04-solar-positions`. The binary mask remains unchanged. Full-year 3000 × 4000 overlay generation was benchmarked below one second using prepared solar geometry for both hourly and 15-minute sources; see `docs/SUN_PATH_OVERLAY.md` for measurement scope and results.

## Source intervals and imports

The irradiance dataset owns the time series. Hourly source means produce hourly result intervals; 15-minute means produce 15-minute results. Explicit variable durations are supported by the prepared pipeline. Missing rows remain missing and requested coverage gaps fail visibly. The pipeline does not fill, interpolate or resample weather data. Its internal solar samples are numerical integration points, not additional irradiance observations.

The current NASA POWER and Open-Meteo endpoints used by the application request hourly means. The returned result declares its native cadence and label convention. The application does not infer a finer provider resolution from the solar integration setting.

A legacy XLSX first worksheet or CSV needs a header followed by timestamp, direct horizontal irradiance (BHI), and diffuse horizontal irradiance (DHI), in W/m². Choose **Import interval, minutes** and whether labels describe the start, end or centre of each interval. These explicit fallback settings apply only when the file lacks native interval metadata. A native exported irradiance workbook carries stable interval IDs, source/selected bounds, label convention, value kind, cadence, units and provenance; preserve those columns. Recorded location metadata must agree with the selected solar site.

Only nonnegative interval-mean BHI/DHI are accepted. Do not substitute DNI, panel/shaded outputs or accumulated energy. Formula cells must be exported as materialized values. CSV supports comma/semicolon separators and decimal-point numbers. ISO timestamps with offsets retain their exact instants. For legacy files, Excel dates and DD.MM.YYYY HH:mm[:ss] use the selected time zone; ambiguous/missing daylight-saving times require an explicit offset. Native interval metadata requires offset-qualified instants.

Include enough source intervals to cover both selected date boundaries. For example, an end-labelled series needs the following midnight row to represent the final interval of a day. Retrieval pads the requested dates in the library. Boundary clipping retains the complete source interval for DNI inference and separately records the selected interval used for integration and energy.

## Time zone and chart

**Use Windows time zone (automatic)** follows Windows at startup and when its setting changes. Turn it off to choose a named region or fixed UTC offset. Named regions follow historical daylight saving; fixed offsets remain constant. Loading the example preserves this preference.

The chart holds each interval mean over its actual start/end bounds, including the final interval. Zoom, pan, day view and component selection change presentation only. Hover shows the represented interval and returned component values. The date/time axis uses elapsed time, preserving 23/25-hour daylight-saving days. Workbook source timestamps retain their original labels/offsets; the chart uses the selected display zone.

## Automatic Debug Data

There is one current dataset directly under `Debug Data`, with the stage folders below and one `run.json`. Update replaces only invalid artifacts; unchanged files retain their bytes and modification times. Orientation is prepared within the explicit calculation. Calibrate publishes into `01-calibration` and saves durable copies under `Data/Profiles`. No Export click is needed to save diagnostics.

Edits invalidate affected current files. Verified historical application run folders are removed after selected inputs are copied to `Data/Inputs` and their saved paths updated. User exports, bundled examples and unrecognized folders/files are preserved. Keep Data with the application: it now includes durable calibration/input files as well as disposable caches.

Updates prepare files in the bounded sibling `Debug Data.staging` directory and remove it when finished. A held `Debug Data.lock` file prevents concurrent instances from changing the same dataset; a lock file left on disk after exit is normal. Close the other application instance if this dataset is already in use. A stopped or interrupted update remains incomplete and can be retried. Close locked diagnostic workbooks before retrying a failed write.

| Stage folder | Actual library output |
|---|---|
| `01-calibration` | `calibration.yml`, `camera-profile.json`; calibration-only runs also include observations, solution, solver diagnostics, validation and existing debug overlays/CSVs. |
| `02-sky-mask` | Exact black-and-white `sky-mask.png` and `mask-details.json`. |
| `02-orientation` | `cardinal-directions-overlay.png`, `cardinal-directions.xlsx` and `cardinal-directions-details.json`, generated by the shading library for the original-photo preview. |
| `03-irradiance` | `horizontal-irradiance.xlsx`, the complete returned/imported dataset with authoritative interval metadata before selection. |
| `04-solar-positions` | `solar-positions.xlsx`, one position at each selected source label, plus `solar-integration-samples*.xlsx` for full-source and clipped integration geometry. |
| `05-transposition` | `panel-unshaded.xlsx`, with main interval results and component-integration sheets, split into additional sheets when needed. |
| `06-shading` | `panel-shaded.xlsx`, `shading-correction-factors.xlsx`, `shading-visibility*.xlsx`, and `panel-results.json` containing interval results and summaries. |

Native calibration and masking use YAML/PNG; generic flattened calibration/mask workbooks are no longer the normal output. Solar and shading detail files may have numbered parts; inspect all parts. Transmission means shaded/unshaded: 1 is unchanged, 0 fully blocked, and a blank means an undefined zero-baseline ratio. The main shaded result treats unobserved sky as blocked; the upper bound is retained in the results.

`run.json` records settings, input fingerprints, software/library versions, stage origins, artifact hashes, update identity and the last successful calculation time. Status can be Running, Complete, Partial, Stale, Cancelled, Failed or Interrupted. Unavailable stages are Skipped. Calibration/orientation alone produce a Partial dataset. A required write failure prevents completion; valid independent files remain available when later work fails. Old result objects cannot export a newer dataset just because it uses the same path.

Low-level scalar/per-pixel functions remain in memory. Library stage wrappers/exporters own the scientific file schemas and write the returned values. The frontend sequences those operations and records their artifacts. Manual Export copies completed artifacts without recalculation.

## Library architecture

Scientific code is in the existing libraries, documented in [APPLICATION_ARCHITECTURE.md](../../docs/APPLICATION_ARCHITECTURE.md). `ApplicationFrontend` has no `PanelEngine`, DNI inference, sky integration, shading formula, energy/loss derivation or scientific workbook writer.

The transposition library uses constant daylight DNI inferred over the whole source interval and constant source DHI; only selected bounds contribute to requested-period results. Its Hay–Davies/isotropic decomposition supplies separate components for correction. Receiver-plane visibility applies at each solar substep before temporal averaging. No new Perez obstruction model is introduced.

A compatible library change needs no frontend source rewrite. The self-contained executable must still be rebuilt/published to include changed library code. This is not hot swapping embedded DLLs.

## Example and portable files

- `Example/Calib Images`: twelve original checkerboard photographs and saved calibration YAML.
- `Example/Sky Photo`: demonstration sky photograph.
- `Example/Irradiance/horizontal-irradiance.xlsx`: original buffered hourly Open-Meteo input workbook.
- `Example/Debug Data/reference-run`: fixed native library outputs from the verified example run, with its own `run.json`.
- `Example/settings.json`: executable-relative default input paths. First launch and **Load example** load the inputs without calculating; click **Update results** when ready.
- `Data`: settings, durable Profiles/Inputs, weather/mask/current-value caches and extracted model weights.
- Top-level `Debug Data`: the one current set of published diagnostic files.

The example settings illustrate a workflow; they do not establish the true site/pose of the photograph. Read the reference run's settings and results for its exact parameters and totals. Source input data is separate from calculated Debug Data. The bundled reference output is fixed; user updates affect only the top-level current dataset.

Use a writable extracted folder. Package-relative input/profile settings continue to work when the entire folder is moved; external personal input files do not move automatically. Keep Data/Profiles and Data/Inputs when moving or backing up the application. Only the selected model is extracted into `Data/models-v1` on first use. Native DLLs use the .NET bundle extraction cache. `Packaging/Publish.ps1` records package files/sizes/hashes in `artifacts/application-package.json` and creates clean example settings for the ZIP.

## Build and verify

```powershell
dotnet restore src/ApplicationFrontend/Tests/ApplicationFrontend.Tests.csproj --configfile src/ApplicationFrontend/NuGet.Config -p:NuGetAudit=false
dotnet test SolarShade.sln -c Release --no-restore
./src/ApplicationFrontend/Packaging/Publish.ps1
./Deliverable/APPLICATION.exe --smoke-test artifacts/application-validation/new-run
```

Use the executable location returned by the publish script if a different output folder was selected. Native packaging inputs are under `Packaging/Native`; `BundleModels=true` embeds the existing model files during publication.

Library tests cover independent calibration/solar/transposition references, native artifact round trips, hourly/15-minute/variable intervals, clipping/gaps, mask/receiver extremes and cancellation. Frontend tests verify direct-library agreement, returned summary binding, cache reuse/invalidation, stage manifests and failed output handling. Packaged smoke tests additionally exercise the actual WPF window, real inference/calibration, manual updates, Stop/retry, byte-identical exports with one-page PDFs, calibration, restart and panel edits. Historical pre-rewrite counts/timings are not evidence for the new package; use the current test/smoke artifacts recorded at delivery.

Optional test variables: `SOLARSHADE_FORCE_CPU=1`, `SOLARSHADE_CALIBRATION_IMAGES=<directory>`, `SOLARSHADE_TEST_LIVE=1`, `SOLARSHADE_DATA_DIR`, and `SOLARSHADE_DEBUG_DIR`. `--portable-smoke-test <output>` uses settings next to its executable and should run on an isolated extracted package.

The existing calibration solver and model inference do not interrupt mid-native-call. Cancellation is checked around those calls and during artifact publication. Scientific field accuracy and clean older-PC compatibility require independent validation beyond these software checks.
