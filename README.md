# Fisheye Solar Shade Forecast

Windows desktop application and C# scientific libraries for estimating solar irradiance and fisheye-image shading. The authoritative repository is [ckbk123/FisheyeSolarShadeForecast](https://github.com/ckbk123/FisheyeSolarShadeForecast).

## Current application — 4 October 2026

The current source builds **Preview 0.2.0**, a portable Windows x64 application with two workspaces: Solar Irradiance and PV Autonomy. Solar Irradiance explicitly updates a study, lets you paint and select immutable variants of its AI sky mask in a modal editor, and exports a complete diagnostic snapshot with the selected mask's provenance. Returning to an exact saved photo, mask and input combination restores its verified accepted result. PV Autonomy evaluates a 24-hour repeating load profile against that accepted shaded result and exports its own CSV, JSON and XLSX files. See [application usage](src/ApplicationFrontend/RUNNING.md), [architecture](docs/APPLICATION_ARCHITECTURE.md), the [mask-editor plan](docs/MASK_EDITOR_IMPLEMENTATION_PLAN.md), and the [Preview 0.1.1 source and export review](docs/SOURCE_AND_EXPORT_REVIEW_2026-10-04.md).

The tag `baseline-2026-09-27` preserves the earlier application before these changes. The four manual-update/export milestones and their 395-test result are historical; see [milestone progress](docs/IMPLEMENTATION_PROGRESS.md).

Requires Windows x64, the .NET 10 SDK (the libraries also target .NET 8), and Git LFS. Install the .NET 8 runtime to run the .NET 8 test projects. Clone with Git rather than downloading the source ZIP so the four ONNX model files are retrieved correctly (about 643 MB in total):

```powershell
git clone https://github.com/ckbk123/FisheyeSolarShadeForecast.git
cd FisheyeSolarShadeForecast
git lfs install --local
git lfs pull
dotnet restore SolarShade.sln --configfile src/ApplicationFrontend/NuGet.Config
dotnet build SolarShade.sln -c Release --no-restore
dotnet test SolarShade.sln -c Release --no-build --no-restore
dotnet run --project src/ApplicationFrontend/ApplicationFrontend.csproj -c Release --no-build
```

The source includes example photographs, irradiance input, the fixed reference run, regression fixtures, model weights via LFS, and native packaging inputs. Local debug runs, caches, build output, validation scratch output and installed packages are excluded. `Deliverable/APPLICATION.exe` and `Deliverable.zip` mentioned below are locally generated outputs, not checked-in files.

See [baseline scope and versioning workflow](docs/BASELINE.md) for the original checkpoint. Third-party licenses and notices are retained in `THIRD_PARTY_LICENSES` and `src/ApplicationFrontend/Packaging/Licenses`.

## Existing library and application documentation

The **PV–battery backend and PV Autonomy workspace** are in `src/PvBatterySimulation` and `src/ApplicationFrontend/Workspaces/PvAutonomy`. They consume the accepted shaded panel irradiance and system/load settings to return hourly battery SoC, unmet load and curtailment. The workspace is explicitly evaluated after Solar Irradiance is current; its result and export remain separate from the irradiance snapshot. See [model, API and validation](src/PvBatterySimulation/README.md) and [application usage](src/ApplicationFrontend/RUNNING.md).

The previously installed local application is in **`Deliverable/APPLICATION.exe`**, with a matching **`Deliverable.zip`** for sharing; those generated outputs are still Preview 0.1.1 and are not Git-tracked releases. Build or publish this source for Preview 0.2.0 and the mask editor. Keep the executable alongside its Example, Data and Debug Data folders; all four segmentation models and the runtime are bundled. Start with **Load example** or select your own inputs, then click **Update results**. See [application usage, imports, validation and packaging](src/ApplicationFrontend/RUNNING.md). Source and integration tests are under `src/ApplicationFrontend` and the library project folders.

The solar-position and fisheye shading library is in **`src/SolarPositionAndShading`**. It exports timestamp/zenith/azimuth XLSX data, computes direct and constant-isotropic diffuse shading from the existing mask/calibration contracts, and writes a separate shading workbook. See [API, defaults and integration](src/SolarPositionAndShading/README.md), [validation and performance](src/SolarPositionAndShading/VALIDATION.md), and [the 0.25° solar-radius overlay](src/SolarPositionAndShading/Validator/results/noon-20260531-radius-0.25-detail.png).

