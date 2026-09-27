# Optimized production port: evidence and limits

Historical consolidation measurements from 2026-09-05. Subsequent detector-order and single-read improvements now measure 310 ms median end-to-end; see [IMAGE_PROCESSING.md](IMAGE_PROCESSING.md). Implementation: `src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs`.

## Changes

- Consolidated detection, solving, YAML writing, debug projection/output and hardware policy in one production source file. Retained shared contracts and a thin old-API detector wrapper. Validation does not participate in `Calibrate`.
- Reused grayscale previews created during detection. Parallel debug CSV/JPEG output avoids full-resolution color decoding and repeated contact-sheet decoding. Default preview size is at most 1,000 pixels on its long edge; original coordinates remain full precision in CSV.
- Removed small MathNet allocations in each projection/Jacobian evaluation; expanded them into scalar arithmetic. Damped positive-definite normal systems use Cholesky, with SVD fallback. Initialization retains SVD. This preserves the model and objective.
- Bounded image workers by logical processors, a maximum of 12, and currently available memory. Dedicated workers give predictable concurrency; native OpenCV defaults to one thread per image.
- Added finite-positive detector parameter checks, shared locking around the port's native operations, restoration of OpenCV thread settings, and stage/complete-call timing.

## Measured results

Dataset: 12 JPEGs, each 3000 x 4000 after EXIF orientation, 6 x 9 inner corners, 22 mm squares, 648 points, degree 4. Images remain at the historical absolute paths recorded in observations; they were not copied into the source distribution.

Machine: Ryzen 7 5700X, 8 cores / 16 logical processors, approximately 64 GiB RAM, RTX 3070 8 GiB, Windows 11 build 26200, .NET 10.0.8 x64. Input/output are on the existing local OneDrive filesystem. Filesystem caches were warm, not purged.

| Final normal build | Measured total |
|---|---:|
| Ten warm library calls: minimum | 488.36 ms |
| Median | 518.27 ms |
| P95 (interpolated) | 559.24 ms |
| Maximum | 571.31 ms |
| First library call in benchmark process | 1060.68 ms |
| Three additional fresh-process library calls | 945.53 / 962.14 / 973.63 ms |
| Same calls including executable launch through `dotnet` | 1072.64 / 1074.99 / 1112.97 ms |

All 10 warm calls included input reads, detection, solve, YAML, observation/result/diagnostic JSON, 12 CSVs and 12 preview JPEGs. Benchmark-report output is outside the pipeline measurement. Preview disposal is included in the returned full-call time. OS buffered writes finish before return; physical flush-to-media and OneDrive synchronization are not measured.

**The warm dataset target is met. An unconditional one-second cold-process target is not met.** An arbitrary image count, larger frames, slow disks, difficult/no checkerboards, and older processors cannot share a fixed latency promise.

A ReadyToRun experiment did not improve the observed total: five fresh processes took 1104.62–1328.12 ms. No ReadyToRun setting was made a project default. [Microsoft documents the tradeoff](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run): reduced JIT work can be offset by larger binaries and less optimized initial code.

Raw final results and images: `artifacts/calibration-validation/dedicated-workers/`. Prior normal worker-pool comparison: `optimized/` (568.62 ms median). ReadyToRun experiment: `ready-to-run/fresh-processes.json`. These comparisons are indicative local measurements, not controlled CPU-isolated trials.

## Accuracy

The new detector's 648 coordinates match the pre-change C# run exactly (maximum difference 0 px). The independent validator reports 1.7205368633715132 px 2-D RMSE on newly detected C# corners; recomputing RMSE from the debug CSVs produces the same number. The earlier C# fit was 1.7205368633714988 px.

Frozen Python-observation parity remains approximately 1.69849754086430 px. The test suite checks radius/principal-point parity, proper rotations, square-size invariance, synthetic camera recovery, YAML semantic parsing under a non-English culture, synthetic checkerboard locations and serial/parallel detection equivalence. 29 tests passed both normally and with managed intrinsics disabled/native AVX2 paths disabled.

