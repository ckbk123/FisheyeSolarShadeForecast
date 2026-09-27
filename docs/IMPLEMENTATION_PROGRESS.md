# Application improvement milestones

Overall goal: explicit calculations, one valid current debug dataset, and complete snapshot exports with a one-page PDF recap. Scientific library source and behavior remain unchanged. Baseline: `baseline-2026-09-27`.

## 1. Manual updates and status indicator — accepted and merged

- Replaced Calculate with Update results and retained an explicit Refresh data & update action.
- Removed calculation/orientation timers and startup/example-triggered calculations.
- Added a bottom-left color-and-text indicator for attention, pending edits, running, successful, stopped and failed states.
- Text edits immediately invalidate interface results and disable export. Invalid fields and missing required inputs prevent Update.
- Green/export require a successful shaded result; baseline-only results do not qualify.
- Duplicate updates are rejected. Stop and edits reject late progress, preview and completion callbacks; Update waits for active work to finish.
- Previous graph and summary values are visibly stale; display-only toggles remain immediate.
- Updated help, example instructions and the real-application smoke harness for explicit updates.

Validation on Windows x64: **313 tests passed** (306 existing plus 7 UI tests). New coverage includes 100 edits without processing, draft text, invalid ranges, duplicate clicks, refresh while importing, stop/edit during deliberately blocked work, failure/retry, incomplete results, missing inputs, Load example, and automatic system time-zone changes. The real-data WPF smoke test passed with model inference, sun paths/cardinals, explicit panel/diffuse/camera updates and exports; startup and completed-state screenshots were visually inspected. Scientific library source has no diff from the baseline.

Accepted by the user and merged through [PR #1](https://github.com/ckbk123/FisheyeSolarShadeForecast/pull/1), main commit `8586084`. Existing service-level orientation and baseline APIs remain available; the interface no longer invokes them automatically.

## 2. Dependency invalidation — ready for review

- Added one artifact dependency model used by interface previews, managed debug output and result validity. Numerical solar workbooks are distinguished from camera-dependent sun-path files in the same stage folder.
- Camera pose edits remove orientation, projected sun paths and shading while preserving the mask, raw irradiance, numerical solar positions and unshaded panel data. Panel/diffuse edits remove only transposition and shading. Image/profile, date/site, zone, weather and integration settings follow their respective dependency chains.
- Calibration setup fields configure a future calibration without invalidating the loaded profile. View-only settings and an unused API provider do not dirty imported results. Import interpretation settings conservatively invalidate imported data, including native workbooks that carry their own interval metadata.
- Relevant invalid draft text immediately invalidates affected artifacts. Reverting an edit cannot restore current status after files were removed. Repeated edits to already stale groups do not rewrite their manifest or repeat cleanup.
- Selected input contents are fingerprinted on Update, Export, activation and file-change notification. Replacements with unchanged filenames, sizes and restored modification timestamps are detected. Notifications do not start scientific processing.
- Missing or modified diagnostic files invalidate dependent output; a missing or changed manifest invalidates the run. Export checks source fingerprints and recorded artifact hashes before copying.
- Cleanup records stale status before deletion. A locked workbook reports a failure and cleanup can be retried. Selected inputs inside a managed run are protected from deletion and require relocation; automatic durable migration follows in milestone 3.
- Input changes cancel in-flight work. File removal waits for that run's writer to finish, preventing obsolete work from recreating invalidated files.

Validation on Windows x64: **357 tests passed** (313 previous plus 44 dependency tests), with no failures or skips. Tests cover the input matrix, selective file preservation, 100 repeated edits without manifest rewrites, reverting edits, invalid drafts, source notifications and fingerprint checks, missing/tampered artifacts, missing manifests, locked-file recovery, protected inputs, and deliberately blocked late work. The real-image WPF smoke test passed, including segmentation, overlays, explicit updates and exports; its completed-state screenshot was inspected. Example results remain 696 hourly rows, 149.33192161721075 kWh/m² before shading and 120.82722381119314 kWh/m² after shading. Scientific library source has no diff from the baseline.

Local verification: `artifacts/dependency-final-tests` and `artifacts/dependency-smoke`. Branch: `codex/dependency-invalidation`.

Scope boundary: this milestone manages the active and most recent calculation runs during the session. Historical run folders and legacy standalone orientation/calibration APIs remain until milestone 3 introduces one stable store and shared ownership. Complete export rules at every service entry point, frozen snapshot publication, and the PDF remain milestone 4. The distributed package is verified at the final milestone.

## 3. One current debug dataset — pending

Replace historical debug folders with stable stage paths; protect durable calibration inputs and implement migration, consistent publication and cancellation/recovery rules.

## 4. Complete export and PDF — pending

Validate complete current artifacts at the service boundary, freeze one export snapshot, copy its debug files and add a one-page settings/results recap. Finish with full acceptance and packaged-Windows verification.

Each milestone has a focused branch and pull request. User acceptance is required before merging and advancing to the next milestone.
