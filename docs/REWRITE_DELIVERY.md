# Library/frontend rewrite delivery — 2026-09-15

Historical delivery record. The two local installed executable copies now match the Preview 0.1.1 ZIP; see the [4 October source/export review](SOURCE_AND_EXPORT_REVIEW_2026-10-04.md) for current package identity.

## Result

The existing libraries now own the scientific stages and their native outputs. The frontend owns UI state, sequencing, progress/cancellation, cache coordination, run manifests and display. Frontend PanelEngine and scientific workbook construction were removed.

Irradiance intervals are authoritative throughout: hourly, 15-minute, 30-minute and explicit variable-duration inputs retain their timestamps and bounds. Solar quadrature stays inside those intervals. Missing coverage is reported rather than filled or converted into longer observations.

Each calculation writes library-generated artifacts into numbered Debug Data stage folders. Calibration writes native YAML/profile/diagnostics; masking writes binary PNG/metadata; numerical stages write XLSX and exact result metadata. Manual export copies the completed run. The bundled reference-run was produced by this packaged executable.

## Delivery

- `Deliverable-rewritten/APPLICATION.exe`: new self-contained executable. The previous `Deliverable/APPLICATION.exe` was running, so it was left intact.
- `Deliverable.zip`: clean first-run package with the new executable, inputs and native example reference.
- `docs/APPLICATION_ARCHITECTURE.md`: ownership, APIs, dependencies, interval semantics and cache lifecycle.
- `artifacts/rewrite-baseline-20260915-165416`: source backup before the rewrite.

Executable SHA-256: `514655D0FAE411A63CA5512F13B46A4B244D6B1ED678401DB094550D3325228A`.

The ZIP contains 33 files, 762,868,930 bytes. Every ZIP entry passed CRC verification; the delivered executable matches the packaged executable and publish manifest. The package contains only fresh default settings under Data, with no test caches or runtime debug sessions.

## Validation

| Suite | Passed |
|---|---:|
| Calibration | 46 |
| Masking | 18 |
| Irradiance | 47 |
| Solar/shading geometry | 46 |
| Transposition | 35 |
| Shading correction | 23 |
| Frontend/orchestration | 42 |
| **Total** | **257** |

Coverage includes native format round trips, independent numerical references, interval clipping/gaps/DST, horizontal identity, open/blocked/partial sky, receiver orientation, missing calibration/mask, cache invalidation, returned-result parity, cancellation, failed writes and brief manifest reader locks.

Packaged smoke passed: real model inference; 696-interval example; panel edit without solar recomputation; 12-board calibration; region time-zone changes; manual override; fixed offsets; automatic stage output and manual export. A fresh portable copy auto-started the example. After moving its directory, another process restored saved settings and reused the validated disk mask cache while running from a different working directory.

The old and rewritten example have exactly equal energy totals: 149.33192161721075 kWh/m² unshaded, 120.82722381119314 kWh/m² shaded. The maximum per-component difference over 696 intervals is 4.55 × 10⁻¹³ W/m² (floating-point roundoff).

Final evidence is in `artifacts/rewrite-verified`, with portable evidence in `artifacts/rewrite-portable-check` and `artifacts/rewrite-relocation-check`. The independent native artifact verifier checks every completed calculation variant and calibration, including source bounds, scientific values, weighted reconstruction, YAML and mask pixels.

Two exploratory smoke runs are retained for transparency: one exposed a no-op timezone transition in the smoke harness (corrected to test actual transitions); another exposed a temporary file replacement lock (fixed with bounded atomic replacement retries and a regression test). The final packaged smoke passes.

## Practical limits

The application's current NASA POWER/Open-Meteo endpoints declare hourly means; finer data is accepted when its source provides it. Existing tests cover provider adapters; this delivery did not rerun live external services. Panel shading supports Hay–Davies/isotropic sky diffuse and the existing conservative unknown-sky treatment, without ground reflection or electrical PV-power conversion.

Stable library interfaces allow scientific implementation changes without frontend calculation edits. A self-contained executable must still be republished to include changed libraries. Standalone correction artifacts carry source coordinates when available; the actual requested calculation site is recorded in the solar-stage artifacts and complete run manifest.
