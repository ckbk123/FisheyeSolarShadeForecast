# Application performance audit and fix plan

Date: 30 September 2026. Audited source: `901dc62` (PV frontend plus startup-cleanup hotfix). Status: **investigation and proposed implementation plan; no performance changes installed**.

## Findings and recommended order

The user's report concerns **calculating/updating irradiance**. The strongest installation-specific finding is repeated scanning of historical diagnostic folders. Beyond that, the application spends substantially more time serializing, validating and exporting data than calculating solar geometry or panel irradiance.

1. **P0: remove historical-folder discovery from routine input preparation.** A replica of this installation's 126 historical folders took a median **5.82 s per repeated call**, versus **1–2 ms** with no historical folders. Multiple calls occur during one update, and the UI invokes the same operation on edits and activation. This also exposes a flaw in the startup hotfix: cleanup failures are deferred, but directory classification still happens before checking that deferral.
2. **P0: make expensive validation/publication asynchronous and measure the entire user operation.** An unchanged annual update blocked the dispatcher for **1.19–1.52 s**, while the app's internal timer reported **0.16–0.19 s**. Existing `Task.Run` calls cover many computations, but preparation and continuations still run synchronously on the UI thread.
3. **P1: reject stale cache entries before deserializing their payloads.** For an annual dataset, reading the wrong panel cache costs **2.48 s and 523 MiB of managed allocation** before returning a miss. A panel edit causes this useless work even though the old key is already known not to match.
4. **P1: reduce diagnostic serialization cost without losing research evidence.** Annual solar workbooks take **8.64 s** to write; solar computation takes **0.053 s**. Mandatory diagnostic work and cache writes delay both displayed results and downstream availability.
5. **P2: optimize chart drawing, memory use and cold mask startup.** These matter for responsiveness and older PCs, but they do not explain the repeated historical scans or cache misses.

Do not begin by adding threads everywhere or reducing the scientific sampling rate. The current defaults of 60 samples per interval, 64 solar-disk rays and 32,768 diffuse-dome samples remain unchanged in this proposal's first fixes.

## Evidence and limits

The audit ran sequential Release-mode probes on the user's Ryzen 7 5700X (8 cores / 16 logical processors), approximately 64 GiB RAM, RTX 3070. The harness uses the real frontend service and libraries, with an STA dispatcher and a 16 ms heartbeat. It writes to isolated audit folders and does not use the installed app's mutable data store.

- **Month:** bundled photograph, calibration and imported irradiance; 696 hourly intervals, 60 quadrature samples per interval. The service benchmark uses W. Europe Standard Time, matching the saved study at audit time.
- **Year:** 8,760 synthetic hourly intervals, 60 samples each, same photograph/calibration, UTC. Synthetic values exercise performance; they are not measured weather or an energy prediction.
- **Historical-folder replica:** copied manifest/directory topology from the installation, 126 run folders and 1,582 non-manifest files. File contents are small placeholders, and fixture roots are read-only. No personal diagnostic files were modified. This isolates enumeration/classification rather than historical file size.
- Separate stage probes generally have three repetitions; tables show their medians. First pipeline runs include cold model/session and runtime initialization; repeat updates retain process caches.
- Stage probes rerun functions independently. Their timings explain costs but are **not an additive, perfectly exclusive trace** of the full update. Managed allocation is cumulative allocation, not live or peak RAM.
- Chart probes include offscreen WPF layout and rasterization at 1,100 × 500. They identify drawing costs, not measured interactive FPS.
- CPU mask tests use two ONNX threads on this modern CPU. They are a constrained CPU test, **not an emulation of an older PC**.
- Filesystem results depend on storage/synchronization activity. The main probes use the OneDrive workspace. No controlled OneDrive on/off comparison was made.

Raw measurements are committed in [2026-09-30-measurements.json](performance/2026-09-30-measurements.json). The repeatable harness is [tools/PerformanceAudit](../tools/PerformanceAudit/README.md).

