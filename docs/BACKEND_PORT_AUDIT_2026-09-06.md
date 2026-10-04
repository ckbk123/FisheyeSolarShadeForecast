# Backend C# port audit — 6 September 2026

Historical checkpoint: the missing application and shaded tilted-panel workflow described below were implemented later. See [current architecture](APPLICATION_ARCHITECTURE.md) and the [4 October source/export review](SOURCE_AND_EXPORT_REVIEW_2026-10-04.md). The findings below describe the 6 September source state.

## Verdict

The repository contains substantive C# implementations of all seven major computational capabilities listed below. No production Python subprocess bridge, embedded Python runtime, or placeholder algorithm was found in the inspected C# sources and project references. Python files are development exporters or independent numerical oracles. OpenCV and ONNX Runtime are native binary dependencies invoked from C#, not Python scripts.

The library port is substantially complete **within its documented scope**. A complete application is not yet present. In particular, horizontal shading correction and unshaded tilted-panel transposition are separate capabilities; a physically consistent shaded tilted-panel result is still missing. This is a backend calculation task as well as an integration task.

This audit uses the acceptance criteria recorded in the repository: real C# runtime implementation, actual algorithms/weights, callable library contracts, reference validation, input/output semantics, hardware fallbacks, and deployment/performance limits. There is no single current repository-wide porting checklist; the calibration checklist predates several later changes. This is not a reconstruction of every requirement from earlier conversations.

## Capability assessment

| Capability | Actual implementation | Assessment and limits |
|---|---|---|
| Checkerboard images to lens calibration | `OmniCalibCSharpPort.Calibrate`; C# initialization, joint optimization, polynomial fitting, YAML/debug output; OpenCvSharp corner detection and MathNet/SIMD math | Implemented for the Poenitz model. Not full MATLAB OCamCalib parity. Robust loss, held-out validation, and broad physical lens coverage remain outstanding. |
| Sky photo to binary mask | `SkyPhotoMasker.CreateMaskDetailed`; OpenCV preprocessing, `InferenceSession`, `session.Run`, thresholding and original-size PNG reconstruction | Implemented with all four real B4–B7 ONNX models. CPU fallback is real inference. Optional historical LightGBM refinement is not included. |
| Historical irradiance extraction | `IrradianceClient.FetchAsync` / `PullDetailedAsync`; direct HTTP, JSON/CSV parsing, validation and XLSX writing | Six adapters implemented. Saved live verification exists for NASA POWER and Open-Meteo. NSRDB, CAMS, Oikolab and CDS have synthetic contract tests, not successful live-service evidence. |
| Solar position | `SolarPositionModule.Calculate` / `Prepare`; local NOAA/Meeus equations and interval directions | Implemented in C#, without an astronomy service. Geometric positions; transposition has an explicit apparent-angle adapter. |
| Direct and diffuse shading factors | `SkyShadingModule`; calibrated ray projection, solar-disk quadrature, temporal integration, coverage bounds | Implemented for a horizontal receiver with a constant isotropic diffuse sky. Camera tilt changes camera pose, not receiver tilt. |
| Horizontal shading correction | `ShadingCorrectionModule.Apply` | Implemented, including timestamp matching, unknown factors and invalid inputs. Returns horizontal components in memory. No final corrected-result exporter is provided by this module. |
| Unshaded tilted-panel irradiance | `TranspositionModule` / `SkyTransposition`; Hay–Davies, Perez–Driesse and isotropic formulas | Implemented and checked against frozen pvlib data. No image/mask input and no shaded panel result. |

Useful implementation anchors:

- [Calibration entry point](../src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs), including explicit rejection of unconverged subset/final refinement.
- [Real model loading and inference](../src/SkyPhotoMasking/SkyPhotoMasker.cs), methods `CreateSession` and `Run`.
- [Provider HTTP dispatch and parsers](../src/IrradianceDataExtractor/SolarShade.Irradiance/Providers.cs) and [CDS REST implementation](../src/IrradianceDataExtractor/SolarShade.Irradiance/CopernicusCds.cs).
- [Horizontal ray weights and diffuse integral](../src/SolarPositionAndShading/SolarShade.Shading/SkyShading.cs), methods `Disk`, `Evaluate`, and `ComputeDiffuse`.
- [Correction input/output types](../src/ShadingCorrectionApplication/SolarShade.ShadingCorrection/ShadingCorrectionModule.cs).
- [Unshaded transposition calculation](../src/IrradianceTransposition/SolarShade.Irradiance.Transposition/TranspositionModule.cs).

## Fresh verification performed

Commands executed from the repository root:

```powershell
dotnet build SolarShade.sln -c Release --no-restore
dotnet test SolarShade.sln -c Release --no-build --no-restore --logger "trx;LogFilePrefix=backend-audit" --results-directory artifacts/backend-audit/test-results
dotnet run --project src/SkyPhotoMasking/Validator -c Release --no-build --no-restore -- --output artifacts/backend-audit/masking
dotnet run --project src/IrradianceTransposition/Validator -c Release --no-build --no-restore -- src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905 artifacts/backend-audit/transposition
dotnet run --project src/SolarPositionAndShading/Validator -c Release --no-build --no-restore -- --output artifacts/backend-audit/shading --repeats 1 --nasa --no-images
```

Build: **zero warnings, zero errors**. Tests: **148 passed, zero failed, zero skipped**.

