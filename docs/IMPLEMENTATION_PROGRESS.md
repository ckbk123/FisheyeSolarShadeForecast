# Application improvement milestones

Overall goal: explicit calculations, one valid current debug dataset, and complete snapshot exports with a one-page PDF recap. Scientific library source and behavior remain unchanged. Baseline: `baseline-2026-09-27`.

## 1. Manual updates and status indicator — ready for review

- Replaced Calculate with Update results and retained an explicit Refresh data & update action.
- Removed calculation/orientation timers and startup/example-triggered calculations.
- Added a bottom-left color-and-text indicator for attention, pending edits, running, successful, stopped and failed states.
- Text edits immediately invalidate interface results and disable export. Invalid fields and missing required inputs prevent Update.
- Green/export require a successful shaded result; baseline-only results do not qualify.
- Duplicate updates are rejected. Stop and edits reject late progress, preview and completion callbacks; Update waits for active work to finish.
- Previous graph and summary values are visibly stale; display-only toggles remain immediate.
- Updated help, example instructions and the real-application smoke harness for explicit updates.

Validation on Windows x64: **313 tests passed** (306 existing plus 7 UI tests). New coverage includes 100 edits without processing, draft text, invalid ranges, duplicate clicks, refresh while importing, stop/edit during deliberately blocked work, failure/retry, incomplete results, missing inputs, Load example, and automatic system time-zone changes. The real-data WPF smoke test passed with model inference, sun paths/cardinals, explicit panel/diffuse/camera updates and exports; startup and completed-state screenshots were visually inspected. Scientific library source has no diff from the baseline.

This milestone does not claim artifact-level invalidation, stable current-folder storage, source-file monitoring or the new export service/PDF. Existing service-level orientation and baseline APIs remain available; the interface no longer invokes them automatically.

## 2. Dependency invalidation — pending acceptance of milestone 1

Use one dependency model to invalidate the affected previews/files and export eligibility, including source changes in place. Preserve valid independent artifacts; perform removal only once per stale transition.

## 3. One current debug dataset — pending

Replace historical debug folders with stable stage paths; protect durable calibration inputs and implement migration, consistent publication and cancellation/recovery rules.

## 4. Complete export and PDF — pending

Validate complete current artifacts at the service boundary, freeze one export snapshot, copy its debug files and add a one-page settings/results recap. Finish with full acceptance and packaged-Windows verification.

Each milestone has a focused branch and pull request. User acceptance is required before merging and advancing to the next milestone.
