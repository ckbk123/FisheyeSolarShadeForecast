# Frontend workspace delivery record

## Part I: tabbed frontend foundation

29 September 2026. Existing behavior is now hosted in **Solar Irradiance**. The shell constructs each workspace once; its local view/controller owns inputs, subscriptions and cancellation. `IIrradianceService` exposes accepted source readiness without requiring dependent views to inspect upstream controls.

Optional artifacts share the existing storage lease, with a separate owner marker and sidecar manifest. They never add requirements to upstream completeness. Publication/export verify source files, settings/source identity and artifact bytes; an interrupted or stale writer cannot publish a current dataset. Existing manifests need no migration. Cache keys now use an explicit irradiance orchestration schema and scientific-library identities, so changing a PV assembly does not discard upstream science. Old cache entries are safely missed once.

Verification:

- Release solution test run: 466 passed, no failures (145 frontend, including six new workspace/storage tests).
- Standalone self-contained application published and packaged smoke workflow passed: explicit example update, overlays, panel changes, cancellation and verified irradiance export.
- Rendered screenshots reviewed at the default window size. Existing chart and all original controls remain in the first tab; workspace actions remain in the header.
- No numerical equations, mandatory output files or one-page irradiance report were changed.

Part II will provide the PV controls, shared-operation guard and its own service/backend/UI/export tests. Orientation optimization remains a future isolated service, outside these two PRs.
