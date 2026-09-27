# Manual updates, current debug data, and complete exports

Prepared 27 September 2026. This is the full implementation specification. See [milestone progress](IMPLEMENTATION_PROGRESS.md) for completed work, verification, and remaining scope.

## Intended behavior

The user edits inputs freely, then explicitly clicks **Update results**. Editing never starts segmentation, orientation generation, weather retrieval, calculation, or diagnostic export. Debug Data contains one current set of stage outputs. An explicit export copies that set, with its existing names and structure, and adds a one-page PDF describing the settings and results.

The application must distinguish editable inputs from the immutable settings snapshot used by the last calculation. A result is current only when its dependencies match the current inputs and source files, all required stages have succeeded, and the corresponding artifacts are available.

For this specification, a **complete dataset** means a full shaded calculation: compatible calibration and sky mask, valid irradiance covering the selected period, solar geometry, unshaded panel irradiance, shading factors, and shaded panel irradiance. An unshaded baseline alone is incomplete and cannot be exported through Export results. Original calibration photographs and a new calibration solve are not required when a valid saved profile is supplied. Integration samples do not change the source data's native reporting intervals.

## Goal 1 — Explicit calculation and clear state

Place a colored indicator with a short text label beside the bottom-left calculation controls. Keep the existing detailed progress/error text. Color must never be the only explanation.

| State | Indicator and label | Update results | Export results |
| --- | --- | --- | --- |
| Required inputs missing, invalid, or incompatible | Red: Needs attention | Disabled until known input problems are fixed | Disabled |
| Inputs appear valid but have not been calculated, or results are stale | Yellow: Update required | Enabled | Disabled |
| Update running | Yellow with progress: Updating… | Disabled; Stop enabled | Disabled |
| All required results and artifacts successfully match current inputs | Green: Results up to date | Enabled; unchanged inputs may be a no-op | Enabled |
| User stopped an update | Yellow: Update stopped | Enabled if inputs are valid | Disabled |
| Calculation or artifact publication failed | Red: Update failed, with the actual reason | Retry enabled if inputs are valid | Disabled |

Green follows successful completion, never the button press. Lightweight validation can reject syntax, ranges, missing paths, and obvious incompatibilities before running; data-quality errors discovered during processing also prevent green.

Replace Calculate with Update results. Remove both automatic calculation and automatic orientation timers, including their startup and Load example triggers. Loading an example fills the interface and waits for Update. Loading a raw photo preview and changing graph zoom or overlay visibility may remain immediate because they do not run scientific stages or write diagnostics. Calibrate remains an explicit operation; successful calibration makes dependent results stale and does not trigger a calculation.

Retain an explicit force-refresh action for provider data, with wording such as **Refresh data & update**. Ordinary Update reuses valid cached inputs. Force refresh must not silently change a selected imported file into API mode.

Track text edits as soon as they occur, including before a field loses focus. Disable Export immediately on a relevant pending edit. Commit and validate the latest visible values before Update. Changes made while work is running cancel or supersede that work; its late callbacks cannot publish a current result or turn the indicator green. View-only preferences do not dirty scientific results.

## Goal 2 — Correct dependency invalidation

On a relevant edit, mark the affected stages stale, disable export, and remove their published debug artifacts. Invalidation is idempotent: once a stage is dirty and its files are absent, more edits do not repeatedly perform filesystem operations. Do not generate replacement workbooks, images, or per-edit run folders. Record status when stage validity changes rather than rewriting every artifact for every keystroke.

Keep valid upstream outputs. Keep a previous graph only if clearly dimmed and labeled **Previous results — update required**. Hide stale mask/orientation/sun-path overlays so they cannot appear aligned with a newly selected photo or pose.

