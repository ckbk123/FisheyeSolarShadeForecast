# Running SolarShade

Launch `Deliverable/APPLICATION.exe` or extract the clean Preview 0.2.0 `Deliverable.zip` first. Keep `Example`, `Data` and `Debug Data` alongside the executable. All four segmentation models are embedded; no Python, Excel installation or model download is required.

## Workflow

1. Select checkerboard photos, set inner-corner columns/rows and one square's side in millimetres, then **Calibrate**. The checkerboard review lists detected/failed photos and shows the selected photo with its detected inner corners marked. It stays separate from the main study so both windows remain usable. **Review checkerboard photos** reopens it. The library saves native `calibration.yml`, reusable `camera-profile.json`, per-photo `checkerboard-detections.json`/`detections` previews, validation and solver diagnostics in `Debug Data/01-calibration`. A durable copy is retained in `Data/Profiles`; subsequent updates cannot delete the selected profile. Failed calibrations retain their photo feedback under `Data/CalibrationReviews` without publishing a camera profile. The review shows the saved location and can open its diagnostics folder. Older saved profiles can show their existing observed/fitted corner overlays; failed-photo counts from those older runs are unavailable.
2. Use **Choose sky photo…** to select a photograph directly, or select its folder and active photo. Choose a segmentation model, or use **Import existing mask…** for an already segmented PNG without AI. The physical lens setup and oriented image dimensions must match calibration. **Load camera profile…** selects `camera-profile.json`, which includes the camera parameters and validated incident-angle coverage. Calibrate saves this reusable file in `Data/Profiles`. For a native OmniCalib YAML calibration, select its matching `camera-profile.json`; fitted coverage is provisional. Existing saved YAML selections remain supported with their saved coverage.
3. Set camera pose, site/elevation, time zone and dates. Dates include the complete end day. The editable example defaults are not GPS measurements.
4. Choose NASA POWER, Open-Meteo ERA5 or **Import XLSX / CSV** in **Weather data source**, then **Update results**. Imported files are shown by name; choosing a provider leaves import mode. Loading the example or editing inputs does not start calculations or generate debug files.
5. Adjust inputs freely, then click **Update results** again. The app reuses matching weather, masks, solar geometry and visibility work and rereads changed imports. **Download weather again & update** bypasses the provider weather cache while reusing unchanged camera and mask work. It is unavailable for imported files; use **Update results** to reread those. The source controls show the last result's recorded cache/source origin and dataset timestamp. Legacy imports record their read time, which is not a remote weather retrieval timestamp.
6. **Export data** is enabled only after a successful complete shaded calculation matching the current interface inputs. It opens an export dialog with **Irradiance (.xlsx)** checked. Choose a destination folder, then click **Export** to create a new scenario folder containing **irradiance.xlsx** and an integrity receipt (`export.json`). Clearing the checkbox disables Export; Cancel closes the dialog without creating files. Baseline-only or skipped-stage results cannot be exported.

The irradiance workbook contains every result interval, in order, with exactly six columns: timeseries, unshaded direct irradiance on the panel, unshaded diffuse irradiance on the panel, direct shading factor, diffuse shading factor, and final shaded total irradiance. Irradiance is in W/m². The diffuse header identifies **Isotropic** or **Hay–Davies** from the completed calculation. Factors are shaded ÷ unshaded irradiance (1 = open, 0 = blocked), with blank cells when the corresponding unshaded component is zero. The Hay–Davies diffuse factor includes both isotropic and circumsolar shading. Timestamps retain their source labels and UTC offsets; the workbook metadata records the time zone, timestamp convention and study period. Export preserves the original interval cadence and numeric precision without recalculation or graph filtering.

The bottom-left indicator combines color and text: **red** means inputs need attention or an update failed; **yellow** means an update is needed, running, or stopped; **green** means complete results are up to date. Green appears after success, never merely after clicking Update. Invalid or missing inputs disable Update; a processing failure can be retried. Stop cancels publication, although active native work may need to finish before another update can start. An edit made during processing supersedes that result.

Typing a relevant new value immediately disables export, even before leaving the field. Previous graphs and summaries are dimmed and labeled when stale. Graph navigation, overlay visibility and future checkerboard setup do not invalidate calculated results. Changes in the automatic Windows time zone require another explicit update.

Edits remove affected diagnostic files and hide stale previews. Camera rotation preserves the mask and numerical solar positions but removes orientation, sun-path and shading files. Panel edits preserve camera previews and upstream files. Repeated edits do not regenerate diagnostics. Returning to an exact accepted photo, mask and input combination can restore its verified result without another calculation; other combinations require Update. Selected source files are monitored and their contents rechecked on activation, Update and Export. Replacing a file under the same name cannot silently retain current results. If Excel holds an affected workbook open, close it and retry Update.

