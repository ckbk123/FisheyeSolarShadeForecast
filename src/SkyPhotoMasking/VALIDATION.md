# Recorded validation

Hardware: Ryzen 7 5700X (8 cores / 16 logical processors), approximately 64 GiB RAM, NVIDIA RTX 3070 (8 GiB), Windows x64. The C# hardware probe found DXGI adapter 0 and selected DirectML. Native ONNX Runtime 1.24.4; OpenCvSharp 4.13.0.20260627. All four original state dictionaries loaded strictly, without missing or unexpected keys.

Input: `Validator/example_image.jpg`, the original project's supplied photo. OpenCV applies EXIF orientation, producing 3000 × 4000 pixels. Automatic disk detection returned approximately `(1521.88, 2003.13)`, radius `882.92`; the inclusive square crop is `(639,1120,1767,1767)`. The source shows sky, clouds, foliage, a building edge, and a surrounding phone-lens housing. Automatic disk detection was also tested on an offset synthetic disk and a blank image.

## Measured complete-call latency

These are individual warm calls after one model load, **not statistical medians or latency guarantees**. GPU and CPU benchmarks ran separately. Complete-call time includes image decoding, disk detection, preprocessing, inference, restoring the original dimensions, PNG encoding, and enabled diagnostic input encoding. Writing the returned PNG to disk is outside the call. The real app can leave diagnostics disabled.

| Model | GPU 1024 warm | GPU 1024 inference only | CPU 1024 warm | GPU 512 warm |
|---|---:|---:|---:|---:|
| B4 | 261 ms | 56 ms | 1450 ms | 152 ms |
| B5 | 238 ms | 70 ms | 1720 ms | 163 ms |
| B6 | 248 ms | 81 ms | 1957 ms | 166 ms |
| B7 | 269 ms | 101 ms | 2592 ms | 163 ms |

Latest 1024 GPU first calls took 1.27–1.74 s. An earlier run took 1.51–2.66 s. These include session loading/compilation and are affected by OS/driver caches. A model stays loaded until the instance is disposed or a different model is selected. Complete-call times vary with image decoding/encoding, scheduling, and disk detection; network-only time increased with model size. Do not rank model compute cost from a single complete-call measurement.

Recommendation: keep 1024 as the default because it preserves the Python default resolution while taking roughly a quarter second on this machine. Expose 512 for slower hardware. Choose B5 initially to match the original function's default model, and expose all four models for comparison. No labeled accuracy data were supplied to establish an objectively best model or prove that B7 is more accurate on every scene. Python GPU latency was not benchmarked; do not interpret Python CPU comparisons as a language-only speedup.

Raw evidence:

- `Validator/validation-report.json`: GPU 1024.
- `Validator/Fast512/validation-report.json`: GPU 512.
- `Validator/Cpu1024/validation-report.json`: forced CPU 1024.

## Python parity

The comparator used the original Python `crop_around_disk` and `fix_circle` functions, extracted directly from `inference.py`. Both implementations receive the **same detected disk**. The network is reconstructed in segmentation-models-pytorch 0.3.4, original checkpoints load strictly, and the reference runs the original EfficientNet memory-efficient Swish implementation in evaluation mode. Export uses an equivalent export-friendly Swish. RGB input, `/255` normalization, thresholds, inversion, and interpolation match the source.

| Execution | Preprocessed pixels | Final mask disagreement, B4 / B5 / B6 / B7 |
|---|---|---|
| DirectML 1024 | Exact match | 0 / 0 / 0 / 0 pixels |
| DirectML 512 | Exact match | 0 / 0 / 0 / 0 pixels |
| CPU 1024 | Exact match | 4 / 0 / 4 / 0 pixels out of 12,000,000 |

GPU maximum absolute probability differences were below 0.000014. CPU differences were below 0.00028. CPU B4 and B6 have very small changes at threshold-adjacent pixels; this is expected numerical variation between native kernels. Reports are `python-parity.json` in the corresponding validator directories. `PythonReference/` contains full reference masks.

Acceptance gates: probability mean absolute error < 0.0001; maximum absolute error < 0.01; classified-pixel disagreement inside the model disk < 0.1%. All passed. Each C# validator also verifies repeated mask identity, original oriented dimensions, and only 0/255 values.

This establishes **port fidelity for the supplied example**, not universal sky-segmentation accuracy. It does not compare automatic disk detection against a calibrated lens boundary or measure downstream annual irradiance error. Different camera optics, exposure, thin branches, and unusual lighting still warrant application-level visual review. No optional LightGBM refinement is included.

## Build and behavior checks

The new module is registered in `SolarShade.sln`; the whole solution builds in Release with no warnings/errors. Module tests cover RGB channel order, all model thresholds including equality, circular boundary exclusion, invalid probabilities, inclusive crop geometry, offset-disk detection, failure on a blank disk, missing images/weights, disposal, camera dimension mismatch, and real-model CPU fallback when a nonexistent GPU ID is requested. GPU/CPU real-model inference is also exercised by the separate validator.

The Windows C# process loads the model files and native DLLs directly. There are no Python subprocesses or Python imports in the production source. The Python export environment lives outside the target repository and is only a development tool.
