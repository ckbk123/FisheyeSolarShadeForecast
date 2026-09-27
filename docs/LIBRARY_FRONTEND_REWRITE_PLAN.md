# Library-owned calculation and Debug Data rewrite

Status: approved plan, 15 September 2026. The library extraction and frontend integration are implemented; final package verification is tracked separately. This document records the agreed requirements. See [current application architecture](APPLICATION_ARCHITECTURE.md) for implemented APIs and ownership.

## Objective and scope

ApplicationFrontend handles user input, UI state, stage sequencing, progress, cancellation, cache coordination and rendering. Every scientific calculation, derived result and scientific file schema belongs to an existing library. A calculation change inside a library must reach the application without a frontend source change when the public interface stays compatible.

Keep the existing UI, libraries, calibration method, segmentation models, providers and packaging. Add narrow APIs where required, and consolidate duplicated calculation paths. Do not move PanelEngine wholesale into a new catch-all library or replace the existing libraries with a new engine.

This is primarily extraction and integration, but inclined-panel shading requires an additive library capability. The current horizontal shading-correction API cannot substitute for it unchanged.

## Authoritative time series: irradiance comes first

User clarification: the irradiance service determines the available time resolution and supplies the authoritative time series. Hourly source data produces hourly result intervals; 15-minute source data produces 15-minute result intervals. Solar, transposition and shading must consume that series, not construct independent output grids. This is a prerequisite for the stage interfaces, not a later optimization.

The irradiance library's returned dataset must contain:

- Original source timestamps and a stable interval identifier.
- Explicit interval start/end instants, with timestamp-label convention (start, end or center) and timezone metadata.
- Irradiance components and units, and whether values represent interval means, accumulated energy or instantaneous samples. The downstream interval-mean pipeline must not silently treat different value kinds as interchangeable.
- Native cadence when regular, plus explicit row bounds as the authority; source/provenance and missing-data information.

Provider adapters derive interval semantics from the returned metadata and documented request/response convention. Cadence must not be guessed solely from adjacent timestamps: a missing 15-minute row is a gap, not proof of a 30-minute measurement. Ambiguous imports need explicit interval metadata. Do not interpolate, fill gaps, aggregate or upsample without a separate user instruction. Preserve the existing missing-coverage error behavior and report the actual gap.

Every downstream main result and debug workbook preserves the source interval identifier, label and represented bounds for the selected source rows. Any clipping at a requested date boundary is explicit. Internal numerical integration can evaluate solar positions within a source interval, but it creates no new irradiance observations and does not change the output cadence; record the within-interval assumptions and keep these samples in a separate diagnostic sheet/file. Compute energy using each actual interval duration, not a constant one-hour multiplier.

Calibration and masking can run or be reused independently. The time-dependent dependency order is irradiance dataset -> solar timeline for that dataset -> transposition -> shading correction. Neither the frontend nor the solar library invents the master weather time series.

Current hourly assumptions to remove or generalize include the frontend's sub-hour import rejection, provider-specific forced 60-minute selection/validation where other intervals are supported, and `FollowingHour`/`PrecedingHour` selection solely by provider identity. Existing hourly APIs remain compatibility conveniences, not the new stage contract. Do not claim finer data is available from a service when its selected endpoint only provides hourly data.

## Target stage ownership

| Stage | Existing owner | Required library responsibility | Canonical Debug Data output |
|---|---|---|---|
| Calibration | SolarShade.Calibration.Solver, with existing validation/camera helpers | Detect and solve; provide effective camera profile; write native calibration and existing diagnostics. Keep validation independent of the numerical solver. | `01-calibration/calibration.yml`, profile/validation metadata and existing diagnostics |
| Sky masking | SkyPhotoMasking | Inference, thresholding, lens disk, oriented coordinates; add export of the returned mask. | `02-sky-mask/sky-mask.png`, small metadata file |
| Irradiance retrieval/import | SolarShade.Irradiance | Own the authoritative source time series, cadence and per-row interval semantics; provider retrieval, workbook import, validation/coverage and export of the returned dataset. | `03-irradiance/horizontal-irradiance.xlsx` with interval metadata |
| Solar positions | SolarShade.Shading solar-position portion | Consume the irradiance dataset's intervals; prepare corresponding solar instants, source/effective bounds, geometric/apparent angle conventions and directions. | `04-solar-positions/solar-positions.xlsx`, integration-sample detail |
| Transposition | SolarShade.Irradiance.Transposition | Convert horizontal irradiance to inclined-panel irradiance; own DNI inference, component breakdown and unshaded interval integration. | `05-transposition/panel-unshaded.xlsx`, substep/component diagnostics |
| Shading correction | SolarShade.Shading for visibility; SolarShade.ShadingCorrection for application of losses | Receiver-plane disk/dome visibility; apply visibility to transposed components at each substep; integrate shaded results, bounds, ratios, energy and loss summaries. | `06-shading/shading-visibility.xlsx`, `shading-correction-factors.xlsx`, `panel-shaded.xlsx` |

