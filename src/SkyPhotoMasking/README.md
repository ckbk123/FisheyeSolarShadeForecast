# Sky photo masking

Python-free Windows x64 C# library. The production implementation is **one file: `SkyPhotoMasker.cs`**. `Models/` contains four ready-to-run ONNX files with the existing trained weights embedded. No model downloads, training, Python, CUDA toolkit, or internet access are required at runtime.

## Two-input function

Add a project reference to `SkyPhotoMasking.csproj`, then:

```csharp
using SolarShade.SkyPhotoMasking;

using var masker = new SkyPhotoMasker(); // keep alive across photos
byte[] png = masker.CreateMask(@"C:\Photos\sky.jpg", SkyModel.EfficientNetB5);
File.WriteAllBytes(@"C:\Photos\sky-mask.png", png);
```

`CreateMask(string imagePath, SkyModel model)` returns **encoded grayscale PNG bytes**. White (255) is sky; black (0) is an obstruction or outside the lens. The returned image retains the input's **EXIF-oriented** dimensions and pixel coordinates. There is no text on the actual mask. Pass a local file path; no remote URL fetching occurs. The function does not overwrite the source or write any files itself.

The choices are `EfficientNetB4`, `EfficientNetB5`, `EfficientNetB6`, and `EfficientNetB7`. Each uses the original U-Net++ network, its complete trained weights, sigmoid output, and original per-model threshold. This implements the original **non-LightGBM** mode. LightGBM is not needed to offer all four neural-network choices.

Use `Task.Run(() => masker.CreateMask(path, selectedModel))` from a desktop UI to keep it responsive. Calls on the same instance are serialized (DirectML requires this); reuse one instance and dispose it at application shutdown. Only the last selected model is cached, bounding GPU memory when users change model selection. Switching models has a cold-load cost again.

## Speed and quality

Default: **1024 × 1024 inference**, preserving the original Python default. Use B5 as an initial UI selection consistent with the old function default; let users compare B4–B7. No ground-truth study was performed to rank their accuracy. B4 is the fastest of these models on the measured machine. Larger models do not guarantee a better mask on every image.

The existing Python 512 mode is available without changing the two-input function:

```csharp
using var fast = new SkyPhotoMasker(new SkyMaskOptions { InputSize = 512 });
byte[] png = fast.CreateMask(path, selectedModel);
```

At 512, fewer pixels represent small branches and narrow gaps. No quantization, altered normalization, changed thresholds, or heuristic sky classifier is used to accelerate either resolution. Output resizing uses nearest-neighbor interpolation to keep the mask strictly binary.

## Hardware acceleration

The library enumerates DXGI adapters, excludes software adapters, probes Direct3D 12 support, and selects the supported adapter with the most dedicated video memory. That is a practical selection heuristic, not a benchmark of every GPU. DirectML supports NVIDIA, AMD, and Intel hardware; only the RTX 3070 was tested here. `Hardware` exposes adapter names, IDs, memory, CPU logical processors, and vector acceleration availability.

DirectML runs with sequential session execution and memory-pattern optimization disabled, as required. Dynamic model dimensions are specialized to the selected resolution at session creation. ONNX Runtime optimizes the graph. CPU execution uses ORT's default physical-core thread selection and hardware vector kernels; idle spinning is disabled. Image tensor preparation also uses row-level CPU parallelism.

`Auto` falls back to the **same model at the same resolution on CPU** if a compatible adapter is absent or DirectML model loading/inference fails. `CreateMaskDetailed()` reports the provider and fallback reason. The GPU provider may use CPU for unsupported operators; the label does not assert that every operation executes on the GPU. To force or diagnose a provider:

```csharp
using var cpu = new SkyPhotoMasker(new SkyMaskOptions { Acceleration = MaskAcceleration.Cpu });
using var gpu = new SkyPhotoMasker(new SkyMaskOptions
{
    Acceleration = MaskAcceleration.DirectML, // explicit GPU mode throws instead of falling back
    DirectMLDeviceId = 0                     // optional DXGI ID; otherwise automatic selection
});
```

## Finding the lens disk

The two-input API automatically finds a complete circular fisheye disk using the rim contrast against the lens housing. It succeeded on the included phone-adapter example. **It is not a universal camera calibration algorithm.** Heavily cropped, very dark, non-circular, or obscured lens boundaries may not be detected. An uncertain detection throws a descriptive exception instead of inventing a sky mask. The detailed result reports the disk and crop so the app can show/check the selected region.

For a known camera, configure the calibrated disk once on the object (the mask function still takes two inputs):

