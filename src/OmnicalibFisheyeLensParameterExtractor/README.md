# Omnicalib fisheye lens parameter extractor

This folder owns the image-to-lens-parameters module and its independent validation tests.

- `SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs`: production image reading, checkerboard detection, parameter solving, YAML and debug output, and runtime acceleration policy.
- `SolarShade.Calibration.Detection`: compatibility wrapper for detection callers.
- `SolarShade.Calibration.Cli`: command-line entry point and benchmarks.
- `SolarShade.Calibration.Validation`: independent result validation.
- `tests/SolarShade.Calibration.Tests`: detection, solver, pipeline, and integration tests.

The module references shared contracts in `../SolarShade.Core`. Tests also exercise the profile importer/projector in `../SolarShade.Camera`. Those shared projects remain outside this module for use by other application modules. Assembly names and namespaces are unchanged.

Build and test from the repository root with `dotnet build SolarShade.sln -c Release` and `dotnet test SolarShade.sln -c Release`. Run the CLI with `dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release -- <command>`.

Documentation remains in repository root `docs/`; reference adapters and benchmarks remain in `tools/calibration-validation/`, and recorded outputs remain in `artifacts/calibration-validation/`.
