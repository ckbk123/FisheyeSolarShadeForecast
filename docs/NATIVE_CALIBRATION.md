# Native C# OmniCalib API

Reference `src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/SolarShade.Calibration.Solver.csproj`. Its single implementation source is `OmniCalibCSharpPort.cs`.

```csharp
using SolarShade.Calibration.Solver;
using SolarShade.Core.Models;

var run = new OmniCalibCSharpPort().Calibrate(
    imageDirectory, outputDirectory,
    CheckerboardDetectionSettings.CreateFastDefault() with {
        InnerColumns = 6, InnerRows = 9, SquareSizeMillimetres = 22
    },
    new OmniCalibrationOptions(PolynomialDegree: 4),
    previewMaxDimension: 1000);
```

`Calibrate` returns after all calibration/debug writes and preview disposal. `TotalMilliseconds` includes lock contention, input reads, detection, solving and output writes; it excludes process startup and the caller's logging. Writes use normal OS buffering, not a physical-media durability barrier. No stages run after return.

Output:

- `calibration.yml`: py-omnicalib keys, ascending coefficients, angles in radians, principal point in oriented zero-based x/y pixels and 3 x 4 board poses.
- `observations.json`: exact detected coordinates, input hashes, image dimensions, board and effective detector settings.
- `result.json`: fitted parameters in the independent validator's input schema.
- `solver-diagnostics.json`: initialization selection, convergence and optimizer termination.
- `debug/NNN-image.csv`: observed/expected full-resolution pixels, residual components and Euclidean distance for each corner.
- `debug/NNN-image.jpg`: grayscale preview with green observed circles and red fitted crosses.

Use a fresh output directory when changing datasets; prior unrelated files are not deleted. Missing boards are omitted by the existing detector; check `run.Observations.Images.Count`. Exhaustive fallback or difficult initialization can exceed one second. The solver throws on non-convergence. No fixed deadline silently truncates calibration.

The YAML intentionally omits `fov`: observed checkerboards do not prove a physical usable lens FOV. The existing solar-profile importer requires an independently validated `fov`; calibration YAML is directly usable as lens coefficients, but solar horizon-range certification is a separate step.

## CLI

From the repository root, after `dotnet build SolarShade.sln -c Release`:

```powershell
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- calibrate <images> <output> 6 9 22 4
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- detect <images> <observations.json> 6 9 22 4 1
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- solve <observations.json> <result.json> 4 250
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- validate <observations.json> <result.json> [full-resolution-overlay-directory]
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- benchmark <observations.json> 10 4
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- benchmark-pipeline <images> <output> 10
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- hardware
```

`benchmark` is solver-only. `benchmark-pipeline` measures full library calls with the default 6 x 9 board and writes `benchmark.json`. For repeated fresh processes and independent metrics, run `tools/calibration-validation/benchmark_port.ps1 -ImageDirectory <images>`.

The validation project is not a dependency of the production library. MATLAB and Python checks remain separate; see `PORT_COMPARISON.md` for their provenance and limitations.

The solver defaults to `OmniSolverKernel.Auto`: wide SIMD normal-equation arithmetic when supported, otherwise the original MathNet implementation. `Kernel: OmniSolverKernel.MathNet` forces the original implementation; `Scalar` is an unvectorized comparison mode. `solver-diagnostics.json` includes the selected kernel. See `SOLVER_SIMD.md` for the interleaved benchmark command and measured gains.

The current detector reads each file once and tries normal SB detection, exhaustive SB, then classic detection. Full-resolution sharpening and subpixel refinement remain enabled. `CreateVerificationDefault()` restores classic-first file decoding for reference comparisons. `ProfileDetection(imageDirectory, settings)` returns per-image operation times and attempts; those times overlap across workers. See `IMAGE_PROCESSING.md` for comparisons and current end-to-end measurements.