### Older/current packaged executable check

Both existing packages completed their built-in smoke checks using clean local temporary output folders, identical example settings and 696 intervals. Executable hashes and settings are in [the comparison receipt](performance/2026-09-30-packaged-comparison.json).

| Packaged operation | Older `Deliverable` (before tabs) | Current `Deliverable-rewritten` |
|---|---:|---:|
| First service calculation | 6.339 s | 6.109 s |
| Panel-edit service calculation | 1.769 s | 1.602 s |
| Panel-edit click-to-completion smoke measurement | 3.330 s | 3.248 s |
| UI ready | 0.922 s | 0.900 s |

This single passing pair is not statistically sufficient to establish a speedup or rule out all regressions. It shows no large tab-related regression in a clean installation and distinguishes that case from the 126-folder installation. Both binaries predate or include substantial diagnostic machinery; this comparison does not establish parity with the much earlier September 6 app.

Two failed comparison attempts were excluded: an export directory rename was denied in the synchronized workspace, and a subsequent setup placed Debug Data underneath the requested PV export parent, correctly triggering the managed-folder guard. The final passing run used separate debug/export locations on local temporary storage. These failures are not timing observations or proof that OneDrive caused the user's slowdown.

### Complete service operations

These start at `AppServices.Evaluate`, including preparation, but exclude the outer window's initial/final rechecks and final chart binding. The historical-folder case is measured separately; do not present the clean-store timings as the installed application's click-to-ready timings.

| Operation | 696 intervals | 8,760 intervals |
|---|---:|---:|
| First update, empty audit caches | 6.60 s | 21.93 s |
| Unchanged update, median of 3 | 0.374 s | 1.335 s |
| Panel-angle edit/update, median of 3 | 1.280 s | 11.337 s |
| Longest dispatcher gap during unchanged update | 0.408 s | 1.517 s |
| Annual first-update managed allocation | — | 4.86 GiB |
| Annual panel-edit managed allocation | — | about 2.43 GiB |
| Final diagnostic dataset on disk | 23.3 MiB | 286.0 MiB |

The annual process reached about 1.18 GiB peak working set across the complete pipeline and repeated diagnostic probes. This is not a minimum RAM specification. Cache-size totals in the raw results include extra probe caches and must not be treated as the production cache footprint.

### Module measurements

| Component / operation | Month median | Year median | Assessment |
|---|---:|---:|---|
| Import source XLSX / synthetic CSV | 5.6 ms | 86.4 ms | Small in these inputs; large external XLSX remains untested |
| Solar interval geometry | 4.3 ms | 53.0 ms | Fast; already uses bounded parallelism |
| Solar diagnostic workbooks | 600.6 ms | 8,636.8 ms | Largest measured annual output cost |
| Sun-path generation | 294.1 ms | 379.2 ms | Full image projection/raster output; reused for panel edits |
| Cardinal overlay generation | 304.7 ms | 295.9 ms | Mostly image-sized cost; independent of study duration |
| Visibility-scene construction | 13.2 ms | 15.5 ms | Low priority; preserve reuse |
| Panel transposition calculation | 3.9 ms | 67.9 ms | Much cheaper than its cache/output |
| Panel diagnostic workbook | 163.9 ms | 2,162.5 ms | Significant repeated panel-edit cost |
| Shading, fresh direction cache | 56.0 ms | 851.9 ms | Real compute cost, but below output/cache overhead |
| Shading, reusable directions | 11.7 ms | 276.9 ms | Preserve reuse across panel changes |
| Shading XLSX/JSON output | 133.4 ms | 1,513.0 ms | Significant repeated output cost |
| Panel cache read / reject | 189.3 / 185.2 ms | 2,614.4 / 2,480.5 ms | Miss costs almost as much as a hit |
| Solar cache read / reject | 81.6 / 89.4 ms | 1,287.3 / 1,343.3 ms | More expensive than recomputing geometry |
| Shading cache read / reject | 55.4 / 52.3 ms | 871.8 / 954.6 ms | More expensive than warm direction-based shading |
| Panel cache write | 171.5 ms | 2,263.4 ms | Repeated on every changed panel |
| Solar cache write | 85.5 ms | 1,112.7 ms | Stores a large object graph |
| Shading cache write | 41.2 ms | 463.4 ms | Includes substep visibility |
| Full source/artifact recheck | 65.0 ms | 489.4 ms | Currently reachable from the UI thread |
| Readiness inspection / snapshot | 25.6 ms | 77.9 ms | Extra metadata traversal and row serialization |
| Battery numerical calculation | 1.7 ms | 15.8 ms | Not a priority bottleneck |
| Battery CSV/JSON/XLSX output | 24.6 ms | 129.2 ms | Larger than battery math, still relatively small |
| Irradiance chart offscreen render | 51.0 ms | 544.1 ms | Long-period drawing needs investigation/optimization |
| Battery chart offscreen render | 131.9 ms | 119.5 ms | Pixel-envelope path helps at larger row counts |

