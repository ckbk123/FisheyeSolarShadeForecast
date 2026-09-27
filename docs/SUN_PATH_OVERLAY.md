# Sun-path overlay — implementation and validation

**Complete — 2026-09-16.**

The overlay is implemented in the existing SolarShade.Shading library. SunPathOverlayGenerator consumes the selected samples already prepared in SolarTimeline and uses CalibratedSkyProjection, the same lightweight projector now used by shading. It produces semi-transparent golden-yellow daily tracks at the native mask resolution. No extra ephemeris sequence is calculated and no irradiance observations are added.

## Accuracy and behaviour

- Every selected sample is inspected; no days or source intervals are discarded for speed.
- Apparent solar directions and effective calibration/camera pose agree with shading.
- Tracks split at display-local midnight, nighttime, gaps, invalid projection, excluded lens coverage and wide-angle rear-axis discontinuities.
- Projected paths use a 0.35 native-pixel simplification bound relative to input points. Raster coordinates retain 1/256 pixel precision. This drawing tolerance is separate from the solar model/calibration's physical accuracy and the existing temporal integration resolution.
- Golden yellow is #FFD200 with 50% target opacity. Repeated tracks do not accumulate into an opaque band. Stroke width is a visual annotation, not the physical solar diameter.
- Paths remain visible over both sky and obstructions. The original binary mask stays unchanged.
- The UI uses a common native-coordinate viewport, including identical scaling/letterboxing. The overlay appears before large numerical exports and shading correction finish.
- Camera pose/calibration/image geometry and solar timeline changes invalidate the overlay. Panel and diffuse-model changes reuse it. The visibility toggle and resizing are display-only.

## Native Debug Data

Under `04-solar-positions`:

- `sun-path-overlay.png`: exact transparent image returned by the generator.
- `sun-path-projection.xlsx`: compact retained vertices with source interval ID, selected sample index, timestamp, local track date and native pixel coordinates. Large outputs split into 100,000-row sheets.
- `sun-path-details.json`: counts, dimensions, convention, visual settings, tolerance and generation timing.

The existing solar integration sheets retain the complete source data; the new vertex workbook references them without duplicating hundreds of thousands of rows. Cached images are exported into each run with their cache origin recorded in the run manifest.

## Performance target

Full 365-day year, native 3000 x 4000 image, 60 prepared geometry samples per source interval. Three fresh processes and five subsequent runs per process for each cadence. Timings include projector construction, projection, simplification, drawing and PNG encoding. Export measurements additionally include PNG, compact XLSX and metadata writes. The source solar preparation is measured separately because it is an existing pipeline stage.

| Source cadence | Selected samples | Worst first image | Median subsequent image | Worst first image + all overlay exports |
|---|---:|---:|---:|---:|
| Hourly | 525,600 | 717 ms | 392 ms | 957 ms |
| 15 minutes | 2,102,400 | 634 ms | 501 ms | 804 ms |

All final measured overlay-generation and overlay-export runs were under one second on the test machine (AMD Ryzen 7 5700X, 8 cores / 16 threads, Windows 11). The 365 tracks retained 14,171 / 14,228 vertices respectively. Separate source solar preparation took 68-125 ms / 232-274 ms. These are measured results for the stated image size, data and machine, not a timing guarantee for arbitrary resolutions or hardware. They do not include unrelated mask inference, network retrieval, other stage workbooks or shading integration.

Evidence: `artifacts/overlay-final-summary.json` and the six `artifacts/overlay-final-60-*` / `overlay-final-15-*` benchmark directories. Reproduce with:

```powershell
dotnet run -c Release --project src/SolarPositionAndShading/OverlayBenchmark -- artifacts/my-overlay-benchmark 60 365 5
```

## Verification

74 solar/shading/overlay tests, 45 frontend tests and 23 shading-correction regression tests passed after this change (142 tests). Coverage includes independent analytical projection points, shared-shading parity, dense projection rays, clipping, roll/heading/tilt, selected-only inputs, simplification bounds, day/gap/night breaks, alpha overlap, native exports, cancellation, wide-angle discontinuities and aligned UI viewports.

Packaged application and independent artifact evidence: `artifacts/sunpath-final`; portable startup evidence: `artifacts/sunpath-portable-check`. The final smoke passed native overlay byte equality, unchanged binary masks, early display, display-only toggle, panel/diffuse reuse, camera reprojection without solar recalculation, invalid-camera input/recovery and timezone changes. Independent read-back passed all nine final calculation runs: each contains eight native workbooks, 41,760 selected integration samples, 957 overlay vertices across 29 daily tracks, and 12 million unchanged binary-mask pixels. The first 696-row example retains exactly 149.33192161721075 / 120.82722381119314 kWh/m² before/after shading.


## Delivery

Updated `Deliverable-rewritten/APPLICATION.exe` and clean `Deliverable.zip` (36 files, 762,967,357 bytes). The bundled native reference run includes the new overlay PNG, vertex workbook and metadata. All 16 reference-run files in the ZIP are byte-identical to the final packaged application's first run. Every ZIP entry passed CRC verification; the ZIP and installed executable match the publish SHA-256.

Executable SHA-256: `8DDBA33058F866AA88F13AE75C0AECD7CAB8B413AAEEB211664CDE5DFFA62EA1`.

Source backup before this feature: `artifacts/sunpath-baseline-20260916-214418`. Changed scientific/library files are the shared projector, shading delegation, overlay generator/exporter, tests and benchmark; frontend additions are orchestration/cache keys, native image layering and display controls. No new library project was added (the standalone benchmark is a development-only console harness).