The Python-free sky image masker is in **`src/SkyPhotoMasking`**. `SkyPhotoMasker.CreateMask(imagePath, model)` returns a black/white PNG using one of four bundled pretrained models, with automatic DirectML GPU acceleration and CPU fallback. See [usage](src/SkyPhotoMasking/README.md), [validation and timings](src/SkyPhotoMasking/VALIDATION.md), and [the four-model comparison](src/SkyPhotoMasking/Validator/four-model-comparison.png).

The independent historical irradiance downloader is in **`src/IrradianceDataExtractor`**. It provides a C# async function returning 0/1, local-time three-column XLSX export, six API choices, researcher credential inputs, and a separate validator. See [usage and validation](src/IrradianceDataExtractor/README.md) and [free-provider research](docs/IRRADIANCE_PROVIDERS.md).

Unshaded irradiance correction for panel tilt and azimuth is in **`src/IrradianceTransposition`**. It provides selectable Hay–Davies and Perez–Driesse models, interval-aware solar geometry, separate direct/sky-diffuse outputs and local-time XLSX export. See [the C# API](src/IrradianceTransposition/README.md) and [pvlib validation with May 2025 sample files](src/IrradianceTransposition/VALIDATION.md).

The production image-to-calibration implementation is in **`src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs`**. It reads JPEG/PNG checkerboard photos, detects and refines corners, solves the Poenitz/Scaramuzza lens model, writes `calibration.yml`, and writes observed-versus-fitted corner CSVs and annotated preview JPEGs under `debug/`.

```csharp
using SolarShade.Calibration.Solver;
var run = new OmniCalibCSharpPort().Calibrate(imageDirectory, outputDirectory);
Console.WriteLine(run.TotalMilliseconds);
```

Defaults: 6 x 9 **inner corners**, 22 mm squares, degree 4, 1,000-pixel maximum preview dimension. Board dimensions, square size, solver options and preview size are configurable. Full-precision CSV coordinates refer to the oriented original image. This is checkerboard reprojection output, not dense equirectangular image rectification.

The implementation is a native C# port of [Poenitz py-omnicalib](https://github.com/tasptz/py-omnicalib), not complete feature parity with MATLAB OCamCalib (which also fits affine terms). See [the comparison](docs/PORT_COMPARISON.md).

All production algorithms and hardware policy live in that one source file. Shared contracts remain in `SolarShade.Core`; dependencies are MathNet.Numerics and OpenCvSharp. The old detection project is only a compatibility wrapper. Independent C# validation, tests, and Python/MATLAB oracle adapters remain separate.

## Run

```powershell
dotnet build SolarShade.sln -c Release
dotnet test SolarShade.sln -c Release --no-build
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- calibrate <image-directory> <output-directory> 6 9 22 4
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- hardware
```

The intended deployment is Windows 10/11 **x64**, including older SSE3 CPUs and 2-core/4-thread machines. AVX2, AVX-512 and CUDA are optional, not requirements. Native OpenCV dispatches supported instructions automatically; image concurrency is capped by logical CPUs and available memory. Actual execution on an older Windows 10 machine is still an outstanding deployment check.

Measured on this Ryzen 7 5700X with 12 photos at 3000 x 4000: **310 ms median / 381 ms P95** for ten warm library calls including YAML, JSON, CSV and preview JPEG writes. Three fresh processes completed in 857–863 ms with warm filesystem caches. There is no universal one-second guarantee for arbitrary images or older hardware. See [image-processing measurements](docs/IMAGE_PROCESSING.md) and [portability evidence](docs/OPTIMIZED_PORT.md).

The solver now uses a wide-SIMD normal-equation kernel when supported, falling back to the original MathNet implementation otherwise. Measured solver time improved by 18–22% on the two frozen observation sets; see [solver acceleration evidence](docs/SOLVER_SIMD.md).

## Documentation

- [API and commands](docs/NATIVE_CALIBRATION.md)
- [Project structure](docs/PROJECT_STRUCTURE.md)
- [Comparison and reference provenance](docs/PORT_COMPARISON.md)
- [Historical numerical validation and coverage limits](docs/CALIBRATION_VALIDATION.md)
- [Historical threading measurements](docs/CALIBRATION_PERFORMANCE.md)

Retain `THIRD_PARTY_LICENSES/py-omnicalib-LICENSE.txt`. Choose a license for the new C# project before public distribution. The original MATLAB implementation is not bundled.