Debug Data holds one current set, including the full scientific diagnostics. Explicit exports are separate snapshots that later updates do not overwrite. Export validates source contents and artifact hashes before reading the saved result and before publication. A missing or locked file, a changed input, or workbook write failure produces an error; any remaining interrupted temporary folder is clearly named `.solarshade-export-*.partial` and is never reported as complete. Close the affected file and retry. The developer diagnostic snapshot API still supports copying the full debug set and its one-page PDF; the Export data dialog currently offers the irradiance workbook only.

Both curves show irradiance received by the selected panel before/after obstruction shading, in W/m². Summary cards integrate the actual represented durations in kWh/m². Electrical PV power, ground reflection and rear-side irradiance are outside this calculation.

The input workflow is grouped under **01 Images & Calibration**, **02 Camera Pose**, **03 Site Location**, **04 Historical Data**, and **05 Panel Installation**. Site Location contains the coordinates, elevation and time zone. Historical Data contains the study dates, weather source/import controls, and **Use isotropic diffuse instead of Hay–Davies**. The other Advanced settings controls are no longer shown; existing saved calculation settings are retained. All five sections, including panel tilt and azimuth, belong to one scrolling input list. The highlighted **UPDATE RESULTS** block stays fixed at the bottom left while inputs or results scroll. Its bold status and **Update results**, **Stop**, and **Download weather again & update** buttons keep the calculation controls easy to find.

## PV Autonomy

On small displays, workspace scrollbars keep graphs, photo previews and controls reachable. The input list and daily consumption list also have their own scrollbars. Moving keyboard focus to an input brings it into view.

Help opens a separate reference window, so you can keep reading while editing the study or switching workspaces. Clicking Help again brings back the existing guide. Close it with its title-bar button or Escape.

The application has two independent tabs: **Solar Irradiance** and **PV Autonomy**. Complete an explicit irradiance update first. The PV tab unlocks only for a current, complete shaded dataset covering the selected study dates. If source inputs or files change, PV evaluation/export is blocked until the source is updated. A previously accepted PV result can return when the exact irradiance source and PV settings match; other combinations require **Evaluate system**.

In PV Autonomy, enter the 24 hourly consumption values on the left in **Wh per nominal hour**. The profile repeats by study-local hour; DST days can contain 23 or 25 actual hours. Paste exactly 24 tab/newline-separated values or fill every slot with a constant. Blank/invalid inputs remain visible and block calculation. **Example system** deliberately loads illustrative values; it does not calculate.

Enter panel area (m²), panel efficiency (%), conversion efficiency (%), battery capacity (Wh) and initial charge (%). Initial charge applies at the start of the entire study. The existing backend computes the full interval; navigation does not reset charge. Battery charging/discharging is ideal, all supplied capacity is accessible, and the model omits power limits, ageing and temperature effects.

Click **Evaluate system**. The shared chart shows load demand and available PV energy on the same Wh axis, with end-of-hour battery charge on the fixed 0–100% axis. Available PV is after panel/conversion efficiencies and before curtailment. Red marks identify intervals containing unmet load, even when their end charge recovered. Hover or open **Hourly values** for explicit interval bounds, offsets, partial-hour flags and values. **Model & totals** shows full energy totals, assumptions and the first shortfall interval. “No unmet load during this study” does not guarantee supply in unseen weather.

**Export PV results** copies verified CSV, JSON and XLSX files plus a scoped manifest to a new scenario folder. JSON includes the full irradiance input, system settings and substeps. Irradiance and PV exports remain independent. PV settings persist in `Data/pv-settings.json`; a matching accepted PV result can also be restored from `Data/AcceptedResults`. Optional managed outputs occupy `Debug Data/07-battery` under the same storage lease as irradiance; altering them blocks PV export without damaging the irradiance study.

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

### Editing and choosing a sky mask

**Import existing mask…** accepts an opaque, 8-bit black-and-white PNG at the oriented photo's exact pixel dimensions. White means open sky and black means obstruction by default; choose the explicit inversion option when the source uses the opposite meaning. Gray edges and colored pixels are rejected. The lens disk is detected from the photograph, never from the mask's white region. Choose **Centered full disk** only for a centered full-disk photograph. Pixels outside that disk become black. Import saves a named immutable copy and selects it in **Mask for calculation** without loading a model or running AI; the external import file is no longer required. AI model, resolution and disk controls are unavailable while an imported mask is selected. Select **AI mask** to use segmentation again.

After importing a mask, or after **Update results** has produced an AI mask, click **Edit mask** at the lower corner of its preview. A separate modal window keeps the main study controls fixed while you paint. Choose **Black · obstruction** or **White · open sky**, set brush diameter in original-image pixels, and use the mask-opacity slider to see the photo through the mask while painting. The zoom slider, mouse wheel, Fit image, right-drag pan, undo/redo and Reset affect the editor view or strokes only. The saved PNG remains exactly black/white at the original image dimensions; painting outside the detected lens disk is blocked.

