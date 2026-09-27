# C# module structure

The lens parameter extraction projects and their tests live under `src/OmnicalibFisheyeLensParameterExtractor/`. The shared `src/SolarShade.Core` and `src/SolarShade.Camera` projects remain alongside it for other application modules. The root solution groups the extractor projects into a matching solution folder. Existing assembly names and namespaces are unchanged.

| Project | Responsibility |
|---|---|
| `SolarShade.Calibration.Solver` | `OmniCalibCSharpPort.cs`: complete production pipeline, detector, solver, YAML/debug output and runtime hardware policy. Depends on Core, MathNet and OpenCvSharp. |
| `SolarShade.Core` | Shared observation, solution, profile and validation contracts. No third-party dependencies. |
| `SolarShade.Calibration.Detection` | Compatibility wrapper forwarding old extractor calls to the unified library. No separate detection algorithm. |
| `SolarShade.Calibration.Validation` | Independent validator calculations plus `CalibrationWorkflow`, which composes the solver, validator and native profile export. The project references Solver and Camera for that workflow; the validator's numerical implementation remains separate. |
| `SolarShade.Camera` | YAML/JSON profile loading, effective coverage and dimensional validation, native YAML/profile export, and solar-direction projection. |
| `SolarShade.Calibration.Cli` | Command-line invocation and benchmarking; independent validation only through explicit validation/solver benchmark commands. |
| `tests/SolarShade.Calibration.Tests` | Numerical parity, synthetic recovery/detection, YAML, worker policy and projection tests. |

`OmniCalibrator` remains public for solving frozen observations, but its implementation is now in `OmniCalibCSharpPort.cs`. Reuse the solver project reference plus Core contracts and native runtime dependencies; this is one algorithm source file, not a dependency-free single-file executable.

Production flow: photos -> `OmniCalibCSharpPort.Calibrate` -> YAML, frozen observations, result JSON, solver diagnostics, CSVs and preview overlays.

Independent validation flow: frozen observations + result -> `OmniCalibrationValidator` -> metrics and optional full-resolution review images. Python/MATLAB oracle adapters live under `tools/calibration-validation`; original MATLAB source must be supplied externally.

All maintained C# source remains under `src`. `artifacts/calibration-validation/dedicated-workers` contains the final normal-build measurement and outputs. See `OPTIMIZED_PORT.md` for runtime boundaries and outstanding compatibility checks.

## Application pipeline

| Project | Application-facing responsibility |
|---|---|
| `SkyPhotoMasking` | Segmentation, binary PNG and metadata export/cache loading. |
| `SolarShade.Irradiance` (.NET 8) | Retrieval/import, authoritative source intervals, selection/coverage, numerical workbook transport and irradiance export. Owns the portable solar timeline contracts. |
| `SolarShade.Shading` (.NET 10 Windows) | Solar geometry for those intervals, solar export, camera pose and receiver-plane visibility. |
| `SolarShade.Irradiance.Transposition` (.NET 8) | Horizontal-to-panel conversion, whole-source DNI inference, per-substep diffuse components, unshaded integration and export. |
| `SolarShade.ShadingCorrection` (.NET 10 Windows) | Panel shading, uncertainty bounds, transmission/loss and energy summaries, and shaded/visibility exports. Preserves the original horizontal correction API. |
| `ApplicationFrontend` | Input controls, stage sequencing, caching, progress/cancellation, run manifests and display of returned values. No scientific calculation engine or workbook schemas. |

See [application architecture](APPLICATION_ARCHITECTURE.md) for the dependency direction, stable source-interval contract, stage APIs and Debug Data lifecycle. A library implementation change requires republishing the self-contained executable, but no frontend calculation rewrite when its public interface is unchanged.
