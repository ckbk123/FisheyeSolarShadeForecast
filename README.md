# Fisheye Solar Shade Forecast

Windows desktop application and C# scientific libraries for estimating solar irradiance and fisheye-image shading. The authoritative repository is [ckbk123/FisheyeSolarShadeForecast](https://github.com/ckbk123/FisheyeSolarShadeForecast).

## Source baseline — 27 September 2026

The tag `baseline-2026-09-27` preserves the application and libraries before the planned improvements. Milestones 1 and 2 (manual updates and dependency invalidation) are accepted and merged. This development branch implements **milestone 3: one current debug dataset**, durable calibration profiles, safe migration and recovery. Complete export packaging and the PDF recap remain pending. See [milestone progress](docs/IMPLEMENTATION_PROGRESS.md) and [the full specification](docs/MANUAL_UPDATE_AND_EXPORT_PLAN.md).

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

See [baseline scope and versioning workflow](docs/BASELINE.md) for what is preserved, verification results and future branch/PR conventions. Existing scientific library source must remain unchanged during the planned interface/storage/export work. Third-party licenses and notices are retained in `THIRD_PARTY_LICENSES` and `src/ApplicationFrontend/Packaging/Licenses`.

## Existing library and application documentation

The Windows test application is now in **`Deliverable/APPLICATION.exe`**, with a matching **`Deliverable.zip`** for sharing. The folder contains only the executable; all four segmentation models and the runtime are bundled. Start with **Load example** or select your own inputs. See [application usage, imports, validation and packaging](src/ApplicationFrontend/RUNNING.md). Source and integration tests are under `src/ApplicationFrontend`.

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


