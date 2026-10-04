# Source and exported application review — 4 October 2026

This is an initial architecture and release-state assessment of the active `ckbk123/FisheyeSolarShadeForecast` repository at `main` commit `220895bf94c9e35b8904435a14742aeb5c01ed8c`. It records the Preview 0.1.1 baseline before the mask-editor implementation; the current source workflow is documented in [RUNNING](../src/ApplicationFrontend/RUNNING.md). The separate `Pastoralee/fisheye-solar-shading-estimator` checkout is the earlier Python research implementation and a separate, uncommitted C# prototype; it is not the source of the reviewed `APPLICATION.exe`. This review changes documentation only.

## What the source implements

`ApplicationFrontend` is a Windows x64 WPF shell with two workspaces. Solar Irradiance accepts calibration/profile, sky photo, site, period, panel and irradiance inputs. `AppServices` coordinates native calibration, ONNX segmentation, interval-aware irradiance acquisition/import, solar geometry, transposition and shading correction. The scientific calculations and native exports live in their respective libraries. The frontend owns input state, dependency invalidation, cache use, artifact publication, chart display and complete-snapshot export. PV Autonomy consumes only an accepted, complete shaded `PanelRun`; its integration adapter invokes the separate PV/battery model and writes a separately scoped optional `07-battery` result.

The dependency direction is acyclic. The .NET 8 irradiance, transposition and battery libraries have portable contracts; Windows-specific calibration, camera projection, masking, shading, correction and WPF projects sit above them. Source intervals remain authoritative through solar and panel calculations. A larger internal quadrature count does not fabricate higher-resolution weather observations.

The application maintains one current published irradiance dataset with a manifest and hashes. A verified unchanged update preserves its identity. Changed stages use bounded staging; selected inputs and reusable profiles are kept in `Data`. Normal startup and editing do not scan historical run folders. The PV result has its own manifest and is invalidated when its source or settings change. Irradiance and PV exports are independent.

## Verification performed for this review

- Restored and ran `dotnet test SolarShade.sln -c Release --no-restore` after restore: **504 passed, 0 failed, 0 skipped** across nine test projects. An initial no-restore attempt reported missing assets for the PV test projects; restore resolved that setup issue.
- Ran the installed `Deliverable/APPLICATION.exe --smoke-test` with isolated data and debug directories. It passed the real example calculation (696 hourly rows; 149.33192161721075 kWh/m² before shading and 120.82722381119314 kWh/m² after shading), PV/backend agreement, an irradiance export with 18 byte-identical diagnostic artifacts and one-page PDF, PV export, panel edit, and preview overlays. The Solar Irradiance and PV Autonomy screenshots were visually inspected. Output is local under `artifacts/review-2026-10-04/smoke`.
- Verified `Deliverable/APPLICATION.exe`, `Deliverable-rewritten/APPLICATION.exe` and the executable inside `Deliverable.zip` have the same SHA-256: `096B6FC9088FBCDFCCBD949E9868D40D841CC5BBD7C36FB066525C48B7647206`. The ZIP SHA-256 is `8083F5DE3C1A23D653F3285834B72CB9E86817160A3FADAC59226F40E46202B2`, matching the tracked [package receipt](performance/2026-10-01-packaged-validation.json). The executable identifies itself as `0.1.1+14a386c4c3089084b6aea39c3f88bac2ac51cb13`; only documentation/receipt files changed between that source commit and current `main`. The local generated `artifacts/application-package.json` records an older hash (`A23EC0C2...`) and should be regenerated at the next publish; it is not reliable evidence for this installed package.
- GitHub CLI is installed at `C:\Program Files\GitHub CLI\gh.exe` and authenticated. There is no published GitHub release for the active repository; the deliverable and ZIP are local generated outputs.

## Architecture assessment

The strongest aspect is the explicit scientific-library boundary: calibration, image masking, weather intervals, solar geometry, transposition, shading correction and battery simulation can be tested separately. Provenance and checksums make an accepted calculation inspectable. The test suite exercises numerical references, time intervals, artifacts and frontend invalidation, and the exported executable passed a real workflow.

The main maintainability pressure is concentration of orchestration and UI behavior in large files: `AppServices.cs` (about 568 lines), `IrradianceWorkspace.cs` (about 629 lines), and the calibration solver's `OmniCalibCSharpPort.cs` (about 1,263 lines). Shared static paths and the native-operation gate also assume one local application session and make concurrent or multi-site use harder. These are refactoring candidates if future features need them; the current tests and smoke run do not show a functional failure from this structure.

The main scientific limits are still camera/profile pairing and field accuracy. A matching image size does not prove the same lens, orientation or calibrated edge coverage. The main shaded curve treats unobserved sky as blocked and retains an upper bound. Segmentation, pose, shading and PV assumptions need held-out site measurements before interpreting a result as a validated prediction. The PV model uses fixed efficiency and ideal battery charge/discharge with no ageing, temperature or power limits. The application currently exposes NASA POWER and Open-Meteo; the library has four additional credentialed adapters, but this review did not live-test providers.

## Documentation corrected

The root README now describes Preview 0.1.1, both workspaces, current test count and the local package. `APPLICATION_ARCHITECTURE.md` and `PROJECT_STRUCTURE.md` now include PV integration and the current single-dataset lifecycle. `ApplicationFrontend/README.md` and `RUNNING.md` now distinguish the implemented explicit-update workflow and retained historical folders from the archived initial design. Historical plans and dated validation reports remain as records of their original checkpoints.

## Useful next decisions

1. Define the field-validation dataset and acceptable errors for calibration, mask, solar projection, shaded irradiance and PV autonomy.
2. Decide whether upcoming work targets research accuracy, colleague usability on older Windows PCs, or multi-site/multi-session workflows; each puts pressure on a different boundary.
3. Regenerate the local package manifest during the next publish and continue retaining the versioned executable/ZIP receipt and smoke result.