Use native outputs: YAML for calibration and a black-and-white PNG for masking. The recently added flattened calibration/mask Excel files are not the default replacement for those formats. Numerical stages retain their established workbook schemas; add detail sheets or companions without breaking existing input formats.

## Library API contract

Each application-facing stage operation accepts typed inputs, an explicit debug destination, and cancellation where supported. It returns an immutable typed result, status/provenance and the paths of the artifacts it actually wrote. Required artifacts must be successfully saved before the stage reports completion.

- Reuse existing `Calibrate`, `PullDetailedAsync`, `ComputeToWorkbook` and exporters where their semantics fit.
- Preserve existing public computational APIs. Add compatible stage wrappers/export methods; do not make every low-level scalar or per-pixel call write a file.
- Export the same returned values that downstream stages consume. Do not run a separate calculation to populate a workbook.
- The frontend supplies the destination and consumes the result. It does not define scientific XLSX columns, serialize scientific objects, or reconstruct stage values.
- Manual Export calls the owning library exporters or copies completed stage artifacts.

Before parallel implementation, agree on the irradiance-owned interval contract, then the prepared-solar contract: source interval identifier and label, complete source bounds, selected/clipped bounds, substep timestamps, angle conventions and quadrature information. Carry substep component contributions through correction; do not reduce everything to source-interval averages too early.

Keep dependencies acyclic. Transposition remains independent of masks and shading correction. Shading geometry does not own transposition formulas. Shading correction can reference transposition and shading. Retain .NET 8 library compatibility: use the existing portable DTO/callback approach for exchanges with the .NET 10 solar/shading project, without moving those contracts into Windows-only Core or forcing a framework upgrade.

## Important mathematical preservation rules

1. Existing `ShadingCorrectionModule.Apply` is horizontal-only. Keep it compatible and add an explicit panel operation such as `ApplyToPanel`.
2. Consolidate DNI inference and diffuse decomposition in the existing transposition kernel. Expose its direct, isotropic and circumsolar contributions for panel correction.
3. Infer DNI from the whole source interval, then integrate the selected clipped interval. Preserve preceding/following/centered labels, offsets, DST and boundary coverage.
4. Prepare solar geometry for diffuse-only and nighttime rows as needed. Existing solar `Prepare` skips zero-direct rows, so it needs an additive general interval API.
5. Apply visibility to each substep's irradiance before averaging. Multiplying two independently averaged quantities can change the answer.
6. Name `Transmission` and `LossFraction` explicitly. Existing horizontal factors use 0=open/1=blocked; current panel exports use the reverse transmission convention. Undefined zero-baseline ratios remain blank.
7. Preserve observed/unobserved coverage, uncertainty bounds, camera conventions and horizontal-identity behavior. Keep existing Hay–Davies/isotropic panel-shading scope; this rewrite does not add Perez shading or ground reflection.

## Frontend removal list

- Remove `PanelEngine` and `SkyScene` implementations from ApplicationFrontend after their responsibilities are consolidated into the owning libraries.
- Move `PanelRow`/`PanelRun` scientific totals, interval durations and energy summaries into library result contracts.
- Remove `AppServices.ShadingFactor` and the energy-loss formula in MainWindow. Bind directly to returned summaries.
- Move `AppData.Select`, scientific date-boundary/interval interpretation and `WorkbookIo` scientific import/export behavior to library APIs.
- Move image-bottom-to-camera-heading conversion and effective profile/coverage handling to camera/shading helpers.
- Replace `DebugDataRun`'s scientific export code with run/stage bookkeeping and artifact-path collection.
- Keep AppServices as a thin sequencer. UI layout, preview sizing, axis coordinates and number formatting remain presentation logic.

## Debug Data lifecycle

```text
Debug Data/
  calculation-<timestamp>-<id>/
    run.json
    01-calibration/calibration.yml
    02-sky-mask/sky-mask.png
    03-irradiance/horizontal-irradiance.xlsx
    04-solar-positions/solar-positions.xlsx
    05-transposition/panel-unshaded.xlsx
    06-shading/shading-visibility.xlsx
    06-shading/shading-correction-factors.xlsx
    06-shading/panel-shaded.xlsx
```

