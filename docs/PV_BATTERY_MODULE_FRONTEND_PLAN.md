# PV–battery module frontend implementation plan

**Status: revised plan for discussion. The user-directed two-part structure and layout below replace the initial brainstorm. Implementation has not started.**

Prepared 29 September 2026 against main commit `d4e021c`, after [backend PR #5](https://github.com/ckbk123/FisheyeSolarShadeForecast/pull/5). The document is maintained in [draft planning PR #6](https://github.com/ckbk123/FisheyeSolarShadeForecast/pull/6).

## Scope and decisions carried forward

This is now explicitly **two separate implementation parts**:

1. **Reorganize the frontend for extensions.** Reevaluate responsibilities, introduce a tabbed application shell, separate workspace state, and establish service, dependency and storage boundaries.
2. **Build the PV-system evaluation workspace.** Connect the existing battery backend, implement the 24-hour consumption editor and system settings, and display consumption, PV production and battery SoC together.

Part I has its own working deliverable and regression checks. Part II builds on those boundaries. This is more than placing another chart inside the current window.

The user's selected layout is:

- Tabs across the top of the workspace.
- A left-hand column dedicated to the editable 24-hour consumption list.
- System settings across the top of the larger right-hand area.
- One large chart in the lower right, overlaying consumption, PV energy and SoC.
- The PV workspace becomes operational only when the complete selected irradiance/shading study is valid and current.

These replace the earlier proposal for a separate load-editor dialog and an optional separate energy subplot. Naming and the implementation details below are recommendations for discussion.

The first release supports one current irradiance study, one PV/battery configuration and one repeating daily load profile. Direction optimization is an architectural extension to prepare for, not a solver to implement in these two parts.

**Reading guide:** Part I defines the changes to the existing application; Part II specifies the PV workspace. The final sections map delivery checkpoints, tests and remaining decisions.

## Naming recommendation

Use **PV Autonomy** for the tab and **PV System Autonomy** for its page heading.

Supporting text: **“Evaluate energy supply and battery charge over the study period.”**

The shorter tab name is easier to scan beside **Irradiance & shading**. “Autonomy” expresses the actual question—whether the PV/battery system can supply the load—rather than describing just a battery graph.

| Candidate | Strength | Limitation |
|---|---|---|
| **PV Autonomy** — recommended tab label | Short; covers generation, consumption and battery behavior. | Explain autonomy in the page subtitle/help. |
| **PV System Autonomy** — recommended page heading | More explicit without being excessively technical. | Slightly long as a tab caption. |
| **Autonomous PV Evaluator** | Closest to the user's initial wording. | Longer; “autonomous” can also suggest automated operation. |
| **Solar & Battery** | Familiar wording for less technical users. | Does not convey assessment of load supply. |
| **Off-grid PV** | Immediately identifies a standalone system. | Describes a system category rather than the evaluation task. |

Proposed action labels: **Evaluate system**, **Stop**, **Export PV results**. Result wording should be **“No unmet load during this study”** or **“Unmet load detected”**, not a permanent reliability guarantee.

Names of internal classes can retain `PvBattery` even if the public workspace is called PV Autonomy.

# Part I — Reorganize the frontend for extensions

## I.1. Review the current boundaries before moving code

The frontend is programmatic WPF C#, not XAML. WPF supplies a standard `TabControl`; the current `MainWindow` does not use a tabbed workspace. We need to introduce and style the container, then organize the views and their ownership around it.

Several modules already exist. The main problem is that presentation and operation state are concentrated in the window, while `AppServices` and the diagnostic store assume one primary calculation.

| Existing component | Current responsibility | Planned treatment |
|---|---|---|
| [MainWindow.cs](../src/ApplicationFrontend/MainWindow.cs) | Constructs the screen; owns fields/events, active evaluation, status, revisions and cancellation. | Reduce to application shell, tab host and lifetime; move workspace-specific responsibilities out. |
| [AppServices.cs](../src/ApplicationFrontend/AppServices.cs) | Irradiance pipeline, caches, native-work coordination, diagnostics and current-evaluation checks. | Preserve its scientific orchestration behind an irradiance service boundary; expose an accepted result handoff. |
| [AppData.cs](../src/ApplicationFrontend/AppData.cs) | Mutable `UserSettings`, JSON persistence and identity helpers. | Separate irradiance settings, PV settings and view preferences; migrate saved settings deliberately. |
| [InputDependencies.cs](../src/ApplicationFrontend/InputDependencies.cs) | Scientific-stage keys, relevant-field mapping and invalidation. | Retain fine-grained upstream rules; add workspace-level downstream dependencies. |
| [UpdateState.cs](../src/ApplicationFrontend/UpdateState.cs) | Current update state and lightweight validation. | Give each workspace its own state, readiness and export eligibility. |
| [DebugDataRun.cs](../src/ApplicationFrontend/DebugDataRun.cs) / [DebugDataStore.cs](../src/ApplicationFrontend/DebugDataStore.cs) | Current dataset, locking, staging, manifests and recovery. | Support optional dependent results without invalidating a complete upstream dataset. |
| [CurrentValueCache.cs](../src/ApplicationFrontend/CurrentValueCache.cs) | Recoverable stage values with application-wide software identity. | Keep useful caches; separate identities where a PV-only change must not invalidate irradiance. |
| [SnapshotExport.cs](../src/ApplicationFrontend/SnapshotExport.cs) / [SummaryPdf.cs](../src/ApplicationFrontend/SummaryPdf.cs) | Verified export with a one-page irradiance recap. | Introduce explicit export scopes and preserve the existing irradiance contract. |
| [ChartControl.cs](../src/ApplicationFrontend/ChartControl.cs) / [ChartLayout.cs](../src/ApplicationFrontend/ChartLayout.cs) | Irradiance rendering and timezone-aware navigation. | Reuse general time-axis helpers; isolate the PV chart's series and scale semantics. |
| [ApplicationFrontend.csproj](../src/ApplicationFrontend/ApplicationFrontend.csproj) | Frontend compilation, project references and packaging. | Update compile paths and PV references as new folders/components are introduced. |

The project currently disables default compile items and includes only `*.cs` at its root. Moving classes into subfolders requires explicit compile includes or a carefully scoped replacement. Do not accidentally compile `Tests`, `obj` or generated files into the application.

First inventory event subscriptions, source watchers, system-timezone callbacks, ownership of cancellation/disposal, test-access methods, and storage locks. These must move with their responsible component.

## I.2. Target organization

Choose a modest workspace architecture with concrete services. Do not put a second large block of controls and lifecycle logic into `MainWindow`.

Provisional organization inside the existing frontend project:

```text
Shell/
  MainWindow / workspace tab host
  application composition and lifetime

Workspaces/
  Irradiance/
    view, controller/state, existing settings editor
  PvAutonomy/
    view, controller/state, daily-load editor, system settings

Services/
  irradiance evaluation adapter around existing AppServices
  PV evaluation service calling the existing backend

State/
  accepted irradiance snapshot / source readiness
  workspace state and downstream invalidation

Storage/
  shared publication owner, scoped manifests and exports

Charts/
  shared time-axis helpers, existing irradiance chart, PV system chart
```

The directory names are provisional; responsibility boundaries are the requirement.

| Owner | Owns | Must not own |
|---|---|---|
| Shell | Navigation, common header, active workspace, application shutdown. | PV equations, scientific-stage orchestration or a universal result-valid flag. |
| Workspace view | Controls, bindings/events, chart display, accessible messages. | Scientific calculations or direct access to another workspace's controls. |
| Workspace controller/state | Draft inputs, validation messages, operation revision, cancellation and accepted-result presentation. | Reimplementation of backend numerical rules. |
| Irradiance service | Existing pipeline and caches; accepted source publication/readiness. | PV load editing or a forced battery stage on every irradiance update. |
| PV evaluation service | Validated request construction, backend invocation and returned result handoff. | Re-running weather, masking or transposition for battery-only edits. |
| Shared storage/export owner | Writer lock, staging, verified publication, recovery and scoped export. | Treating an optional PV failure as a failed upstream study. |

A small workspace descriptor can expose ID, title, view, availability/reason and local status to the tab host. The shell should not need to know how an individual workspace computes its result.

Use straightforward construction/event subscriptions and typed requests/results. Dynamic plugin discovery, a universal event bus, a generic workflow engine and a full new dependency-injection framework are not required. A later workspace should be addable without reopening every event handler in `MainWindow`.

### Refactoring approach

Recommended: extract programmatic WPF views plus controllers/state objects, retaining existing libraries and introducing bindings where they simplify state display. Full MVVM/XAML conversion remains an alternative only if separately justified during the inventory.

Partial classes alone are insufficient: they split files while leaving shared mutable state and global lifecycle ownership intact.

Keep compatibility wrappers/test entry points temporarily where helpful. Extract the irradiance view first, then move its state and orchestration calls in small behavior-preserving steps.

## I.3. Tab host and workspace lifecycle

Use one full-body tab host below the application header:

- **Irradiance & shading**
- **PV Autonomy**
- Future modules, such as **Orientation optimization**, can be registered later; do not show an unfinished optimizer tab now.

Each tab owns its entire body. The irradiance sidebar and image previews belong only to the first tab.

Create each workspace once for the application session. Switching tabs preserves draft inputs, accepted results and chart navigation; it does not reconstruct services, reload data or calculate anything.

Keep Help and common navigation in the shell. Calculation, Stop, status and export operate on the relevant workspace. A status badge on an inactive tab can show that its results need an update, without replacing another workspace's status message.

The source-readiness service controls PV availability; no click handler should infer readiness from a green label or an enabled export button.

Part I may use a nonfunctional PV placeholder to verify navigation and readiness. Part II replaces it with the actual evaluator.

## I.4. AppServices changes and the source-to-PV handoff

Yes, `AppServices` needs changes, but it should not become a larger service containing all future modules. Treat its existing implementation as the irradiance evaluator, with a narrower interface/adaptor and a clear output boundary.

Suggested contract responsibilities:

| Contract / operation | Purpose |
|---|---|
| Irradiance evaluate request | Immutable snapshot of relevant settings plus explicit refresh mode. |
| Accepted irradiance snapshot | Final `PanelRun`, selected bounds, effective timezone, source/scientific identity, upstream settings/provenance and artifact identity. |
| Source readiness result | Ready/not ready plus an actionable reason; same rule used by tab state, calculation and export. |
| Source changed notification | Carries a revision/identity change so dependent work becomes stale promptly. |
| PV evaluate request | Accepted source snapshot + immutable six-input `BatterySimulationSettings`. |
| Accepted PV result | `BatterySimulationResult`, its settings snapshot, source identity, operation revision and published-artifact identity. |

Do not simply pass a reference to mutable `Evaluation.Settings` or the window's current text fields. Freeze/deep-copy the relevant values; `with { }` is not sufficient for a mutable 24-element array.

Planned calculation flow:

1. The irradiance workspace explicitly requests an update through its service.
2. The service completes, validates and publishes the irradiance result.
3. An accepted source snapshot is exposed; the shell updates downstream availability.
4. The user edits the PV workspace and clicks Evaluate system.
5. The PV service checks source currency and validated settings, then calls `PanelBatterySimulation.Compute`.
6. The result returns with the source identity and PV revision that produced it.
7. Recheck both identities before accepting/publishing the result.
8. The workspace renders returned values and enables export only for that accepted result.

Use an injectable backend-call boundary for orchestration tests. Tests should be able to delay a request, invalidate its source and confirm that its late completion is rejected.

Updates to old APIs can be staged behind adapters to preserve existing callers. Register/construct the PV service in the application's composition layer, not inside the chart or the tab-selection handler.

## I.5. Dependency, readiness and state model

Keep fine-grained irradiance-stage dependencies in their existing domain. Add a workspace dependency: **PV evaluation consumes the accepted final shaded irradiance snapshot.**

The rule “irradiance must be perfectly valid” means complete, current, structurally valid and accepted under the existing scientific assumptions:

- Successful completed upstream calculation matching current relevant inputs.
- No unresolved relevant draft errors or refresh/update in progress.
- Required upstream artifacts present and verified under the existing completeness contract.
- Nonempty final shaded series covering both boundaries of the selected study period.
- Finite, nonnegative values; valid explicit intervals and unique IDs.
- Ordered, contiguous coverage without gaps or overlaps.
- Explicit valid study timezone.

Combine existing requested-period validation with the battery adapter's series validation. A contiguous middle fragment is not complete coverage of a requested year.

Nighttime zeros and fully shaded intervals are valid. Existing conservative treatment of unobserved sky remains an assumption to disclose; do not invent a requirement for 100% observed sky or imply that validated historical data is physically certain.

### Tab behavior

Before a valid source exists, show the PV tab label with an unavailable state and a persistent message such as **“Complete and update irradiance results to use PV Autonomy.”** Recommended initial behavior is to disable entry into its working controls.

If the source becomes invalid while the PV tab is already open, keep the page and its draft values visible with a blocking explanation. Disable evaluation/export, label old results stale, and provide **Go to irradiance**. Do not force a tab switch or discard inputs.

The service enforces the same rule even if called without the UI. Whether an unavailable tab can be selected solely to read an explanation is a small remaining UX choice; it does not relax the operating dependency.

### Independent states and invalidation

Each workspace has its own needs-attention, update-required, running, current, stopped and failed states; PV also has source-unavailable. No optional PV state may make a complete irradiance study incomplete.

| Change | Irradiance | PV evaluation |
|---|---|---|
| Load entry, area, efficiencies, capacity or initial SoC | Unchanged | Stale; evaluate PV only. |
| Relevant weather/date/timezone/site/interval change | Existing dependency-specific invalidation | Source unavailable; invalidate PV. |
| Panel tilt/azimuth/diffuse model | Recompute affected panel stages | Invalidate PV. |
| Relevant photo/profile/mask/camera change | Existing shading dependency invalidation | Invalidate PV. |
| Source-file content changed in place | Existing fingerprint checks | Invalidate PV if its shaded source is affected. |
| Force weather refresh | Update required/running | Block and invalidate before replacement data arrives. |
| Battery output changed/missing | Unchanged | PV publication/export no longer current. |
| Tab selection, chart zoom/day, legend visibility | Unchanged | Unchanged. |

Inactive provider/import options and future checkerboard setup should remain governed by the existing relevance mapping. Do not invalidate everything on every edit.

Use separate revisions/cancellation tokens per operation. UI-thread changes mark results stale immediately, including malformed uncommitted text. Background callbacks dispatch to the UI and check revision/source identity before updating it. Stop, source changes and shutdown must not permit an obsolete result to become current.

Part I should establish ordering and ownership: do not hold the native image-processing semaphore for PV arithmetic. Serialize shared output publication through the existing storage owner. Until concurrency is deliberately supported, prevent overlapping upstream/PV evaluations and explain why the action is unavailable.

## I.6. Optional results, storage and export scopes

The current diagnostic store is not yet ready for an optional downstream workspace:

- `IsCurrent` expects global Complete status and no invalidated group.
- `HasCompleteDataset` requires `AllStages` and mandatory artifacts.
- `Invalidate` marks the global run Stale.
- Recovery, stage enumeration and export share those assumptions.

Blindly extending every “All” list with battery would create a circular dependency: upstream readiness could require a battery result that itself requires upstream readiness.

Part I therefore needs explicit completeness scopes:

- **Irradiance complete:** existing required upstream stages are current.
- **PV complete:** accepted upstream source + matching PV settings/result/artifacts.
- A future optimization result will have its own completeness definition.

Recommended storage direction: optional `07-battery` under the existing managed Debug Data root, sharing one writer owner and bounded staging area. Define the scope model in Part I; implement actual PV artifact writing in Part II.

Required structural changes:

1. Separate mandatory upstream stages from optional downstream stages.
2. Give PV results their own identity/status and record the exact upstream identity.
3. PV-only edits/failures do not change upstream scientific identity or completeness.
4. Downstream invalidation cannot delete or rewrite valid upstream files.
5. Recovery can mark an interrupted optional result incomplete while retaining upstream success.
6. Existing manifests/settings migrate explicitly; preserve user inputs and exported scenarios.
7. Export chooses a scope and matching verified artifacts, not every file that happens to exist.
8. Scoped software identities avoid invalidating expensive source computations solely for a PV-library update.

Maintain a single `DebugDataStore` owner for the root. Its lease and shared staging directory cannot safely be independently recreated by every workspace.

A separate bounded PV root is an alternative if manifest evolution proves disproportionately risky, but source identity, coordinated invalidation and coherent exports are still necessary. Decide that substitution during Part I rather than discovering the storage problem at the end of Part II.

## I.7. Prepare for orientation optimization without implementing it

The architecture should permit a later service to evaluate candidate panel directions against the same load, battery and weather period.

That extension will require more than the current final shaded curve. Each changed orientation requires its own transposition and panel-dependent shading; it cannot reuse a single orientation's final irradiance as though it were orientation-independent.

Expose a future-compatible service boundary between:

```text
Prepared weather / solar geometry / calibrated sky scene
  -> candidate orientation
  -> candidate final shaded panel irradiance
  -> PV/battery evaluation with fixed system/load settings
  -> candidate score and provenance
```

For now, document the ownership and preserve the ability to extract a pure candidate-evaluation operation from existing orchestration. Do not build the search algorithm, optimizer screen or full batch API in these parts.

A future optimizer must not simulate candidates by changing visible sliders, replacing the user's accepted study or repeatedly publishing candidates into the same current Debug Data folders. Candidate work should be isolated; accepting a chosen design is an explicit later action.

Do not bake in an objective such as “highest annual irradiance.” That can differ from minimizing unmet load or maintaining a reserve. Annual claims also require adequate full-year source coverage; the PV evaluator itself supports any valid supplied period.

### Part I completion checkpoint

Part I is complete when the existing irradiance workspace operates inside the new shell, its current behavior remains verified, readiness/state/service/storage boundaries are in place, and a test workspace can consume a stable source notification without reaching into another view's controls.

This is a useful deliverable on its own. Review it before adding the production PV controls. The actual optimizer remains deferred.

# Part II — Build the PV Autonomy workspace

## II.1. Required screen arrangement

Implement the user's layout, not the earlier dialog-based proposal:

```text
SOLARSHADE                                         Help / workspace export
[Irradiance & shading] [PV Autonomy]

Source status | selected dates | timezone | panel orientation | View irradiance
+-------------------------+------------------------------------------------+
| DAILY CONSUMPTION       | PV SYSTEM SETTINGS                             |
| Local hour       Wh    | Panel area | Panel efficiency | Conversion     |
| 00:00–01:00      [ ]    | Battery capacity | Initial SoC | Evaluate / Stop|
| 01:00–02:00      [ ]    +------------------------------------------------+
| ...                    | Minimum SoC | Final SoC | Unmet energy          |
| 23:00–24:00      [ ]    +------------------------------------------------+
|                         | Legend: Load demand / PV available / SoC       |
| Paste 24 values         |                                                |
| Fill constant           |    ONE COMBINED TIME-SERIES CHART               |
| Nominal daily total     |    left axis: energy (Wh)                      |
|                         |    right axis: SoC (0–100%)                    |
| independently scrolls  |                                                |
| when required           | Full period / day / first shortfall / details  |
+-------------------------+------------------------------------------------+
Workspace status and actionable error/provenance message
```

Use a left column approximately 260–320 device-independent units wide, with the larger right column taking remaining width. Treat these as initial sizing targets, verified against the existing 1080 × 720 minimum and 1440 × 960 default.

The consumption list is always part of the page, with its own vertical scroll when all 24 entries cannot fit. Do not make editing depend on opening a separate modal. Keep column headings and the daily total/actions available without scrolling the entire application.

Place the five scalar inputs in one or two rows at the top right; wrapping at smaller widths should preserve readable labels. Keep the lower-right chart dominant. A small splitter may help resizing, but enforce usable minimum widths.

The source strip is read-only. Period, timezone, panel tilt and azimuth come from the irradiance study. Changing them belongs in that workspace. Do not duplicate competing controls in PV Autonomy.

## II.2. Consumption editor and system settings

Label the table **Daily consumption**, with **Hour (study time)** and **Consumption (Wh)** columns. Add concise help: **“Repeats each day. Each value is the energy used during that one-hour slot.”**

Support keyboard navigation, select-and-replace, paste of exactly 24 ordered spreadsheet values and a fill-all constant action. Validate paste as a complete transaction before replacing the draft. Invalid text stays visible and blocks evaluation; blanks must not silently become zeros.

The nominal daily total is the sum of the 24 values. A 23/25-hour DST day follows the backend's local-hour repetition semantics rather than forcing its actual total to that nominal sum.

| Input | UI unit/range | Backend mapping |
|---|---|---|
| Panel area | m², nonnegative | `PanelAreaM2` unchanged. |
| Panel efficiency | %, greater than 0 and at most 100 | Divide once by 100 into `PanelEfficiency`. |
| Conversion efficiency | %, greater than 0 and at most 100 | Divide once by 100 into `ConversionEfficiency`. |
| Battery capacity | Wh, positive | `BatteryCapacityWh` unchanged. |
| Initial charge / SoC | %, 0–100 | Divide once by 100 into `InitialSoc`. |
| Daily consumption table | 24 finite nonnegative Wh values | Deep snapshot into `HourlyLoadWh`. |

The initial SoC applies at the start of the entire study, not at the currently zoomed day. Show that start date beside it or in contextual help.

Keep Wh as the first-release battery unit. Ah support would require voltage and extra conversion semantics; it is not an additional backend setting in this plan.

Recommended defaults: persist user-entered settings; provide an explicit example preset rather than implying that an arbitrary capacity or initial charge is measured. Exact first-run defaults remain to be selected.

## II.3. Connect settings, backend results and presentation

Part II implements the PV service boundary established in Part I:

- Reference `SolarShade.PvBattery.Integration` and `SolarShade.PvBattery.IO` from the Windows frontend.
- Consume the accepted `Evaluation.Run` through `PanelBatterySimulation.Compute`.
- Do not recalculate irradiance or re-import the application's workbook for this in-memory workflow.
- Use the existing settings/series validation in addition to source readiness and visible-field parsing.
- Run numerical/export work off the UI thread with cancellation.
- Bind the accepted `BatterySimulationResult` to summaries, chart and details.
- Keep input parsing, immutable request creation, computation and UI rendering as distinct steps.

Returned values must drive presentation directly: `Hours` for the three main series, `Summary` for headline metrics and `Steps` for detailed source/subhour diagnostics if needed. Do not reproduce energy accounting in frontend code.

Settings edits invalidate PV results only. An upstream update makes the source unavailable while running and requires an explicit new PV evaluation after successful completion. No automatic calculation on a keystroke, tab switch or new source arrival.

## II.4. One overlaid chart with two explicit vertical scales

All three requested curves are visible by default on the same shared time axis:

| Series | Backend value | Meaning | Axis |
|---|---|---|---|
| **Load demand** | `BatteryHour.LoadWh` | Requested consumption, including any portion that could not be served. | Left: energy in the interval, Wh. |
| **PV energy available** | `BatteryHour.PvWh` | Electrical PV energy after panel and conversion efficiencies, before any surplus is curtailed. | Left: the same energy scale. |
| **Battery SoC** | `BatteryHour.EndSocPercent` | Stored-energy fraction at the interval end. | Right: fixed 0–100%. |

This implements the requested overlay while keeping quantities dimensionally distinct. Do not place Wh and percent on one undifferentiated numeric scale or independently normalize the load and PV traces.

“PV energy available” is preferable to “energy stored” or simply “harvested”: some available energy may be discarded when the battery is full. Curtailment and served/unmet load remain separate diagnostics.

Rendering requirements:

1. One shared, timezone-aware horizontal axis and crosshair; all three hover values refer to the same returned interval.
2. The energy axis starts at zero and uses one common scale for load and PV. SoC retains 0–100% even when another curve is hidden.
3. Use distinct colors and line styles, an explicit legend and axis labels. Curve crossings across the energy/SoC axes do not signify equality; the hover shows units.
4. Represent load/PV as interval-energy steps or interval bands. Represent SoC as endpoint samples with a connecting guide and an initial point at the study start.
5. Label the chart **Hourly energy and battery charge**; identify partial intervals explicitly in hover/details and show their actual duration. Values remain actual Wh for that interval, not implicitly rescaled W.
6. Mark every interval with unmet load, even if its end SoC has recovered above zero. Make the mark distinguishable from the three main traces.
7. Minimum SoC comes from the backend summary, including initial/substep values. It may be lower than all displayed hourly endpoints.
8. Repeated local hours retain their UTC offsets; partial first/last hours keep their bounds.
9. Full period, day navigation, zoom/pan and first-shortfall navigation only change the view. They never reset or recalculate the battery.
10. Legend toggles are display-only. When all curves are hidden, show a clear empty-chart prompt rather than suggesting missing source data.
11. For long studies, preserve energy peaks, SoC minima and shortfall markers during display reduction. Avoid smooth averaging that hides depletion; exports retain every original row.

Reuse appropriate `ChartTimeAxis` helpers. The existing irradiance renderer assumes `PanelRow` interval means and cannot be reused unchanged for endpoint SoC or dual scales.

Start with a focused WPF `PvSystemChart` using existing drawing conventions. A charting package is an alternative only if it materially reduces the work after checking dual-axis behavior, interval rendering, accessibility, licensing and packaging; no package selection is assumed here.

## II.5. Results, persistence and exports

Headline results: minimum SoC, final SoC and unmet energy. Expandable details can show PV/load/served/curtailed energy, intervals containing shortfall and the first shortfall interval.

An exact zero SoC at a boundary is not automatically an outage. Use unmet-load quantities for the supply assessment. The backend returns the start of the first interval containing a shortfall, not the exact within-interval outage instant.

Persist PV settings separately from upstream `UserSettings` and view preferences. Deep-copy the profile whenever creating an accepted request. Restore drafts on restart without silently declaring old results current.

First-release recommendation: restore settings and require explicit evaluation once a current source is available. If result restoration is implemented, require verified source/settings/software/artifact identity. The backend JSON audit export is not an established result-loading API; its immutable result type needs deliberate handling rather than a naive `CurrentValueCache.Read<T>`.

Publish CSV, JSON and XLSX through the existing library exporters into the optional stage agreed in Part I. Per-file atomic writes are insufficient for a complete three-file stage: stage them together, verify, recheck source/revision and mark complete only after successful publication.

Use contextual **Export irradiance** and **Export PV results** actions. Irradiance export remains valid before any PV evaluation. PV export contains the accepted six settings, source provenance, model assumptions and exact returned values. Its JSON includes the supplied irradiance series for audit.

If combined system export is added, capture matching upstream and PV snapshots and explicitly select their verified artifacts. Stale optional files must never be included automatically.

Preserve the existing one-page irradiance `Summary.pdf`. A separate `BatterySummary.pdf` is an option; changing the existing report into multiple pages is not part of this plan by default.

## II.6. Complete the user-facing workflow

Update Help, source-unavailable messages, terminology, example behavior and packaged documentation. Explain:

- Irradiance first, then PV evaluation.
- Consumption repeats by study-local hour.
- Initial SoC applies at the study start.
- PV and load use the energy axis; SoC uses the percentage axis.
- Model assumptions and conservative shading provenance.
- “No unmet load during this study” is a result for the supplied period, not a guarantee for unseen weather.

Verify the application at 1080 × 720 and 1440 × 960, including 125% and 150% scaling. Ensure all 24 load slots are reachable, top-right fields wrap sensibly and the combined graph retains readable axes.

# Delivery checkpoints and acceptance

## Separate implementation milestones

| Part | Milestone | Completion evidence |
|---|---|---|
| I | A — Inventory and boundary design | Current ownership map, service/source contracts, compile paths and migration plan recorded. |
| I | B — Tabbed shell and extracted irradiance workspace | Existing workflow works inside its tab; no duplicate subscriptions or service instances. |
| I | C — Independent state, source readiness and optional-result storage | A test dependent workspace can observe invalidation; optional failures cannot invalidate upstream success. |
| I | D — Regression checkpoint | Existing numerical/manual-update/export/packaged behavior preserved; architecture ready for PV integration. |
| II | A — Inline load table and top-right settings | All six inputs validated, persisted and converted correctly; no edit triggers calculation. |
| II | B — Service/backend integration | Direct backend and UI-driven requests return identical values; cancellation and stale-result rejection verified. |
| II | C — Three-series combined graph and summaries | Shared-time alignment, dual axes, partial/DST intervals and shortfalls rendered correctly. |
| II | D — Managed publication and export | Complete matching snapshots only; recovery and locked-file failures handled. |
| II | E — Packaged acceptance | Full two-tab workflow, examples, Help, restart and exports pass review. |

Use separate implementation PRs for Part I and Part II, with smaller commits inside each. Part I is the dependency of Part II; do not describe the tabbed-shell checkpoint as completion of the PV feature.

The planning PR remains documentation-only. Reassess exact effort after Part I's inventory; the work includes lifecycle and data ownership, not just UI styling.

## Part I acceptance tests

- Existing irradiance numerical values, explicit-update behavior and export content remain unchanged.
- Moving files into folders preserves compile inclusion and excludes test/generated sources.
- Switching tabs repeatedly preserves drafts/results and causes no weather fetch, native processing or export.
- One workspace/service instance and one source subscription exist per session; closure disposes them safely.
- State/readiness is service-owned and can be tested without inspecting a visual label.
- Missing, failed, stale, truncated or invalid shaded source blocks the dependent workspace; valid zero irradiance is accepted.
- Source changes in place and effective timezone changes invalidate the correct downstream work.
- A fake dependent operation finishing after invalidation cannot publish current results.
- Optional-result failure or missing output leaves valid upstream completeness/export intact.
- Manifest/settings migration, interrupted publication and a second app instance preserve existing storage guarantees.
- Battery-only software identity changes do not unnecessarily invalidate upstream scientific caches.
- A test workspace can register with the shell and consume a source snapshot without accessing irradiance controls. This tests extensibility without implementing an optimizer.

## Part II acceptance tests

- Every displayed/exported quantity agrees with direct backend calls and the frozen reference fixture.
- Exactly 24 nonnegative load values are required; zero is valid, blanks are not silently accepted.
- Invalid paste does not partially replace the profile; keyboard editing works in the inline list.
- Uncommitted invalid text immediately disables evaluation/export as appropriate.
- Percent-to-fraction conversion happens once; capacity/load stay in Wh.
- Battery edits preserve upstream file bytes and expensive preparation counters.
- Duplicate clicks start one evaluation; Stop/source edits/settings edits reject late completion.
- PV availability follows all relevant irradiance changes, not irrelevant display preferences.
- Load demand shows requested energy even when unmet; PV shows available generation before curtailment.
- Both energy traces share a scale; SoC remains 0–100%; legend toggles do not change results.
- Partial hours and DST transitions retain correct interval energies, timestamps and offsets.
- Initial SoC is plotted at study start; day navigation does not reset it.
- Zero SoC without unmet load is distinguished from actual shortfall.
- Subhour depletion/recovery is still flagged even if hourly endpoints are positive.
- Long-period display reduction does not hide minima, peaks or shortfall intervals.
- Locked files, export interruption and restart never expose mixed or partial data as current.
- Irradiance-only export excludes stale optional PV files; combined export, if implemented, rejects mismatched identities.
- The inline table, top settings and chart work at minimum size/scaling with keyboard-accessible controls and non-color status messages.
- Packaged first launch, example loading, both explicit calculations, restart and export work with the added project dependencies.

The backend integration baseline recorded 460 passing tests, including 65 PV–battery tests. Re-establish the current baseline when implementation begins; this planning revision does not constitute a new test run.

## Decisions remaining for discussion

The two implementation parts, top tabs, left consumption list, top-right settings, combined three-series chart and valid-irradiance dependency are now recorded as the selected direction.

| Remaining choice | Recommendation |
|---|---|
| Final public name | PV Autonomy tab; PV System Autonomy heading and plain-language subtitle. |
| Extent of presentation rewrite | Extract programmatic WPF views/controllers; defer a wholesale MVVM/XAML conversion. |
| Unavailable-tab explanation | Visible unavailable tab/reason; block working controls. An explanatory page may remain selectable if preferred. |
| Optional artifact storage | Same root/owner with explicit completeness scopes; revisit separate-root alternative during Part I if necessary. |
| First-run values | Blank real-study inputs plus explicit example preset; preserve user settings across upstream updates. |
| Restarted results | Restore settings first; restore results only through verified identity-aware logic. |
| Report scope | Data exports initially; separate PV recap if required, preserving the existing irradiance PDF. |

## References

- [PV–battery backend model, API and units](../src/PvBatterySimulation/README.md)
- [Backend validation record](../src/PvBatterySimulation/VALIDATION.md)
- [Existing manual-update and export specification](MANUAL_UPDATE_AND_EXPORT_PLAN.md)
- [Application architecture](APPLICATION_ARCHITECTURE.md) — some historical lifecycle prose predates the manual-update milestones; current code and implemented behavior take precedence.
- [Frontend running and packaging guidance](../src/ApplicationFrontend/RUNNING.md)

This revision updates the implementation plan only. The next discussion can refine the remaining choices without reopening the user-selected layout or combining the two implementation parts.