Calibration, including its diagnostics and frontend storage, took **1.66 s cold, 1.11 s and 1.07 s repeated** for the 12 bundled photographs. It is an explicit operation and is not rerun by an ordinary irradiance update.

Mask generation on the first month run used DirectML on the RTX 3070: **2.14 s total**, of which **1.77 s** was session creation and **0.166 s** inference. On CPU with two threads, B5/1024 took **3.80 s cold and 3.35–3.44 s warm**. B5/512 took **1.24 s cold and 0.89–0.93 s warm**. The latter changes mask resolution and therefore requires an explicit quality choice; it is not a numerically identical optimization.

## Why the current structure produces these delays

### Historical data: a maintenance operation in the editing path

`DebugDataStore.PrepareInputs` enumerates root folders and runs `IsLegacyRun` before consulting `deferredCleanup`. Classification parses manifests and recursively checks files. `SafePath` repeatedly checks ancestors of each entry. The same historical tree is revisited by `IrradianceWorkspace.Changed`, `AppServices.Evaluate`, and the `DebugDataRun` constructor. `Begin` also rechecks before and after evaluation. Thus several independent scans can accumulate within one click; their sum has not been measured as an installed-window end-to-end trace.

The isolated prototype changed only the classification predicate to reuse a previously deferred classification during the store's lifetime. Repeated-call median fell from **5,815 ms to 364 ms** (about 94%); first calls remained **10.84 s and 11.42 s** respectively. This is evidence for the direction, not a finished implementation: even the remaining top-level scan is too expensive for routine input editing. The prototype was reverted and was never installed.

### UI sequencing: asynchronous computations with synchronous preparation

`Evaluate` is awaited directly from the dispatcher. Its preparation, source hashing, debug-run construction and publication bookkeeping execute synchronously before/between background computations. On unchanged updates, many awaited operations complete immediately, so the entire service call can occupy the UI thread. The internal stopwatch begins after preparation and stops before final cleanup/outer UI checks, understating the user's wait.

`ObserveInputs(...verifyArtifacts: true)` hashes the published files. Both the service's initial observer and the new `DebugDataRun` validate the previous dataset. Source identities are also captured repeatedly. `RecordInput`, `BeginStage`, `CompleteStage` and `Origin` each write a manifest through `Status`; replacement retries can sleep synchronously. The final readiness and export-button checks revisit file metadata. These protections are useful, but their placement and duplication are costly.

### Caches: reading an old result before deciding it is old

`CurrentValueCache.Read<T>` decompresses and deserializes `Entry<T>` before checking its key/software identity. The filename is stage-based, so changed panel settings load yesterday's payload and then discard it. Annual rejected panel payloads allocate about **523 MiB**. Its transposition payload embeds the solar timeline as well as per-row samples, duplicating related data in serialization. Solar payloads serialize both source and selected samples even when those share the same array in memory.