| Changed dependency | Invalidate or remove | Preserve when still applicable |
| Sky photo, segmentation model/resolution, disk detection | Mask, image-dependent effective profile artifacts, orientation overlays, projected sun paths, visibility, shading factors, shaded results | Horizontal irradiance, numerical solar timeline, unshaded panel irradiance |
| Camera bearing, tilt, or roll | Orientation overlays, projected sun paths, visibility, shading factors, shaded results | Binary mask, horizontal irradiance, numerical solar timeline, unshaded panel irradiance |
| Active calibration/profile or coverage override | Effective profile artifacts, orientation overlays, projected sun paths, visibility, shading factors, shaded results | Binary mask if its own dependencies are unchanged, horizontal irradiance, numerical solar timeline, unshaded panel irradiance |
| Panel tilt/azimuth or diffuse model | Panel transposition and panel-dependent shading outputs | Mask, calibration, irradiance, solar timeline, camera overlays |
| Irradiance source/content, selected dates, interval interpretation, site, or effective time zone | Affected irradiance selection/interpretation, solar timeline, projected sun paths, transposition, time-dependent shading results | Image mask, active calibration, camera-only orientation overlays; source workbook if its dependencies truly remain unchanged |
| Integration sample count | Solar timeline, projected sun paths, transposition and time-dependent shading | Raw irradiance, mask, calibration, camera-only overlays |
| Graph zoom/day/component or overlay visibility | Nothing scientific | All calculated artifacts |

Use artifact-level dependencies where a directory contains both independent and dependent files. For example, numerical solar positions remain valid after camera rotation even though the sun-path image must be removed. Calibration setup fields must be distinguished from the already loaded profile: changing checkerboard dimensions configures a future Calibrate action, and must not misrepresent the provenance of an existing profile.

Detect selected source files changed in place, not only changed paths. Revalidate relevant fingerprints on Update and Export, and refresh validity on application activation or source-change notification without recalculating. Unknown or invalid pending input must never fall back silently to an older valid value for export.

## Goal 3 — One bounded, safely published debug set

Keep stable stage paths under Debug Data using the current descriptive file names. Remove timestamp/GUID history creation for automatic diagnostics. Store one current status/settings manifest, with stage validity, input fingerprints, artifact list, software versions, and last successful update time.

Recompute and rewrite only invalid stages on Update. Existing valid stages may be reused, with their provenance recorded. Prepare replacements in a bounded temporary staging area outside the published set and publish completed stages under a shared writer lock. Set the overall completed status last. Failed or canceled work must not publish partial stage files as valid. Stop, app restart, and interrupted publication must leave a recoverable incomplete state, never an apparently complete mixed dataset.

Coordinate calibration, calculation, orientation, invalidation, and export through the same storage ownership rules. Synchronize removal with active writers so obsolete background work cannot recreate an invalidated artifact. Include protection against two application instances targeting the same debug root.

Move durable active calibration profiles and associated required files to Data before removing historical debug folders. Copy selected inputs out of managed diagnostic history if necessary and update their saved paths. Never overwrite user-owned input files or delete user-selected export folders or the bundled reference dataset. One-time migration may remove only verified application-owned historical debug folders after protecting referenced inputs. Internal caches in Data are separate from the visible debug set and need not be eliminated by this change.

## Goal 4 — A complete export with one-page recap

Export results is enabled only in the green state. Enforce the same completeness check in the export service, not just the button. A run marked Complete with required shading stages Skipped is insufficient.

At export time, verify that required artifacts exist, their recorded identity matches the completed update, and current input fingerprints still match. Freeze that completed snapshot for the duration of export. Copy the existing debug files byte-for-byte; do not recalculate or rebuild their workbooks. Keep separate folders for explicit user exports so a later update does not overwrite an already exported scenario.

Add **Summary.pdf**, a readable single-page recap generated from the successful calculation snapshot rather than mutable interface fields. Include:

- Update time, export time, application/library version identification and result identifier.
- Site coordinates/elevation, selected dates, resolved time zone and automatic/manual zone selection.
- Irradiance provider or imported file name, native interval description, timestamp convention, and retrieval time when available.
- Active sky-photo/profile names, effective calibration coverage, segmentation settings, camera bearing/tilt/roll, and disk detection choice.
- Panel tilt/azimuth, diffuse model, and integration samples per interval.
- Before/after shading energy in kWh/m² and estimated loss, matching the interface and workbooks.
- Concise calculation assumptions and a guide to the accompanying stage folders.

Distinguish effective values from unused interface options: imported metadata may override a fallback interval setting; a loaded profile may have different board settings from those currently entered for a future calibration. Use short names in the PDF and retain exact values, identifiers, and provenance in the machine-readable manifest. Do not invent unavailable source metadata.

Build the export in a temporary destination and mark it complete only after all files and the PDF are present. A locked source/destination, missing file, or PDF-generation failure must produce a clear error and no apparently successful partial export. PDF generation must work in the packaged application without requiring Python or external desktop software.

## Completion tests