| Test assembly | Passed |
|---|---:|
| Calibration | 43 |
| Masking | 15 |
| Irradiance extraction | 24 |
| Solar position/shading | 29 |
| Shading correction | 10 |
| Transposition | 27 |

All four bundled ONNX models ran twice at 1024 resolution through DirectML on the RTX 3070. The validator checked repeat identity, binary pixels and original dimensions. Cold complete calls took approximately 2.46–3.14 seconds; warm calls 216–259 ms. The unit suite also executed real B4 CPU fallback at 512. All four ONNX files exist, totaling 642,545,451 bytes.

The transposition validator passed its 1,308 frozen reference cases and 151 refraction cases, with maximum errors approximately 1.71e-12 W/m² and 1.42e-14 degrees. Four provider/model combinations each completed 744 rows and XLSX export. These used saved provider inputs, not fresh network downloads.

The shading validator completed both provider samples. Its Open-Meteo file pipeline took about 669 ms first call and 83 ms on the next call. These are smoke-run observations, not statistical benchmarks. The computed diffuse factor was 0.3235749, with coverage 0.8401031 and loss bounds 0.1636781–0.3235749.

Evidence is under [artifacts/backend-audit](../artifacts/backend-audit/). No production code was changed. The fresh test suite exercised frozen calibration parity and synthetic detection/recovery; this audit did not rerun calibration on the original physical photo collection. Original Python/MATLAB oracles were not rerun, nor were live credentialed APIs, an installer, or a clean older PC. Passing the current tests does not establish arbitrary-image or field accuracy.

## What remains before an integrated interface

### 1. A unified application workflow

There is a partial file pipeline for saved irradiance + mask + calibration to shading workbooks. There is no production entry point coordinating all modules from user inputs to final results. Most downstream calculation APIs already accept typed data, so repeated Excel round trips are unnecessary.

The application layer must own site/time-zone/provider settings; calibration, validated angle and lens-disk metadata; camera heading/pose; model/session lifetime; interval conventions; errors, background work and result persistence/export. Masking and full calibration are synchronous and do not expose cancellation tokens at their public full-call entry points. UI cancellation therefore needs explicitly defined behavior rather than assuming every stage can stop immediately.

The camera/profile path needs particular care: equal image dimensions do not establish that a calibration belongs to the selected physical lens. Pass the masking result's measured disk into shading, preserve EXIF-oriented coordinates, and retain coverage assumptions in the final result.

### 2. Shaded tilted-panel calculation, if required

The current direct shading integral weights rays by `max(0, ray.Up)`. Its diffuse calculation uses a horizontal cosine-weighted hemisphere. The correction API accepts `IrradianceSample` and returns `CorrectedIrradianceSample` with horizontal field names. Conversely, transposition takes unshaded horizontal data and a panel orientation, and has no mask input.

Multiplying an hourly unshaded panel value by the existing horizontal hourly visibility is generally not the same as integrating visibility with panel incidence at each substep. The existing constant diffuse factor also does not account for Hay–Davies/Perez circumsolar and horizon contributions on a tilted receiver. Feeding shaded horizontal values back into the transposition model is not a general remedy: it changes the inferred atmospheric inputs.

Implement a common interval calculation that combines beam visibility with panel incidence, and define masked diffuse treatment consistently with the chosen model. Validate open sky, fully blocked sky, horizontal identity under matching assumptions, changing obstructions within an interval, and tilted receivers. Until then, the interface can correctly offer horizontal shaded irradiance and separate unshaded panel irradiance, but should not label a naive combination as validated shaded panel irradiance.

### 3. Validation and deployment completion

- Exercise the four credentialed providers with real configured accounts before representing them as live-verified.
- Validate the complete composed workflow, including failure/cancellation and preservation of coverage information. Module tests do not replace this.
- Publish and test the combined Windows x64 application with its native OpenCV/ONNX/DirectML dependencies and selected ONNX assets. Mixed .NET 8 libraries and .NET 10 Windows libraries build successfully here; a .NET 10 Windows x64 host fits the current graph.
- Test CPU-only/older physical hardware and a clean deployment environment. Existing fallback tests are useful but are not deployment certification.
- Preserve the documented latency limits. The strict every-call 100 ms shading target is not met; masking has model-load startup cost. Port completion does not imply every performance criterion is complete.

### 4. Calibration acceptance items that need careful interpretation

The old [calibration checklist](CALIBRATION_VALIDATION.md) mixes missing features and missing physical evidence. Board dimensions are already configurable, while another physical board/layout validation is still outstanding. Lens-disk detection now exists separately in masking, and the shading projector rejects rays outside angular/image/disk coverage. Those old unchecked items must not be interpreted as wholly absent code.

Robust loss/bad-corner rejection, held-out calibration validation and broad near-horizon capture remain unresolved. The supplied example uses 66.43 degrees of incident-angle coverage, not a measured full hemisphere. Assuming uncovered sky is blocked is a disclosed conservative policy; it is not an observation of that sky.

## Completion statement

**Seven of seven identified capability areas have genuine C# implementations, with the scope limits above.** This is a capability inventory, not a claim of 100% legacy feature parity or a percentage of total project effort. Full MATLAB OCamCalib features and optional LightGBM refinement are outside the implemented port scope.

For an interface exposing the existing horizontal-shading and separate unshaded-transposition tools, most remaining development is application composition, UI and deployment validation. For a unified shaded tilted-panel estimator, an additional numerical backend stage remains. Future weather forecasting and PV electrical power/energy conversion are not implemented by these historical irradiance libraries; they are additional scope only if those are intended product outputs.
