# Rewrite implementation status

Historical 15 September checkpoint. The installed application has since advanced to Preview 0.1.1 with PV Autonomy; see the [current review](SOURCE_AND_EXPORT_REVIEW_2026-10-04.md).

**Complete — 2026-09-15.** See [delivery report](REWRITE_DELIVERY.md) and [architecture](APPLICATION_ARCHITECTURE.md).

Existing libraries own calibration, masking, irradiance import/retrieval, solar positions, transposition, panel shading and scientific summaries/exports. Frontend PanelEngine and WorkbookIo are removed; the frontend sequences stages and displays returned values.

Source-native hourly, 15-minute, 30-minute and explicit variable-duration intervals are preserved. Every invoked stage places its native YAML/PNG/XLSX output in its Debug Data run; cached artifacts remain truthful and manual export copies the completed run.

Validation: 257 tests passed across seven suites. Final packaged smoke passed calibration, panel edits, cache reuse and time-zone changes. Independent artifact read-back passed six calculation variants plus native calibration. The 696-interval reference has exactly unchanged energy totals and only floating-point roundoff at component level. Fresh portable startup and relocation with restored settings/disk mask cache passed. Clean ZIP contents, CRCs and executable SHA-256 verified.

Delivery: Deliverable-rewritten/APPLICATION.exe and Deliverable.zip. Original Deliverable/APPLICATION.exe remained running and was not replaced. The new package includes a native library-generated Example/Debug Data/reference-run; original input is under Example/Irradiance.

Evidence: artifacts/rewrite-verified, artifacts/rewrite-portable-check, artifacts/rewrite-relocation-check. Backup: artifacts/rewrite-baseline-20260915-165416.

Stable library interfaces require no frontend calculation edits when algorithms change. Rebuild/republish the self-contained application to include updated libraries.
