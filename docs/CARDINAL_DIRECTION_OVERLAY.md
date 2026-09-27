# Cardinal-direction debug overlay

## Library and projection

`CardinalDirectionOverlayGenerator` and `CardinalDirectionOverlayExporter` live in the existing `SolarShade.Shading` library. The frontend does not calculate bearings or draw annotations. The generator accepts an immutable `CalibratedSkyProjection`, uses its camera pose and coverage, and returns a transparent native-resolution PNG plus numerical marker records.

The four ticks follow constant true-azimuth meridians (N=0°, E=90°, S=180°, W=270°) in the upper hemisphere. Each tick begins at the furthest resolved visible zenith angle on its meridian and follows that meridian inward. A marker at zenith 90° is on the horizon; other markers indicate a coverage boundary. Calibrated angular coverage, the actual detected disk and the image rectangle all constrain visibility. Ticks can appear over obstructions: this is an orientation diagnostic, not sky classification.

The search uses bounded 0.025° increments and 40 boundary bisections. Very thin visible fragments can be unresolved; fragments shorter than one native pixel are reported unavailable. Missing directions are not fabricated. These numerical search limits do not establish physical calibration accuracy.

The existing camera convention is preserved: with the camera pointing up, zero roll and top heading North, N is above, E left, S below and W right. Roll rotates this arrangement; it cannot reverse handedness. The overlay does not independently mirror the photograph or interchange E/W. A real known-bearing photograph is still needed to validate the entered physical camera orientation; the bundled example pose is illustrative.

Magenta annotations use 85% target opacity. Text remains upright; label placement may move inside the rectangle or omit a label when space is insufficient. The exported endpoint stays at the true projected boundary independently of text layout. The original image and binary mask remain unchanged.

## Frontend lifecycle

The original-photo viewport and overlay share native image coordinates and one scaling/letterboxing transform, including when the photo preview is downsampled. **Show cardinal directions** changes only visibility and persists the preference.

Orientation preview work has a separate revision/cancellation lifecycle from time-dependent calculation. Relevant committed edits clear stale markers, debounce and request the newest image/calibration/pose. Old callbacks cannot publish after a newer orientation edit. The image stages can run without irradiance, dates, site or panel inputs; full calculations invoke those stages before weather retrieval.

The library-result cache depends on image identity, effective calibration, native dimensions, actual disk, heading/tilt/roll and library identity. Weather/date/time-zone/panel changes and toggling visibility reuse the overlay. Changing mask settings reruns necessary image preparation; the compass is reused if its actual geometry inputs remain identical.

## Debug Data

`02-orientation` contains:

- `cardinal-directions-overlay.png`: exact transparent library result displayed on the photo.
- `cardinal-directions.xlsx`: four direction rows, including bearing, visibility, endpoint zenith/native pixel coordinates, label baseline coordinates and status.
- `cardinal-directions-details.json`: pose, projection coefficients/coverage/disk, all tick vertices, rendering options, conventions and timing.

Standalone previews create `orientation-...` runs containing image, calibration and orientation stages. Full calculation runs include the orientation stage alongside existing scientific stages. Cached results are exported into the current run without regenerating their image.

## Measured performance

Six calls using the bundled 3000×4000 photo's effective calibration and detected disk: three at zero pose and three with heading/tilt/roll. The first call includes cold initialization; subsequent calls share the process.

| Measurement | First call | Subsequent calls |
|---|---:|---:|
| Projector construction and overlay PNG generation | 801 ms | 285–319 ms |
| Above plus PNG/XLSX/JSON exports | 917 ms | 292–329 ms |

Measurements exclude image decoding, segmentation, unrelated stage work and weather retrieval. They describe this machine and image, not a hardware-independent guarantee. No solar samples are computed. Evidence and reproducible development harness: `artifacts/cardinal-backend-check`, including `timings.json`, both pose outputs and `independent-verification.json`.

## Verification

The shading suite passes 87 tests, including 13 cardinal cases covering analytical cardinal positions, heading/roll, tilt, disk/rectangle clipping, horizon versus coverage endpoints, unavailable directions, alpha, exact exports and cancellation. Independent Python read-back checks PNG dimensions/alpha/color, all four workbook rows, analytical projection from the exported pose/calibration, and boundary exclusion immediately beyond each non-horizon endpoint. `preview-proof.png` records a visual comparison of zero and tilted/rolled poses over the original photo.

All 50 frontend tests and 23 shading-correction regression tests also pass: 160 relevant tests total. Frontend tests include reduced-photo/native-overlay alignment, geometry-only cache identity, standalone preview with invalid study settings, and early publication before weather failure.

The packaged application passes `artifacts/cardinal-verified/smoke-result.json`, `cardinal-ui-result.json`, `sun-path-ui-result.json` and `timezone-result.json`. The actual WPF checks cover preview before Calculate, an invalid camera entry followed by an unrelated edit and recovery, a valid pose edit while latitude is invalid, display-only toggle/persistence, panel/time-zone reuse, exactly one generation after a pose edit, immediate stale clearing, native PNG byte equality and unchanged sun-path behavior. Screenshots include the original photo with and without markers and the full application window.

The first 696-row example retains exactly 149.33192161721075 kWh/m² before shading and 120.82722381119314 kWh/m² after shading.

Independent read-back passes all 13 completed Debug Data runs: nine full calculations and four standalone orientation previews. Two deliberately interrupted preview runs are correctly marked Cancelled. Each full calculation contains nine workbooks, 41,760 selected integration samples, four independently verified cardinal markers and 12 million unchanged binary-mask pixels. Evidence: `artifacts/cardinal-verified/workbook-verification.json`.

## Delivery

Updated `Deliverable-rewritten/APPLICATION.exe` and clean `Deliverable.zip`. The native bundled reference run is copied from the verified packaged application's first calculation, including all three new orientation artifacts. The delivery script preserves existing local `Data/settings.json`; the clean ZIP starts with example settings.

The ZIP has 39 files (763,024,575 bytes). All entries pass CRC verification; all 19 reference-run files match the verified source copy, and ZIP/installed executable hashes agree. A fresh extraction passes the portable startup and UI smoke tests, including automatic example loading and the cardinal overlay controls. Evidence: `artifacts/cardinal-verified/package-verification.json` and `artifacts/cardinal-portable-check/smoke-result.json`.

Executable SHA-256: `14DB1B2F99EA338BDF5CF5EBA3A122E0424926FEE0DAC06FF9CB54E60F54A724`.

