# PV–battery module frontend plan

**Status: initial brainstorm for discussion, not an approved implementation specification.**

Prepared 29 September 2026 against main commit `d4e021c`, after [backend PR #5](https://github.com/ckbk123/FisheyeSolarShadeForecast/pull/5).

This document considers how to add a battery-system workspace to the existing Windows application. Recommendations are provisional. Creating this document does not authorize implementing its proposed changes or settle the open decisions below.

**Suggested reading:** sections 3–5 describe the user experience, section 7 compares refactoring approaches, sections 10–11 explain storage/export consequences, and section 14 collects the decisions for discussion. The provisional direction is two full-workspace tabs, separate source/battery validity, focused extraction from the window class and an optional battery output stage.

## 1. What we are trying to achieve

The user should be able to complete an irradiance/shading study, describe a PV-powered load and battery, and inspect the battery's hourly state of charge over that same period.

The existing irradiance screen is already busy. A separate full-workspace tab is the leading option. Battery calculation must depend on a complete, current final shaded irradiance dataset; importing weather alone is insufficient.

The backend already exists. This work would connect it to input controls, readiness checks, charts, settings persistence and reliable exports. It would not add another numerical battery model.

### Established requirements versus open choices

| Established from the discussion and existing behavior | Still to decide |
|---|---|
| Use the implemented six-input battery model and final shaded panel irradiance. | Exact tab titles and battery-page arrangement. |
| Return end-of-hour SoC over the supplied irradiance period. | Inline, expandable or separate daily-load editor. |
| A stale, incomplete or invalid irradiance study cannot support a current battery estimate. | Whether an unavailable tab is disabled or opens an explanatory page. |
| Preserve the existing explicit-update workflow; edits must not launch calculations. | How much UI extraction to undertake in this feature. |
| Preserve valid upstream work when only system/load settings change. | Optional-stage storage, battery export and PDF presentation. |
| Discuss this brainstorm before implementation. | Default values, first-run behavior and result restoration on restart. |

**Suggested first-release boundary:** one current irradiance study, one battery/system configuration and one repeating 24-hour profile. Defer saved scenario comparisons, automatic sizing, multi-panel arrays, tariffs, grid import/export, alternative battery physics and research-tool benchmarking.

## 2. What the existing frontend actually separates

The frontend is native WPF, built programmatically in C# rather than XAML. It is not one undivided application, but its window class combines many responsibilities.

| Existing component | Current responsibility | Likely consequence of this feature |
|---|---|---|
| [MainWindow.cs](../src/ApplicationFrontend/MainWindow.cs) | Builds the entire screen; owns editable fields, events, active evaluation, revisions, cancellation, status and button eligibility. | Tab construction and independent battery interaction need a new boundary. |
| [AppServices.cs](../src/ApplicationFrontend/AppServices.cs) | Sequences the scientific pipeline, coordinates caches and publication, returns an `Evaluation` containing `PanelRun`. | Supply a stable completed irradiance snapshot to a battery coordinator. |
| [AppData.cs](../src/ApplicationFrontend/AppData.cs) | Mutable `UserSettings`, JSON persistence and input/library identity helpers. | Persist battery draft settings without changing irradiance identity. |
| [InputDependencies.cs](../src/ApplicationFrontend/InputDependencies.cs) | Stage flags, dependency keys, relevant field mapping and downstream invalidation. | Propagate shaded-source changes to battery work, never the reverse. |
| [UpdateState.cs](../src/ApplicationFrontend/UpdateState.cs) | Current update states and inexpensive input validation. | Battery needs its own state and explicit source-unavailable reasons. |
| [DebugDataRun.cs](../src/ApplicationFrontend/DebugDataRun.cs) / [DebugDataStore.cs](../src/ApplicationFrontend/DebugDataStore.cs) | One managed current dataset, locking, staging, manifests, recovery and invalidation. | Optional battery artifacts must coexist with a complete irradiance study. |
| [CurrentValueCache.cs](../src/ApplicationFrontend/CurrentValueCache.cs) | Recoverable stage values keyed by inputs and application software identity. | Decide whether battery results need disk restoration in the first release. |
| [SnapshotExport.cs](../src/ApplicationFrontend/SnapshotExport.cs) / [SummaryPdf.cs](../src/ApplicationFrontend/SummaryPdf.cs) | Verified snapshot export with a one-page irradiance recap. | Add battery export without breaking irradiance-only export or the existing one-page promise. |
| [ChartControl.cs](../src/ApplicationFrontend/ChartControl.cs) / [ChartLayout.cs](../src/ApplicationFrontend/ChartLayout.cs) | Irradiance rendering, navigation and timezone-aware axes. | Reuse suitable time-axis helpers, but give battery values their own rendering semantics. |

The current window starts at 1440 × 960 with a 1080 × 720 minimum. Its left input column is 348 units wide; its right side reserves 310 units for image previews before the graph. Keeping those previews and the existing sidebar on the battery screen would waste much of the newly available space.

The present complete-result check includes a current evaluation, a complete managed diagnostic set, nonempty panel rows, a mask and available shaded totals. This is a useful foundation, not a sufficient reason to attach battery validity to one global boolean.

## 3. Navigation and layout options

### Option A — Add a battery section to the existing screen

Put controls below the panel settings and add a chart selector for irradiance/SoC.

- Smallest initial layout change.
- Adds more scrolling and mixes upstream image/weather configuration with system sizing.
- One status indicator and export button become ambiguous.
- Poor fit for a readable 24-hour editor.

**Assessment:** feasible as a prototype; not recommended for the intended application.

### Option B — Two full-workspace tabs: recommended direction

Place tabs below the application header:

1. **Irradiance & shading** — the existing workflow.
2. **Battery & load** — system settings, daily load and SoC.

The tab boundary should contain the entire body, including the sidebar. Switching only the graph would leave the crowded irradiance controls occupying the battery workspace.

Proposed arrangement, not a fixed mockup:

```text
Application header                        Help   Export for this workspace
[Irradiance & shading] [Battery & load]

Source: current shaded study | selected period | time zone | View irradiance

System settings              Minimum SoC | Final SoC | Unmet energy
  Panel area                 ------------------------------------
  Panel efficiency           Large SoC chart, fixed 0–100% axis
  Conversion efficiency      Shortfall intervals clearly marked
  Battery capacity           ------------------------------------
  Initial SoC                Full period / chosen day / first shortfall

Daily load summary           Expandable hourly values and energy details
  profile preview
  Edit 24-hour profile
  Calculate battery
  Battery status
```

Advantages: clear dependency direction, separate workspaces and room for the new chart. Cost: window construction and status/action ownership need modest restructuring.

The source strip should be read-only. Dates, time zone, panel tilt and azimuth remain owned by the irradiance study. A link returns the user to the first tab to change them.

### Option C — A separate battery window

Useful if simultaneous side-by-side comparison is important, but introduces window lifetime, stale-source synchronization, placement and multiple export contexts.

**Assessment:** defer unless side-by-side viewing is a real requirement.

### Daily-load editor choices

| Choice | Benefit | Cost / concern |
|---|---|---|
| Always-visible 24-hour table | Immediate inspection and editing. | Uses substantial vertical space, especially at minimum window size. |
| Expandable editor inside the battery tab | Keeps editing near the chart, with no extra window. | Expansion competes with chart height; scrolling/focus needs care. |
| Dedicated editor dialog with profile preview on the tab | Leaves the main chart uncluttered; easy Apply/Cancel semantics. | Adds a step to inspect individual hourly values. |

**Initial preference:** a dedicated editor with a persistent profile preview and nominal daily consumption total. An expandable editor is a close alternative. This choice should be discussed before coding.

The editor should support labelled slots from 00:00–01:00 through 23:00–24:00, keyboard navigation, paste of 24 spreadsheet values, and filling all slots with a constant value. Bad paste must not partially replace a valid profile. Blank, invalid and zero values must remain distinguishable.

Use **“Consumption per hour (Wh)”**, with a note that each entry is numerically equal to average watts during that one-hour slot. The displayed daily total is for the nominal 24-hour pattern; an actual DST day can contain 23 or 25 elapsed hours.

## 4. When is battery estimation available?

“Complete” means the entire **selected study interval**, not every date that happens to exist in a larger imported file.

A central readiness check should verify:

1. A successful completed irradiance evaluation exists.
2. It matches current committed inputs and has no relevant invalid draft edits.
3. The existing required upstream artifacts remain available and verified.
4. The final shaded panel series is nonempty and covers both requested boundaries.
5. Every selected interval has a finite, nonnegative shaded total, a unique ID and valid bounds.
6. Intervals are ordered and contiguous, with no gaps or overlaps.
7. The study time zone is explicit and valid.

Use the existing period-selection validation and the battery adapter/core validation together. Contiguous rows alone do not prove that the requested first and last dates are covered.

**Do not require positive irradiance in every hour.** Zero nighttime irradiance and fully shaded intervals are legitimate values.

**Do not confuse numerical completeness with physical certainty.** The existing shading model can produce a valid conservative estimate when some sky is unobserved. Surface its coverage/assumption message; do not invent a new 100% observed-sky threshold. If stricter research-quality requirements are wanted later, define them separately.

### Unavailable-tab options

- **Disabled tab:** matches “available only after irradiance is ready” literally. Keep an adjacent visible explanation; a disabled tab's tooltip alone is inadequate for keyboard users.
- **Accessible explanatory page:** the tab opens to “Complete and update irradiance results first”; calculation and export are disabled. Optionally allow system inputs to be prepared in advance.

**Initial preference:** the explanatory page is easier to discover and understand, but the user's preference for a strictly disabled tab remains open. Both must enforce the same service-level calculation gate.

If an external source change invalidates results while the battery page is open, keep the user's configuration and explain the blockage there. Do not unexpectedly switch tabs or discard their work.

## 5. Two independent result states

Keep an irradiance state and a battery state. Do not turn a valid irradiance result stale just because the battery is not configured.

Suggested battery states:

| State | Display and allowed action |
|---|---|
| Source unavailable | Show the reason and route to irradiance; no calculation or result export. |
| Inputs need attention | Highlight system/profile errors; no calculation or result export. |
| Ready / update required | Source and inputs valid; Calculate battery enabled. Previous result visibly stale. |
| Calculating | Progress/Stop; prevent duplicate calculation and result export. |
| Current | Chart, summary and export correspond to the accepted source and settings. |
| Stopped / failed | Explain outcome; preserve editable configuration; no current-result export. |

Changing a valid numeric text field to blank or malformed text must invalidate battery export immediately, even before focus leaves the field. Do not silently calculate with its last valid value.

At Calculate, capture the current source identity and a deep snapshot of all six settings, including the 24 load values. Give battery work a separate revision/cancellation token. Accept completion only if both its settings revision and irradiance source identity still match. Tab switching, chart navigation and a window resize do not cancel or rerun a study.

Conservative first-release behavior: upstream work in progress prevents starting battery work. Battery-only work must not hold the native image-processing semaphore; serialize only shared storage publication where necessary.

## 6. Dependency and recalculation rules

| User or external change | Irradiance impact | Battery impact |
|---|---|---|
| Panel surface area, panel efficiency, conversion efficiency | None | Stale; rerun battery only. |
| Battery capacity, initial SoC, any hourly load entry | None | Stale; rerun battery only. |
| Panel tilt, azimuth or diffuse model | Existing transposition/shading invalidation | Source unavailable until upstream update; then battery requires explicit calculation. |
| Camera/profile/photo/mask/pose affecting final shading | Existing dependency-specific invalidation | Invalidate battery if final shaded source is affected. |
| Weather, selected period, effective time zone, relevant site/interval settings | Existing dependency-specific invalidation | Invalidate dependent battery result. |
| Force weather refresh | Upstream update required/running | Stale even before replacement weather arrives. |
| Relevant source file changed in place | Existing fingerprint detection | Invalidate dependent battery result. |
| Upstream diagnostic files removed or altered | Existing verification rules | Block until upstream validity is restored under the chosen readiness policy. |
| Battery output file removed or altered | None | Battery export/result-publication state invalid; upstream remains usable. |
| Graph zoom/day, tab choice, overlay visibility | None | None. |

Do not invalidate battery results merely for an inactive provider/import option or checkerboard setup that does not alter the loaded profile. Reuse the existing relevance mapping.

Switching back to old settings must not resurrect a supposedly current battery result automatically after its artifacts were invalidated. Explicit calculation may reuse a verified cache, but must republish and verify the requested result.

A battery identity should include the selected final shaded series, effective time zone, six settings and relevant battery library identities/model version. Do not key it only by the raw weather fingerprint: different shading can share the same raw dataset.

## 7. How much frontend restructuring?

| Approach | Initial effort | Longer-term consequence |
|---|---|---|
| Add all controls/events to `MainWindow`, perhaps using partial files | Low | Splitting files does not separate state ownership; global status and lifecycle become harder to reason about. |
| Extract focused views and a small battery coordinator | Moderate | Establishes boundaries needed by this feature while preserving existing scientific orchestration. |
| Convert the entire frontend to MVVM/XAML and a new application architecture | High | Larger regression surface and delayed feature delivery; justified only as a separately agreed project. |

**Recommendation:** focused extraction. A complete frontend rewrite is not a prerequisite.

Provisional boundaries, with names subject to implementation discussion:

- `MainWindow`: application shell, tabs, shared header and lifetime.
- `IrradianceWorkspaceView`: existing controls/previews; initially keep its established behavior.
- `BatteryWorkspaceView`: system editor, load summary, chart and local status.
- `BatteryWorkspaceController`: draft validation, source readiness, revision handling, calculation requests and accepted snapshot.
- `DailyLoadEditor`: independent editing transaction returning exactly 24 values.
- `BatteryChart`: presentation of returned battery values.
- Shared application storage/export coordination: retain one owner of managed output.

These can remain programmatic WPF controls. Avoid introducing a framework merely to achieve two tabs.

The most important extraction is **state ownership**, not the number of source files. Existing window-level tests may need their controls/test seams relocated; avoid breaking the UI and rewriting the test architecture in the same step without a behavior-preserving checkpoint.

## 8. Scientific integration and chart semantics

Call `PanelBatterySimulation.Compute` using the accepted `Evaluation.Run`; do not re-import the application's own workbook in the normal interactive path. Keep XLSX input as the existing standalone/offline path for now. Adding a second independent battery-source picker would create another provenance and readiness workflow.

Reference the integration and IO projects from the Windows frontend. Adapt percentages to backend fractions once at the boundary. Keep energy calculations in the backend.

The chart needs its own treatment:

- SoC is a state at interval end, not an interval-mean irradiance value. Do not reuse the current irradiance step renderer unchanged.
- Show an initial point at the study start, then clearly identified end-of-hour points. Any connecting line is a visual guide, not evidence of measured within-hour behavior.
- Keep the axis at 0–100%; show both percent and stored Wh on hover.
- Mark intervals with unmet load, including when hourly endpoints alone would hide a subhour depletion/recovery.
- Compute headline minimum SoC from the returned summary, which includes substeps and the initial state, not only plotted hourly endpoints.
- Distinguish “battery reached zero” from “demand was not met.” Exact depletion at a boundary can have zero unmet energy.
- Preserve partial-hour markers and UTC offsets around repeated local hours.
- Day selection and zoom only change the view; they must not restart simulation or reapply initial SoC.
- For long studies, any display reduction must preserve extrema and shortfall markers. Exports retain all returned rows.
- Label the shortfall timestamp as “First interval with unmet load”; the backend does not return the exact within-interval outage instant.

Keep PV/load energy comparison optional, in a separate detail view or subplot with its own units. Do not put SoC (%) and irradiance (W/m²) on an unexplained shared axis.

Suggested main summary: minimum SoC, final SoC, unmet load in Wh. Secondary details: intervals containing shortfall, generated/consumed/served/curtailed energy and first shortfall interval.

Use “No unmet load during this study” rather than a permanent reliability guarantee.

## 9. Settings, defaults and restart behavior

Prefer a separate battery settings object owned by the battery workspace, rather than adding every field to the existing irradiance `UserSettings`. It can be persisted in a separate versioned settings file or nested under a future application-settings envelope. Separate persistence is the smaller first-release change.

Keep editable draft text separate from validated numerical settings. Deep-copy the load profile on Apply and Calculate; existing `with { }` copies of settings are shallow and would not protect a mutable array added naively.

Decisions still needed:

- Start with blank system values, or clearly labelled editable example values? **Preference:** blank on a new real study; provide an explicit battery example/preset.
- Initial SoC default: 100%, 50%, or require entry? Do not imply that a default is measured.
- “Load example”: replace battery settings too, or preserve them? **Preference:** load an explicitly labelled complete example when requested; ordinary upstream recalculation preserves the user's battery settings.
- On restart, restore a verified current result or only settings? **Preference for first release:** restore settings, then require explicit calculation using a current irradiance snapshot unless verified result restoration is already straightforward.

The backend JSON export is an audit artifact, not an established result-loading API. Do not assume that its immutable result type can be passed directly through `CurrentValueCache.Read<T>` and restored without additional design and tests.

Use the study timezone, not a separately selected battery timezone. Changing the effective automatic system timezone follows the upstream invalidation rules. An explanatory DST note belongs beside the repeating daily profile.

## 10. Managed outputs: the main integration risk

The existing storage model assumes one all-or-nothing calculation:

- `DebugDataRun.IsCurrent` requires global Complete status and no invalidated group.
- `HasCompleteDataset` requires every entry in `AllStages` and the mandatory export artifacts.
- `Invalidate` changes the global status to Stale.
- `SnapshotExport` currently copies the recorded artifact set and validates one complete irradiance export.
- Dependency groups, key comparisons, stage enumeration and recovery share these assumptions.

Simply adding `Battery` to every “All” list would create incorrect behavior: a battery edit could invalidate a valid irradiance study; an unconfigured battery could block irradiance export; battery readiness could depend on its own completion.

### Storage options

| Option | Benefit | Tradeoff |
|---|---|---|
| Optional `07-battery` under the existing managed Debug Data root | One discoverable dataset, writer owner and export provenance chain. | Requires explicit upstream/battery completeness scopes and careful changes to manifest validation and recovery. |
| A separate bounded battery output root linked to an irradiance snapshot | Less direct coupling to the existing stage manifest. | Two datasets and publication lifecycles; coherent combined export and upstream invalidation still need coordination. |
| Results only in memory, writing files on export | Smallest storage integration. | Departs from the existing “completed results have managed diagnostics” behavior; no comparable recovery/audit set. |

**Provisional preference:** an optional `07-battery` stage under the same storage owner. This is a deliberate storage change, not just another folder name. If preserving the current manifest untouched is more important, discuss the separate-root option before implementation.

Required behavior whichever option is chosen:

1. Irradiance completeness is defined independently of optional battery state.
2. Battery completeness requires a valid upstream snapshot plus matching battery settings and artifacts.
3. A battery result records its own identity and the exact source identity. Updating only the battery must not replace or rewrite the scientific identity of its source.
4. Draft battery edits remove or mark stale only current battery artifacts, following the existing bounded-storage policy.
5. Source changes invalidate dependent battery artifacts without deleting user-owned scenario exports.
6. Use the existing storage owner/lock; do not open a competing `DebugDataStore` for the same root.
7. Write all required battery files to staging, verify them, then publish a completed battery stage. The backend's per-file atomic exporters do not make three separate files a transaction.
8. Recheck source/settings identity at publication and export. Cancelled or superseded work cannot publish as current.
9. Recovery handles an interrupted optional stage without destroying valid irradiance outputs.
10. Old manifests/settings migrate predictably; changing flags, required-stage lists or software fingerprints must be tested explicitly.

Candidate files: `battery-hourly.csv`, `battery-result.json`, `battery-hourly.xlsx`, plus stage provenance. Decide whether all three are mandatory managed artifacts or whether some are optional export formats.

Avoid invalidating the expensive source calculations solely because the battery library was updated. The existing global software fingerprint may need a scoped extension; a universal cache redesign is not required for this feature.

## 11. Export choices

The first tab's existing export must continue to work without a battery calculation. A battery failure must not turn that valid export into a failure.

| Export approach | Assessment |
|---|---|
| Separate “Export irradiance” and “Export battery” actions | Clear initial implementation; least ambiguity about readiness. |
| One export menu with Irradiance / Battery / Complete system choices | Useful once combined export is implemented, but more eligibility and snapshot rules. |
| Automatically include whatever battery files exist | Reject: stale or mismatched optional files could silently enter an export. |

**Initial preference:** contextual actions per tab. Battery export should include the six settings, complete source/timezone provenance, exact result rows and a verified source reference sufficient to audit which shaded series was used. The backend JSON already contains the supplied irradiance intervals.

If a complete-system export is included, freeze matching upstream and battery snapshots together. Explicitly select artifacts by export scope; do not blindly copy old optional files just because they remain listed in a manifest.

The current irradiance PDF is intentionally one page and its exporter enforces that. Options for battery reporting:

- Keep the existing PDF unchanged and export battery data only in the first release.
- Add a separate one-page `BatterySummary.pdf`.
- Introduce a new multi-page system report through a separately named export mode.

Do not silently add pages to the existing `Summary.pdf` contract. **Preference:** data exports first, or a separate battery recap if a readable shareable report is part of the agreed first release.

## 12. Proposed implementation milestones

These are review checkpoints, not time estimates or approval to start implementation.

| Milestone | Work | Exit condition |
|---|---|---|
| 0 — Agree scope and UX | Resolve the key decisions below; sketch normal, blocked and stale states. | The tab, load editor, refactoring boundary and export/storage scope are agreed. |
| 1 — Establish view boundaries | Extract enough window construction/state ownership to host two workspaces. Keep battery as a placeholder. | Existing irradiance behavior, numerical results and manual-update/export tests are unchanged. |
| 2 — Define readiness and optional-state ownership | Centralize source readiness; introduce battery settings/revisions and explicit storage completeness scopes. | Battery edits cannot dirty irradiance; source edits reliably block battery; old manifests remain usable. |
| 3 — Connect controls and backend | Add system inputs, load editor and explicit Calculate/Stop actions; consume accepted `PanelRun`. | UI-derived requests produce the same returned results as direct backend calls. |
| 4 — Present results | Add SoC chart, summaries, source strip, shortfall navigation and details. | Partial intervals, repeated hours, empty/full battery and subhour shortfalls display correctly. |
| 5 — Publish and export | Add managed artifacts and selected export/report behavior; settings/recovery handling. | No mixed/stale snapshot can be exported; failure leaves valid upstream data usable. |
| 6 — Regression and packaged review | Run existing/new tests, inspect responsive layout, update Help/docs and package the application. | Packaged workflow works from first launch through edit, calculation, restart and export. |

Separate behavior-preserving extraction from new behavior in commits where possible. A focused frontend branch can be reviewed against the merged backend. No backend model rewrite should be hidden inside this UI work.

## 13. Acceptance tests to design before implementation

The merged backend's recorded baseline is 460 passing solution tests, including 65 PV–battery tests. Re-establish the current baseline when implementation starts; that historical count is not a new test run for this planning document.

### Source readiness and independence

- Missing source, baseline-only output, failed/stopped upstream work, stale settings and missing artifacts block battery calculation and export.
- Truncated but otherwise contiguous data fails requested-period coverage validation.
- Gaps, overlaps, duplicate IDs, nonfinite values and missing shaded totals are rejected.
- Zero nighttime irradiance and a fully shaded zero series remain valid.
- A valid conservatively shaded dataset remains usable with its assumptions visible.
- Every dependency-table change affects exactly its downstream work.
- Battery settings edits leave upstream artifacts and expensive preparation counters unchanged.
- Missing battery outputs block battery export but preserve irradiance export.
- Completing irradiance without ever opening the battery tab still succeeds.

### Inputs and calculation lifecycle

- Typed-but-uncommitted invalid text disables battery export immediately.
- Percent fields convert to fractions exactly once; Wh inputs are not mistaken for Ah.
- Exactly 24 valid nonnegative entries are required; zero load is valid.
- Invalid paste and cancelled editor changes do not partially mutate the accepted profile.
- Repeated edits and tab switches do not start calculations.
- Double-clicking Calculate starts one run; Stop cannot leave a current partial result.
- Source/settings changes during a slow calculation prevent its late result from becoming current.
- Settings snapshots retain their original 24 values after subsequent edits.
- Existing and new settings files reopen predictably without silently selecting a measured-looking default.

### Numerical presentation

- Compare every displayed/exported value against direct backend output for the frozen fixture.
- First point represents initial SoC; hourly points represent interval ends.
- Zero SoC with no unmet load is not labelled an outage.
- A subhour shortfall followed by recovery remains visible even if the hourly endpoint is positive.
- Summary minimum agrees with the backend's substep-aware minimum.
- Partial first/last hours and DST repeated hours retain their correct identity.
- Selecting another display day does not reset stored energy.
- Long-period chart reduction preserves minimum values and every shortfall indicator.

### Storage, export and recovery

- Exercise locked XLSX files, failed writes, cancelled publication and application restart.
- Verify source and battery snapshots match throughout export, even if drafts change during copying.
- Irradiance-only export excludes stale battery artifacts; combined export rejects mismatched versions.
- Old manifests and caches migrate without treating an optional missing battery stage as upstream failure.
- Existing upstream files remain byte-identical during battery-only calculation.
- Repeated updates keep bounded managed output; explicit user exports are preserved.
- A second app instance cannot race the active writer.
- Any new PDF is rendered and visually checked, with the existing recap's one-page contract preserved.

### Interface and packaged behavior

- Inspect 1440 × 960 and 1080 × 720, plus 125% and 150% scaling.
- Ensure all input fields, profile cells, action buttons and status explanations are reachable.
- Check keyboard navigation, accessible field names, focus after editor closure and non-color status cues.
- Validate full-year navigation without freezing the UI.
- Test the packaged executable, including dependency inclusion, first launch, Load example, manual updates, Stop, restart and exports.

## 14. Decisions for our next discussion

| Priority | Decision | Initial recommendation |
|---|---|---|
| 1 | Two tabs or another layout? | Two full-workspace tabs; keep irradiance setup and system sizing separate. |
| 2 | What happens before irradiance is ready? | Accessible explanation with calculation/export blocked; disabled-tab variant remains available. |
| 3 | Where should the 24-hour editor live? | Dedicated editor, with preview and daily total always visible on the battery page. |
| 4 | How much refactoring now? | Focused view/state extraction; no full MVVM/XAML conversion. |
| 5 | Where do optional battery diagnostics belong? | Same managed root, optional stage, explicit independent completeness checks. |
| 6 | Which export/report modes are essential? | Per-tab export; preserve current irradiance PDF; decide separately on battery recap. |
| 7 | Which defaults and restart behavior? | Explicit example values; persist user settings; restore results only with verified source identity. |

The highest-impact choices are state/storage ownership and the refactoring boundary. Tab styling can change later without changing the scientific contract; a global validity design would be much harder to unwind.

## 15. References and review scope

- [PV–battery backend model, API and units](../src/PvBatterySimulation/README.md)
- [Backend validation record](../src/PvBatterySimulation/VALIDATION.md)
- [Existing manual-update and export specification](MANUAL_UPDATE_AND_EXPORT_PLAN.md)
- [Application architecture](APPLICATION_ARCHITECTURE.md) — useful ownership context; some historical lifecycle prose predates the manual-update milestones, so current code and the implemented manual-update specification take precedence.
- [Frontend running and packaging guidance](../src/ApplicationFrontend/RUNNING.md)

This draft changes no frontend or scientific source. The next step is discussion and revision of these options, followed by an agreed implementation specification.