1. **Manual operation:** Load example and perform 100 field/slider edits. No calculation, segmentation, orientation export, weather request, or new debug run starts before Update. One click starts one update; a rapid double click cannot start duplicates.
2. **State transitions:** Missing/invalid inputs show red; valid edits show yellow; running stays yellow; only a complete successful update becomes green. Status remains understandable without color. Cancellation is yellow and failure is red, both with export disabled.
3. **Uncommitted input:** Type a different or invalid value without leaving the text field. Export disables immediately; Update uses the newly entered value after validation.
4. **Bounded storage:** Run at least 20 successful updates. The debug root still contains one current stage set with no historical calculation/orientation folders or accumulated temporary files.
5. **Dependency precision:** Test every row of the invalidation table. In particular, camera rotation removes orientation/sun-path/shading artifacts but leaves the binary mask and weather workbook unchanged; panel tilt preserves all upstream inputs. Repeated edits to already dirty stages perform no repeated deletes or scientific exports.
6. **No stale revival:** After removing stale files, edit back to the earlier value. The indicator remains yellow until an explicit Update restores and verifies the required outputs. Missing files cannot be treated as complete merely because settings match again.
7. **Scientific parity:** With identical inputs, pre-change and post-change numerical outputs, interval count, timestamps, and precision match. Hourly and 15-minute fixtures keep their native cadence. Fresh and cache-reused calculations agree.
8. **Completeness:** Reject export for baseline-only results, missing/incompatible calibration or mask, missing irradiance intervals, skipped required stages, missing artifacts, stale results, and failed/canceled updates. Valid zero irradiance and zero shading loss are accepted.
9. **Cancellation and failure:** Stop during each major stage and inject a calculation or file-write failure. Valid independent outputs remain; stale/partial outputs are absent or explicitly incomplete. Retry succeeds without mixed settings or orphaned temporary history.
10. **Overlapping work:** Change inputs during a slow update, initiate calibration, attempt export during update, and exercise a second app instance. No obsolete writer can publish green results or recreate stale files; export cannot mix revisions.
11. **Calibration survival:** Migrate a saved profile referenced inside an old debug run, restart the application, and calculate successfully. Cleanup preserves active inputs, user exports, and bundled examples.
12. **Source changes:** Change a selected image/profile/irradiance file in place, including with its timestamp restored. Fingerprint validation detects the change before a completed export or reuse. A changed automatic system time zone also invalidates affected results without starting an update.
13. **Export fidelity:** For a complete green result, every copied diagnostic file matches its source bytes and all report values match the same completed snapshot. Changing the interface during copying cannot alter the captured export.
14. **PDF quality:** Render and visually inspect the PDF. Confirm exactly one page, readable typography, correct units/time zone, no clipping, and sensible handling of long file names and unavailable optional metadata.
15. **Filesystem and recovery:** Test an open Excel workbook, locked destination, failed PDF creation, and restart after interrupted publication. Show actionable errors and never advertise incomplete exports as successful or incomplete debug data as green.
16. **Packaged behavior:** Build and test the distributed Windows application, including first launch, Load example, manual Update, Stop, Calibrate, restart, and Export. Update Help and packaging documentation to describe manual operation and stable debug storage.

## Implementation map

- `src/ApplicationFrontend/MainWindow.cs`: explicit controls, validation/dirty tracking, status indicator, removal of automatic triggers, stale previews, completeness-based button state.
- A shared calculation-state/dependency component: one authority for state transitions, artifact invalidation, revisions and export eligibility.
- `src/ApplicationFrontend/AppServices.cs`: frozen request snapshots, dependency-aware stage reuse/publication, complete-dataset checks, durable calibration storage and consistent export.
- `src/ApplicationFrontend/DebugDataRun.cs`: replace historical run bookkeeping with stable current-artifact bookkeeping, staging, invalidation and manifest validation.
- `src/ApplicationFrontend/AppData.cs` and portable path handling: durable profile storage and safe migration of active paths.
- A packaged PDF report component using a suitable .NET-compatible library, selected during implementation with its packaging and license requirements checked.
- Frontend integration/UI/debug tests, Help text, RUNNING documentation, and delivery packaging.

Implement state and dependency rules first, then manual controls, stable debug storage and calibration migration, then export/PDF, followed by regression and packaged-app validation. The change is complete only when the tests above pass; a green light or renamed directory alone is insufficient.
