# Hardware acceleration in the C# solver

The default solver now contains a measured SIMD implementation in `OmniCalibCSharpPort.cs`. This is actual vectorized arithmetic, not a hardware inventory. The kernel builds the repeated normal equations `JᵀJ` and gradient `Jᵀr` used by Levenberg–Marquardt. Initialization and factorization remain in MathNet.

## Dispatch and implementation

`OmniSolverKernel.Auto` selects the SIMD kernel only when `Vector.IsHardwareAccelerated` is true and `Vector<double>.Count >= 4`. Otherwise it calls the original MathNet matrix-product implementation. This checks exactly the capability the new path uses. No GPU, CUDA, AVX-512 or unrelated feature probes were added.

On the measured Ryzen 7 5700X, each vector contains four doubles (256 bits). JIT disassembly confirms `vmulpd` and `vaddpd` operating on YMM registers; the final disassembly is saved at `artifacts/calibration-validation/solver-simd/jit-validated-loads.txt`. SIMD width and implementation are selected by .NET; no instruction unsupported by its runtime is called. There is no separate, unmeasured AVX-512 optimization.

The implementation reads contiguous columns from MathNet's dense storage, computes one symmetric triangle, and uses four independent vector accumulators. It validates both array slices before `Vector.LoadUnsafe` operations, bounds every vector load to the slice, and handles the remaining elements with scalar arithmetic. The unit tests cover offsets, short inputs, every tail length through 129 elements, and invalid ranges. Floating-point summation order differs, so numerical equivalence is checked rather than bit identity.

The scalar comparison routine is deliberately available as `OmniSolverKernel.Scalar` for benchmarking. It is **not** the automatic fallback: measurements showed the original MathNet path is faster when wide SIMD is disabled. `OmniSolverKernel.MathNet` forces that original path for comparison or application-specific benchmarking.

## Measured complete-solver performance

Windows 11, Ryzen 7 5700X, .NET 10.0.8 x64. Each dataset has 12 images and 648 frozen checkerboard points. Measurements exclude image detection and file output. Each comparison interleaves all three implementations in rotating order, with 15 warmups and 60 measured runs per implementation. The original MathNet path uses the same solver code, tolerances and observations as the new path.

| Frozen observations | Original MathNet median | New SIMD median | Reduction | Original P95 | SIMD P95 |
|---|---:|---:|---:|---:|---:|
| Python detector | 25.38 ms | 20.85 ms | 17.8% | 31.89 ms | 24.20 ms |
| C# detector | 28.70 ms | 22.43 ms | 21.9% | 39.35 ms | 32.01 ms |

For the identical symmetric algorithm, disabling vector arithmetic gave 37.53 ms and 42.18 ms respectively. Thus SIMD is materially helpful within the new kernel, and the whole solver also improves over the prior MathNet implementation. Reported reductions are in elapsed solver time, not equivalent improvements to image-to-YAML latency; image work still dominates that pipeline.

Raw reports are `solver-simd/validated-loads.json` and `solver-simd/csharp-observations.json` under `artifacts/calibration-validation`. Other reports there record intermediate experiments: the first simple SIMD loop did not outperform the original reliably, which is why its loading/accumulation implementation was revised. These are local warm measurements under normal desktop load, not a guarantee for every CPU or dataset.

## Correctness and fallback

34 tests pass normally, with `DOTNET_EnableHWIntrinsic=0`, and with `DOTNET_EnableAVX2=0`. The latter configuration selects the original MathNet fallback, as does disabling all managed intrinsics. These masks verify dispatch behavior on this host; they do not replace physical Windows 10/older-CPU testing.

Tests compare the new normal equations against independent MathNet products, and solve the same frozen observations using all three implementations. Reprojection RMSE agreement is required within `1e-9` pixels; fitted principal points and radial mappings within `1e-4` pixels. Existing Python parity, synthetic recovery and rotation tests also pass.

## Use and reproduce

No caller changes are needed: `Auto` is the default. The production `solver-diagnostics.json` records the selected kernel. To force the original implementation:

```csharp
var options = new OmniCalibrationOptions(Kernel: OmniSolverKernel.MathNet);
```

After building Release, run from the repository root:

```powershell
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release --no-build -- benchmark-kernels artifacts/calibration-validation/ponitz-22mm/observations.json artifacts/solver-kernels.json 60
```

Primary API references: [.NET SIMD](https://learn.microsoft.com/en-us/dotnet/standard/simd), [vector loads](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.vector.loadunsafe?view=net-10.0). [MathNet native providers](https://numerics.mathdotnet.com/Packages.html) are another possible route, but no new native provider or GPU backend is required by this change.
