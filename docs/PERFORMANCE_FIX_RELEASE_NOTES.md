# Performance fixes - Preview 0.1.1

Implemented on `codex/performance-fixes`, based on the approved performance audit. The changes are delivered in one implementation PR with separate commits for pipeline/storage, scientific exports, charts, and packaging. No scientific formula, integration sample count, mask resolution, or mandatory diagnostic was removed to obtain these results.

## Measured effect

Same Ryzen 7 5700X / RTX 3070 machine and Release harness as the 30 September baseline. Measurements use the OneDrive workspace. Month: 696 source intervals; year: 8,760 synthetic hourly intervals; 60 integration samples per interval. Warm figures are medians of three repetitions; first updates are one run each, not a statistical guarantee.

| Operation | Before | After |
| --- | ---: | ---: |
| Prepare inputs with 126 historical folders, repeated median | 5,815 ms | 1.02 ms |
| Month first update | 6.60 s | 5.51 s |
| Month unchanged update | 374 ms | 75 ms |
| Month panel edit | 1.28 s | 0.69 s |
| Year first update | 21.93 s | 16.50 s |
| Year unchanged update | 1.34 s | 0.47 s |
| Year panel edit | 11.34 s | 3.95 s |
| Year solar diagnostic workbooks | 8.64 s | 5.02 s |
| Year transposition workbook | 2.16 s | 1.39 s |
| Year shading outputs | 1.51 s | 0.91 s |
| Year irradiance chart, initial offscreen render | 544 ms | 72 ms |

Wrong-key cache probes now read a bounded header, typically under 1 ms and approximately 8 KiB allocated, rather than decompressing hundreds of MiB. The annual first update allocated about 547 MB instead of approximately 5.2 GB; panel updates allocated about 237-253 MB instead of approximately 2.6 GB. Process allocation totals are not retained heap size.

The annual service-update dispatcher heartbeat had no gap over 100 ms; its largest recorded gap was 49 ms. This isolates the service boundary and does not establish a live UI frame-time percentile. Peak working set through the annual application update sequence stayed below 1 GiB. The complete benchmark reached 1.24 GiB because it subsequently loads deliberately duplicated standalone cache probes, including the obsolete panel cache that the application no longer uses.

Raw evidence: [measurements](performance/2026-10-01-fix-measurements.json), [baseline](performance/2026-09-30-measurements.json). Timing can vary with disk, synchronization, antivirus activity, GPU and CPU. This PC is not a substitute for evaluating an older 2-4 core / 8 GB PC.

## Implementation decisions

- Normal startup, input editing and calculation no longer enumerate historical diagnostic folders. Selected inputs under managed storage are still copied to durable input storage first. History is retained; `DebugDataStore.CleanHistory` is an explicit maintenance API with the existing ownership checks and graceful handling of locked/read-only files. It is not automatically invoked by the UI.
- The entire evaluation, including preparation, content verification, publication and cleanup, runs on a worker. UI changes immediately mark results stale and cancel acceptance of late work. Ordered background observers preserve invalidation across rapid edits and reversions. Source checks are coalesced while pending; a notification received during a calculation gets a final check before completion.
- A fully verified unchanged update keeps its run identity and diagnostic files. Existing PV results survive that no-op update. Refresh remains an explicit forced operation. Export and action boundaries retain content checks, including same-name/same-timestamp replacements.
- PV invalidation cleanup and UI publication verification run in the background. UI generation guards reject late results. Readiness snapshots are built on the worker and reused, while live source/artifact validity remains checked.
- Cache v2 stores a bounded identity/software header and a checksum of the compressed payload. It rejects mismatched keys before reading payloads. Solar payloads share equal source/selected sample sets and reference dataset intervals by position. Clipped domains remain separate. The costly transposition disk cache was removed; its much cheaper computation and in-memory reuse remain.
- Typed streaming row writers retain every existing sheet, column, timestamp, value and split-file convention. Solar and shading exporters no longer retain 100,000 boxed row arrays. Cancellation and failed writes preserve the previous destination.
- Both charts retain one immutable drawing for the current view. Long irradiance views use pixel envelopes retaining minima, maxima and gaps; zoomed views and hover use the original intervals. This display reduction never changes calculations or exports.
- Optional local tracing uses `SOLARSHADE_PERFORMANCE_LOG`. It records operation, input verification, cache and stage timings with process-wide allocations. Stage spans include their output work; timings are not independent additive CPU measurements.

## Validation and boundaries

All 504 solution tests passed, including CPU model fallback, calibration, irradiance, shading, exports, battery simulation and frontend integration. The final frontend suite contains 175 tests. New regression coverage includes cache identity rejection/corruption, clipped timeline round trips, verified no-op updates, tampered output repair, immediate edits while diagnostic storage is deliberately blocked, dense-chart extremes/gaps, preserved input paths when Update is clicked before background preparation finishes, and workbook cell parity under a non-English culture.

The numerical workbook comparison tool independently compares decoded cells in all solar, transposition and shading workbooks against the pre-fix month and year outputs. All 29 workbooks matched exactly: 1,998,139 month cells and 25,137,956 annual cells. [Month evidence](performance/2026-10-01-month-workbook-parity.json) and [annual evidence](performance/2026-10-01-year-workbook-parity.json) are stored beside the performance measurements. ZIP bytes and build/provenance metadata are intentionally not numerical-equality criteria.

The plan's month/year update targets and history-scan target are met on this machine. The 33 ms live chart interaction target is not established: the annual initial offscreen render median is 72 ms, and a real interactive/older-PC evaluation is still needed. Cached redraws avoid rebuilding data/labels; initial rendering remains a cost. Calibration and mask quality settings are unchanged.

The proposed on-demand diagnostics mode and shared-domain workbook schema were alternatives, not selected for this release. Full diagnostics still complete before PV/export readiness. Retrieval timestamps remain part of provenance, and forced refresh deliberately recomputes; numerical-only refresh reuse is deferred to avoid changing refresh/provenance behavior in the first colleague preview. Settings persistence remains on field commit/explicit actions rather than every text keystroke.

## Distribution

Preview 0.1.1 is a self-contained Windows x64 package with the four original model resources, the bundled example, both Solar Irradiance and PV Autonomy, and a short recipient guide. The first model use extracts its resource into local Data. Updating an installed executable preserves the personal Data/settings.json. The distributable ZIP is made from a clean stage, never the developer's saved session.

The shipped executable identifies source commit `14a386c4c3089084b6aea39c3f88bac2ac51cb13`. Normal GPU selection, forced CPU execution, and a clean ZIP extraction launched from a different working directory all passed the packaged end-to-end checks, including both workspaces and PV/backend agreement. All four bundled models were extracted and hashed. Both installed copies match the ZIP executable; both settings files remained byte-identical, and previous executables were backed up. See [packaged validation receipt](performance/2026-10-01-packaged-validation.json). The final documentation commit records these checks without changing the compiled source.