**Save as new mask** creates a named, immutable variant. **Cancel** leaves the current calculation unchanged. The **Mask for calculation** dropdown below the preview selects the AI mask, an imported mask or a compatible saved edit. Each photo remembers its last selection. An imported mask can be edited immediately, without an AI ancestor. `Data/Masks/<photo-hash>/Imported` retains normalized imported source pixels; `Data/Masks/<photo-hash>/AI` retains original AI source pixels even if ordinary compute caches are cleaned. Saved variants and their labels are under `Data/Masks/<photo-hash>`. A new selection needs **Update results** unless an exact prior accepted result is available; then the app verifies and restores it without recalculation. Matching PV results also return when their settings match.

`02-sky-mask/mask-provenance.json` identifies the active mask as AI, imported or edited, its source ancestor hash, optional variant/parent IDs and active pixel hash. Imported lineage also records the original filename and file hash, explicit inversion and normalized source hash; it has no AI ancestor. Scenario exports include that file and the exact selected `sky-mask.png`. `Data/AcceptedResults` retains hash-verified complete studies and shared artifact blobs so photo/mask switch-back can become current after a restart. Keep both `Data/Masks` and `Data/AcceptedResults` when moving or backing up the portable application. **Saved studies** in the main toolbar shows storage use and offers deliberate cleanup; it preserves the current result and every saved mask.

There is one current dataset directly under `Debug Data`, with the stage folders below and one `run.json`. Update replaces only invalid artifacts; unchanged files retain their bytes and modification times. Orientation is prepared within the explicit calculation. Calibrate publishes into `01-calibration` and saves durable copies under `Data/Profiles`. No Export click is needed to save diagnostics.

Edits invalidate affected current files. Selected inputs under managed diagnostics are copied to `Data/Inputs` and their saved paths updated before an update. Historical run folders are retained during normal startup, editing and calculation; explicit maintenance can clean verified old runs while preserving read-only, inaccessible or unrecognized content. Keep Data with the application: it includes durable calibration/input files as well as disposable caches.

Updates prepare files in the bounded sibling `Debug Data.staging` directory and remove it when finished. A held `Debug Data.lock` file prevents concurrent instances from changing the same dataset; a lock file left on disk after exit is normal. Close the other application instance if this dataset is already in use. A stopped or interrupted update remains incomplete and can be retried. Close locked diagnostic workbooks before retrying a failed write.

| Stage folder | Actual library output |
|---|---|
| `01-calibration` | `calibration.yml`, `camera-profile.json`; calibration-only runs also include observations, solution, solver diagnostics, validation and existing debug overlays/CSVs. |
| `02-sky-mask` | Exact black-and-white `sky-mask.png`, `mask-details.json` and `mask-provenance.json`. |
| `02-orientation` | `cardinal-directions-overlay.png`, `cardinal-directions.xlsx` and `cardinal-directions-details.json`, generated by the shading library for the original-photo preview. |
| `03-irradiance` | `horizontal-irradiance.xlsx`, the complete returned/imported dataset with authoritative interval metadata before selection. |
| `04-solar-positions` | `solar-positions.xlsx`, one position at each selected source label, plus `solar-integration-samples*.xlsx` for full-source and clipped integration geometry. |
| `05-transposition` | `panel-unshaded.xlsx`, with main interval results and component-integration sheets, split into additional sheets when needed. |
| `06-shading` | `panel-shaded.xlsx`, `shading-correction-factors.xlsx`, `shading-visibility*.xlsx`, and `panel-results.json` containing interval results and summaries. |

Native calibration and masking use YAML/PNG; generic flattened calibration/mask workbooks are no longer the normal output. Solar and shading detail files may have numbered parts; inspect all parts. Transmission means shaded/unshaded: 1 is unchanged, 0 fully blocked, and a blank means an undefined zero-baseline ratio. The main shaded result treats unobserved sky as blocked; the upper bound is retained in the results.

`run.json` records settings, input fingerprints, software/library versions, stage origins, artifact hashes, update identity and the last successful calculation time. Status can be Running, Complete, Partial, Stale, Cancelled, Failed or Interrupted. Unavailable stages are Skipped. Calibration/orientation alone produce a Partial dataset. A required write failure prevents completion; valid independent files remain available when later work fails. Old result objects cannot export a newer dataset just because it uses the same path.

Low-level scalar/per-pixel functions remain in memory. Library stage wrappers/exporters own the scientific file schemas and write the returned values. The frontend sequences those operations and records their artifacts. Export data assembles the requested columns from the verified saved panel result without recalculation. The diagnostic snapshot API copies completed artifacts unchanged.

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
- `Data`: settings, durable Profiles/Inputs/Masks, accepted-result vault, weather/current-value caches and extracted model weights.
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