On an annual panel edit, rejected panel/shading cache reads cost roughly **3.44 s** in the isolated probes, replacement writes about **2.73 s**, and panel/shading output about **3.68 s**. These costs explain most of the measured 11.34 s update. They are separate probe medians, not an exact decomposition.

### Diagnostic output: millions of cells per year

At 60 samples per hour, annual solar geometry contains 525,600 samples. The exporter writes both source and selected domains: **1,051,200 detail rows and about 15.8 million cells**, even when the domains are identical. This is in addition to 525,600 transposition rows and 525,600 visibility rows. Solar export alone allocates about **2.89 GiB** cumulatively.

`ScientificWorkbook` already streams XML into ZIP entries; replacing it with another nominally streaming library is not automatically an improvement. Allocation comes from boxed `object[]` rows, chunk materialization, cell-coordinate strings and repeated timestamp/numeric formatting. Some exporters materialize chunks before writing. Full object-graph cache serialization adds another layer of work.

### Scientific kernels, providers, PV and charts

- Solar computation is already parallel and cheap. Transposition is serial but cheap relative to I/O. Shading's ray projections and direction cache deserve attention only after the larger overhead is removed.
- Provider requests are asynchronous and bounded, with timeout/rate-limit handling. No live NASA/Open-Meteo timing was measured: the user's current example uses an import, and the reproduced lag does not require network access. The frontend's cached-weather import and post-download cache export still have synchronous work to move away from the dispatcher.
- Refresh currently forces downstream recomputation. The dataset fingerprint includes `FetchedUtc`, so identical physical values fetched again can have a different identity. Separate numerical content identity from retrieval provenance before attempting finer reuse.
- PV evaluation does not run merely because the tab exists. Its readiness checks add some overhead, and optional-result publication/export can acquire shared locks and rehash upstream files. PV draft changes also synchronously save settings and invalidate optional outputs. Review these paths, but they are not the main irradiance calculation bottleneck.
- Irradiance rendering draws every interval at every scale; the comment claiming inexpensive year-long views needs a performance regression test. PV uses pixel envelopes for long periods, but filters/groups rows and redraws all series on pointer movement. Both should cache view geometry and keep cursor/tooltip updates separate.
- Startup includes native-bundle/model extraction, directory recovery and initial UI construction. Do not confuse first model-session creation with recurring calculation time. Packaging/native loading is not invoked afresh for every panel edit.

## Implementation sequence

### PR 1 — Remove repeated migration scans and add truthful timings (P0)

Separate protecting selected inputs from optional historical cleanup. Resolve only selected paths during normal editing; maintain an explicit inventory of owned historical roots and completed/deferred migration state. Run bounded discovery/cleanup outside the UI thread. New/changed historical roots must be handled deliberately; do not simply ignore every legacy-looking folder forever. Never remove read-only attributes, unknown files or user exports to obtain speed.

Add operation IDs and timings from button click through validation, queue wait, computation, cache read/write, artifact serialization/hash/publication and UI binding. Include cache hit/miss/rejection reason, row/sample counts, bytes and dispatcher gaps. Keep detailed tracing opt-in and local. Show meaningful phase progress rather than reporting a partial duration as total calculation time.

Tests: reproduce 126 read-only historical roots; selected profiles/imports inside historical roots must remain safe, including newly selected files after the first scan. Cover newly created roots, changed manifests, unknown files, locked files, process restart, interrupted migration, cancellation and concurrent app ownership. Assert no recursive historical scan on ordinary repeated edits/updates. Initial discovery must not block the dispatcher.

### PR 2 — Remove blocking verification and redundant work (P0)

Give the service a background operation boundary for preparation, cache/weather I/O, hashing and publication, not just numerical kernels. Publish progress/results to the dispatcher explicitly. Preserve lock order and use immutable settings/generation tokens so an edit immediately makes results stale without waiting for large file operations. Do not hold UI-needed locks across workbook generation or bulk hashing.

