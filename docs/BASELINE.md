# Repository baseline

This repository starts with the current **Solar Forecast Estimator** working project on 27 September 2026. Its only Git remote is `https://github.com/ckbk123/FisheyeSolarShadeForecast.git`. The separate older Fisheye Solar Shading Estimator checkout and its remote are not part of this import and are not modified.

## Scope

- Preserve all current application and scientific library source, project files, tests, scripts, documentation and third-party notices.
- Preserve required calibration regression fixtures at their existing paths under `artifacts/calibration-validation`; other generated artifacts and backup checkouts are excluded.
- Preserve the two frozen provider workbooks under `src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905` and the Astropy reference JSON under `src/SolarPositionAndShading/Validator/results`, because the existing shading tests read these exact paths.
- Preserve example images, irradiance input and the fixed example reference outputs, plus existing raw research images and validator reference inputs. These are distinct from generated user sessions.
- Store the four pretrained ONNX models in Git LFS. A clone needs `git lfs pull`; Git pointers alone are not usable model weights.
- Retain the existing native runtime packaging files and their component notices.
- Exclude installed application folders/ZIPs, build products, Python environments, runtime caches, temporary debug runs, test output and credential files. Exclusion does not delete local files.
- No application functionality or scientific library implementation changes are included. The README, Git settings and this document establish the source baseline.

The baseline is a checkpoint of existing behavior, including automatic recalculation, historical debug folders, and current export behavior. The manual-update/current-debug/PDF-export specification is deferred in [MANUAL_UPDATE_AND_EXPORT_PLAN.md](MANUAL_UPDATE_AND_EXPORT_PLAN.md).

## Build and validation

Use the Windows x64 commands in the repository README. The desktop application targets .NET 10; the scientific projects also target .NET 8. All models and NuGet dependencies must be available before model-dependent runs. Optional network/provider checks and hardware-specific validation are separate from ordinary test runs.

Verified on Windows x64 using .NET SDK 10.0.204, from a clean directory materialized from the Git index (no pre-existing build outputs or untracked inputs):

- Solution restore succeeded.
- Release solution build succeeded with **0 warnings and 0 errors**.
- **306 tests passed** across seven suites: calibration 46, masking 18, irradiance 47, transposition 35, solar/shading 87, shading correction 23, and frontend 50.
- The first test pass identified three reference files excluded by the initial ignore rules. Those files were added unchanged and the full affected solar/shading suite was rerun successfully. No test or production implementation was edited.
- Selected source and asset files were checked against the clean copy using SHA-256, including all four actual ONNX files rather than LFS pointers.
- A high-confidence token/private-key pattern scan of included text found no matches. Generated credentials and local session directories are excluded.

This verifies source restore/build and the existing automated tests, not a new application release, live-provider study, fresh packaged smoke test, or independent field validation. The annotated tag `baseline-2026-09-27` identifies this pre-change checkpoint. Raw local test logs remain excluded from the repository.

The existing packaging script is preserved unchanged. Its notice-collection step expects specific NuGet package/runtime versions in the local cache, including .NET runtime 10.0.8, and runs before its restore step. A source build/test does not establish that this historical packaging flow works on every fresh machine. See [RUNNING.md](../src/ApplicationFrontend/RUNNING.md) for its current behavior and optional smoke tests. Historical documentation can reference local reports excluded from Git; such reports are not newly verified by this import.

## Working from this baseline

1. Keep `main` as the accepted source. The initial import is pushed directly because the remote is empty.
2. Create one focused branch per change, normally using `codex/<description>`.
3. Make small, meaningful commits and push the branch to this repository.
4. Open a pull request explaining the problem, resulting behavior, validation and remaining limitations.
5. Review the diff and test results, then merge the accepted change into `main`.
6. Use annotated tags for known checkpoints and GitHub releases for separately distributed application packages.

The planned interface/storage/export work must leave scientific library source unchanged. Continue to run the existing library regression tests as well as relevant frontend tests. No new licensing terms are introduced by this import; retain and consult the existing component notices.