Calibration-only calls use a calibration run folder and the same library exporter. When a stored calibration is loaded, record that origin and export the effective profile, including any coverage override in metadata; do not claim it was recalibrated.

The app owns a manifest containing input identifiers, stage/library versions, stage status, relative artifact paths and errors. Each stage is Computed, Imported, Reused or Skipped, with Running/Complete/Cancelled/Failed run status. Cached stages still place their actual artifacts in the current run via the library exporter or an exact copy. Do not fetch or solve again solely to create a debug file.

Write artifacts atomically; preserve completed stages when later work fails. Missing mask/calibration means shading is explicitly unavailable. A debug write failure prevents that stage being marked complete. Retain earlier runs. The original Example inputs remain separate from current-run outputs.

## Implementation sequence and subagent assignments

### Phase 1 — agree contracts and capture baseline

Lead agent and Agent B first freeze the irradiance dataset/time-series contract and cover both hourly and 15-minute fixtures. Solar/transposition/correction contracts depend on it. Then freeze stage ownership, prepared-interval/result contracts, debug names and dependency direction before parallel edits. Capture deterministic current results and run existing independent reference tests. Current application snapshots are regression evidence, not the sole correctness oracle.

### Phase 2 — library work in parallel

- **Agent A: calibration, masking and solar geometry.** Route native calibration artifacts correctly; add mask persistence; extend solar preparation/export; move camera convention handling; own the corresponding library tests.
- **Agent B: irradiance and transposition.** Establish source cadence and explicit interval semantics first; remove unsupported hourly-only assumptions, move import/coverage semantics and expose result export; consolidate interval/DNI/component calculations in the transposition library; retain native workbook compatibility; own transposition reference tests.
- **Agent C: shading and correction.** Consolidate SkyScene with library projection/visibility; add inclined-panel correction, derived summaries and native exports; own mask/receiver/coverage/correction tests. It consumes the contracts agreed in Phase 1 and integrates Agent B's component results.

Assign distinct files before edits. The lead owns shared contract changes and project references. Agents must extend established calculations, not introduce another competing copy.

### Phase 3 — thin frontend integration

Lead agent rewires AppServices to the completed library operations, removes the listed frontend scientific implementations and binds UI values directly to library results. Move scientific tests to their owning library projects; keep UI/orchestration tests in ApplicationFrontend.Tests. Reuse a finished subagent for an independent boundary review.

Cache results with relevant inputs and library/algorithm version, including the authoritative source dataset/time-axis fingerprint (timestamps, interval bounds and value convention). A cadence change invalidates solar and downstream results. Panel changes rerun transposition/correction while reusing input data and solar geometry. Site/time changes rerun solar and downstream stages. Mask/profile/pose changes rerun visibility/correction. Publish cache keys and values together after successful computation; preserve native-call serialization and obsolete-UI-revision protection.

### Phase 4 — verify and package

Run library tests, direct-library versus frontend-orchestration comparisons, automatic artifact read-back and the packaged smoke test. Publish only after the architecture checks pass. Keep the existing portable executable/Example/Data/Debug Data layout.

## Completion criteria

- Headless calls to each stage library produce their YAML/PNG/XLSX artifacts without referencing SolarShade.Desktop.
- ApplicationFrontend contains no scientific formulas, interval integration, DNI inference, shading weights, scientific summary derivation or scientific workbook schemas.
- A test double returning distinct library totals/results reaches UI-bound values unchanged; real-library integration returns the same results through the frontend and direct invocation.
- Library behavior changes invalidate affected cached results and require no frontend source edits with stable interfaces.
- Native calibration round-trips correctly; mask output matches returned pixels; every numerical workbook matches returned stage values and intervals.
- Hourly and 15-minute fixtures preserve their respective source timestamps, interval counts and durations through all main outputs. Verify other supported durations and explicit variable-duration inputs; detect missing records without stretching intervals. Interval-mean energy totals use actual durations. Internal solar integration must not change main output cadence. Existing hourly provider tests still pass.
- Cover open/blocked/partial sky, receiver orientation, sunrise/sunset, diffuse-only rows, horizontal identity, missing coverage, clipped intervals, timestamp offsets/DST, missing data, cancellation and failed writes.
- Preserve current independent library validators. Review numerical differences rather than silently accepting them merely because a snapshot changed.
- Packaged calculation, panel edit, calibration and cache reuse produce truthful per-stage Debug Data.

The self-contained executable still needs republishing to include a changed library. The intended guarantee is no frontend calculation rewrite, not hot replacement of code embedded inside the executable.