Coalesce source checks into one validation transaction at an action boundary. Retain full integrity checks where required; reuse only evidence from that same protected source/version. Watcher notifications invalidate the appropriate generation, and explicit action-boundary validation must still catch missed events and same-name file replacement. Coalesce repeated settings saves and make retry delays asynchronous.

A verified unchanged update should reuse the accepted dataset without rewriting every stage manifest or replacing its source identity. Preserve valid PV results when the scientific source is genuinely unchanged. Forced refresh remains a distinct operation.

Tests: editing/stopping while verification is pending, source replacement with unchanged filename/size/timestamp, missing/tampered output, missed watcher events, exceptions, second-instance locks, restart, source changes during export, and stale results rejected after every asynchronous boundary. Verify that cancellation feedback is immediate even where native work finishes later.

### PR 3 — Fix cache misses and reduce cache payloads (P1)

First check a bounded metadata header containing key, schema and library version; reject a mismatch without parsing the large value. Use a single atomic cache envelope or a carefully committed header/payload pair so mismatched metadata cannot authorize unrelated bytes. Validate payload integrity when accepting a hit.

Benchmark whether cheap solar/transposition stages should be recomputed instead of read from disk. Prefer normalized, compact representations that avoid repeating the same timeline in several caches. Keep in-memory direction reuse. Consider a compact binary representation only after comparing it with simpler JSON changes; adding a storage dependency is not required for the first improvement. Bound retained cache entries rather than creating an unlimited file per panel angle.

Split numerical content fingerprints from retrieval timestamps/provenance. Refresh may update provenance without recomputing numerically identical stages, provided source verification still succeeds.

Tests: corrupted/truncated caches, old software/schema, key mismatch, interrupted writes, time-zone and interval metadata, clipped source domains, changed panel/model/calibration, and exact numerical agreement between uncached, memory-cached and disk-cached paths. Add a mismatch test proving payload deserialization did not happen.

### PR 4 — Reduce diagnostic output cost while preserving research traceability (P1)

Implement typed streaming rows, reusable column references/formatting, and bounded sheet/file chunking without retaining all row arrays. Preserve existing exported values, units, timestamps, integration weights and split-file completeness. Where source and selected domains coincide, evaluate an explicit shared-domain representation; this is a schema change and requires versioned readers/documentation.

There are two product options:

| Option | Benefit | Contract impact |
|---|---|---|
| Keep all current workbooks mandatory, optimize their writers | Lowest behavior risk; immediate first implementation | Full completion/PV/export gating stays as it is |
| Store a complete compact scientific snapshot, materialize detailed Excel output on request/in background | Removes millions of formatted cells from interactive updates | Requires separate computational readiness and diagnostic/export readiness; full research export must remain complete and verified |

Recommendation: begin with the first option. Prototype the second for review because it offers larger gains, but do not silently remove automatic diagnostics or weaken current completeness checks. An optional fast mode must say what is pending and prevent an incomplete export from being represented as complete. Persist enough exact substep data to produce research outputs without changing the calculation or depending on later source files.

Tests: compare decoded workbook cells and JSON fields with the current schema; ZIP bytes may differ harmlessly. Include annual and finer-cadence split-file limits, cancellation, disk-full/write failures, partial output recovery and reproducible full exports. Confirm that any optional diagnostic mode does not change irradiance, visibility or battery values.

### PR 5 — Charts and lower-end execution (P2)

Cache chart geometry by result identity, selected range, component, size and DPI. Use pixel envelopes preserving minima/maxima and shortfalls for long-period views; keep full-resolution values for hover/export. Binary-search visible bounds, cache axes, freeze reusable drawing resources and separate crosshair drawing from series geometry. Retain interval edges, gaps and DST behavior. Do not average away short peaks or depleted periods.