```csharp
using var calibrated = new SkyPhotoMasker(new SkyMaskOptions
{
    Disk = new LensDisk(centerX, centerY, radiusPixels, orientedImageWidth, orientedImageHeight)
});
```

For a centered disk filling the shorter image dimension, `DiskDetection = DiskDetection.Centered` is an explicit alternative. Use the same EXIF orientation, camera resolution, and coordinates as the calibration module. Automatic disk localization does not estimate projection coefficients or north orientation; those remain camera-calibration responsibilities. Pixels outside the disk remain black, and the output is restored to full-image coordinates for downstream shading.

## Validator

From the `Solar Forecast Estimator` repository root:

```powershell
dotnet run --project src/SkyPhotoMasking/Validator -c Release
dotnet run --project src/SkyPhotoMasking/Validator -c Release -- --image "C:\Photos\sky.jpg"
dotnet test src/SkyPhotoMasking/Tests -c Release
```

The default example is `Validator/example_image.jpg`. Outputs go **beside `Validator/Program.cs`**:

- `example_image-efficientnet-b4.png` through `...b7.png`: pure, full-resolution masks.
- `...-labeled.png`: enlarged disk previews with model, input resolution, and timing in a separate border.
- `four-model-comparison.png`: four labeled previews together.
- `validation-report.json`: hardware, crop, provider, cold/warm timings, and errors.

The validator runs each model twice, verifies repeated output, checks dimensions and strict 0/255 values, and returns nonzero on failure. Labels are never inserted into the mask used for shading.

Options: `--size 512`, `--acceleration Cpu`, `--acceleration DirectML`, `--model EfficientNetB4`, `--output <directory>`, `--models <directory>`, `--centered`, and `--diagnostics`. Diagnostics save model inputs and floating-point outputs for the development-only Python comparator. The validator's `SkyMaskValidator.Validate(imagePath)` function can also be called directly from a test harness.

## Deployment / copying the single source file

The supplied project targets .NET 8 Windows x64 and can be referenced by the .NET 10 app. It references:

- `Microsoft.ML.OnnxRuntime.DirectML` **1.24.4** (includes CPU execution).
- `OpenCvSharp4.Windows` **4.13.0.20260627**.

The project copies `Models/*.onnx` to `SkyPhotoModels/` next to the consuming executable for both build and publish. Alternatively set `ModelsDirectory` explicitly. These ONNX files **are the pretrained weights plus computation graph**; shipping the `.pt` copies is unnecessary. Total model size is about 643 MB decimal. A product distributing only selected models can ship only those files and expose only the corresponding choices.

To copy only `SkyPhotoMasker.cs` into another project, add those two package references, enable `AllowUnsafeBlocks`, target x64, and deploy the `SkyPhotoModels` directory. Do not mix CPU/CUDA and DirectML ONNX Runtime native NuGet packages in one output directory. Native OpenCV and ONNX runtime DLLs remain runtime dependencies; deploy the normal build/publish output. A Windows machine needs a working graphics driver for GPU execution and the normal Microsoft VC++ x64 runtime required by native OpenCV.

## Verification and rebuilding models

See `VALIDATION.md` for measured results and scope. `Models/*.json` records source/export SHA-256 checksums and export versions. Original source checkpoints came from the existing `Fisheye Solar Shading Estimator/FishEyes-Travail/FishEyes-Travail/SystemData` folder; no weights were trained or substituted.

Python is used **only** by the development tools:

```powershell
# In an isolated development environment; see Tools/requirements-export.txt.
python Tools/export_models.py "C:\path\to\original\SystemData"
dotnet run --project Validator -c Release -- --diagnostics
python Tools/validate_python_parity.py "C:\path\to\original\SystemData" "C:\path\to\original\inference.py"
```

The comparator executes the original crop/circle helpers, reconstructs the original model with strict weight loading, and uses the same detected disk for both languages. This separates inference parity from camera calibration. Both 512 and 1024 exports were tested against Python.

## Official API references checked during implementation

- [ONNX Runtime C#](https://onnxruntime.ai/docs/get-started/with-csharp.html)
- [DirectML requirements and device selection](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [Session options and dimension overrides](https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.SessionOptions.html)
- [CPU thread management](https://onnxruntime.ai/docs/performance/tune-performance/threading.html)
- [OpenCvSharp documentation](https://shimat.github.io/opencvsharp/)
- [DXGI adapter enumeration](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgifactory1-enumadapters1)
- [PyTorch ONNX export](https://docs.pytorch.org/docs/stable/onnx)
- [EfficientNet export-friendly Swish](https://github.com/lukemelas/efficientnet-pytorch)