These tests do not turn the fit into subpixel accuracy or validate unobserved lens edges. Existing detector outliers and fitted angular coverage still apply. The restored MATLAB results are historical independent-model comparisons, not newly rerun MATLAB validation; see `PORT_COMPARISON.md`.

## Hardware portability

The library checks CPU count and available memory to choose image-worker concurrency. A subsequent solver optimization now also checks whether wide SIMD is usable, selecting a vectorized normal-equation kernel or the original MathNet fallback. This check controls actual arithmetic; unrelated instruction-set and CUDA diagnostic probes remain removed. OpenCV selects its own native implementations internally. See [the measured solver update](SOLVER_SIMD.md).

Bundled OpenCV build inspected during the prior validation (not probed by the production worker policy):

- Required baseline: **SSE, SSE2, SSE3**.
- Optional dispatch: SSE4.1, SSE4.2, AVX, FP16, AVX2, AVX512_SKX.
- This host supports AVX2/FMA, but not AVX-512. OpenCV performs its own native dispatch; no managed AVX512F check is needed for those calls.
- No CUDA support in the bundled native library. The RTX 3070's presence does not accelerate this pipeline. There is no mandatory GPU dependency. The managed SIMD update and its portable MathNet fallback are documented in `SOLVER_SIMD.md`.

Sandy Bridge-era x64 CPUs satisfy this native baseline. A 2-core/4-thread system selects at most four image workers, reduced further for memory pressure. The memory estimate reserves 512 MiB and allows 128 MiB per worker; it is appropriate to the measured 12-megapixel workload, not a hard memory bound for arbitrary resolution. Unknown availability selects one worker. Windows memory is queried through [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex), with the GC budget also respected. This avoids treating an uninitialized GC snapshot as an empty machine.

The managed math uses portable operations. The native library independently selects its [supported CPU implementations](https://docs.opencv.org/4.13.0/db/de0/group__core__utils.html). The shared lock coordinates this library's OpenCV operations only; hosts calling `SetNumThreads` elsewhere must coordinate their own native operations.

Intended deployment: **Windows 10/11 x64**, .NET 10 x64 and the OpenCvSharp native dependencies. The framework name `net10.0-windows` does not mean Windows 11-only. Actual Windows 10 and physical 2-core execution are not available on this host and remain release checks. Microsoft's supported Windows editions/builds also follow its [current .NET OS lifecycle matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md); compatibility intent does not imply every Windows 10 release is currently serviced.

A feature-masked check used `DOTNET_EnableHWIntrinsic=0`, `DOTNET_PROCESSOR_COUNT=4`, and `OPENCV_CPU_DISABLE=AVX2,AVX512-SKX,FP16`. All tests passed, and the pipeline still found 12 boards and wrote all outputs. The initial feature-masked four-worker call took 1458.87 ms. This is fallback evidence on a Ryzen CPU, **not** an emulation or benchmark of an old i3.

## Reproduce and remaining improvements

```powershell
./tools/calibration-validation/benchmark_port.ps1 -ImageDirectory <image-directory> -Runs 10 -FreshProcesses 3
```

The benchmark uses the default board geometry. For other boards, call `Calibrate` with explicit settings or the `calibrate` CLI endpoint. Use a fresh output directory for a new dataset so unrelated prior debug files are not mistaken for current results.

The remaining largest cost is native JPEG decoding, full-image sharpening and checkerboard detection. Reduced JPEG decoding and skipping the full-resolution sharpening pass are possible experiments, but change corner localization and need independent detection-quality gates before replacing this parity-preserving default. For much larger datasets, block normal equations/Schur elimination and contiguous residual storage are better candidates than adding GPU startup and transfers to the current small solver. Robust loss, bad-corner rejection and held-out images address quality rather than the measured startup bottleneck.

The diagnostic cleanup left 28 tests; subsequent SIMD implementation and fallback tests bring the current suite to 34. See `SOLVER_SIMD.md` for current solver evidence.