Measure mask session creation, image decode and inference separately; retain sessions/caches with bounded memory. Expose the existing 512/1024 tradeoff clearly if a fast mode is desired. Do not automatically change segmentation quality. Profile shading allocation and bounded parallelism only if it remains significant after PRs 1–4. Keep cancellation and memory limits more important than saturating every core.

Tests: graph extrema/gaps/DST/partial intervals, unchanged scientific outputs on navigation, responsive long-period pan/zoom/hover, hidden-tab inactivity, GPU absence/fallback, memory-pressure scenarios and repeated panel edits without unbounded direction/cache growth.

## Acceptance targets and verification matrix

These are **proposed engineering targets, not achieved improvements**. Record hardware, input hashes, build, storage and cold/warm state with every run. Use at least five repetitions for implementation acceptance, reporting median and P95; investigate outliers rather than silently discarding them.

| Gate | Proposed target on reference PC |
|---|---|
| Repeated input preparation with 126 historical roots | under 50 ms; no recursive scan per edit/update |
| Verified unchanged update | under 250 ms monthly / 750 ms annual, with expensive validation off dispatcher |
| Dispatcher during update/validation | P95 scheduling gap under 50 ms; no application-caused stall over 100 ms |
| Cancel/edit acknowledgement | under 100 ms, even if native completion is deferred |
| Wrong-key cache lookup | under 10 ms, under 1 MiB allocation, no payload deserialization |
| Panel edit with full current diagnostics | under 1 s monthly / 5 s annual after cache and writer work |
| Long-period chart interaction | P95 below 33 ms/frame, measured in the actual application |
| Annual update memory | reduce managed allocation by at least 50%; keep peak working set under 1 GiB in the agreed isolated benchmark |

Run 1 day, the 29-day example, a synthetic full year, and a multi-year/finer-cadence stress case. Exercise cold first launch, first update, unchanged update, panel-only edit, camera-pose edit, date/site edit, model change, identical-data refresh, changed-data refresh, process restart, PV evaluation, and full export. Repeat with no historical data and the 126-folder fixture, and with local versus synchronized output locations.

For older-system sign-off, use a real 2–4-core machine with 8 GiB RAM and no discrete GPU; also consider an HDD if supported. Require the same responsiveness/correctness gates, no paging-driven runaway, and record compute timings as a separate hardware-specific baseline. Two-thread tests on this PC do not satisfy that sign-off.

Run the existing frontend, scientific and PV suites after each relevant PR, adding targeted integrity/concurrency tests rather than brittle unit-test wall-clock limits. Keep performance acceptance in a dedicated benchmark job. Package the selected fixes and verify **the user's actual `Deliverable-rewritten/APPLICATION.exe` with existing saved data**; a clean test folder alone missed the historical-folder problem before.

## Scope and remaining uncertainty

The audit covers the main window/workspaces, source watchers/readiness, app services, historical/current/dependent stores, caches, weather/import transport, calibration/detection/solver, camera projection, mask inference, solar geometry, overlays, transposition, shading, workbook/JSON transport, PV simulation/output, charts, exports and packaging. Every subsystem has been reviewed; not every branch or provider has a measured trace.

Live provider latency, disk-full behavior, truly old hardware, multi-year studies, and high-frequency pointer interaction are pending acceptance work. Full exports perform several integrity passes and take shared locks; that is a reviewed risk, not a separately measured export-only bottleneck. Avoid attributing all wall time to OneDrive, GPU performance or the PV tab without evidence.

Historical smoke receipts show panel-update service times around 0.09–0.10 s on 6 September, 1.0–1.7 s in mid-September, and 1.5–3.0 s before the PV tabs. Those runs span different functionality/cache states and are not a controlled speedup/regression ratio. A Git comparison from `dfebfb0` to `901dc62` shows no changes in the existing scientific library source directories. The recent changes are concentrated in orchestration, storage/validation and frontend behavior. The audit therefore prioritizes those measured costs rather than assuming the scientific algorithms became intrinsically slower.
