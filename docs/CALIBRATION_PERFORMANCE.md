# Calibration threading benchmark

## Result

Parallelize independent images. Do not rely on OpenCV's internal thread count as the primary acceleration mechanism.

On the 8-core / 16-logical-processor Ryzen 7 5700X, the complete 12-image C# extraction stage fell from a median 2.716 seconds to 0.683 seconds with 12 image workers and one OpenCV thread. This is a 3.98x speedup. The absolute fastest measured configuration was 12 image workers and four OpenCV threads at 0.664 seconds, but the 0.019-second difference is too small to justify making nested parallelism the portable default.

The application fast default is therefore:

```text
image workers = min(image count, logical processors, 12)
OpenCV threads = 1
```

This default uses one independent work item per image where the processor permits it and caps the memory cost at 12 concurrent images. For this computer and image set, it resolves to 12 workers and measured 0.683 seconds. The verification profile remains one worker and one OpenCV thread.

## Requested comparison

All values are medians of three warm-cache runs. The timed operation includes a fresh .NET process, JPEG decoding, two sharpening passes, resizing, checkerboard detection, full-resolution sub-pixel refinement, SHA-256 hashing, and JSON output.

| Image workers | OpenCV threads | Median | Speedup vs 1/1 |
|---:|---:|---:|---:|
| 1 | 1 | 2.716 s | 1.00x |
| 1 | 4 | 2.672 s | 1.02x |
| 1 | 6 | 2.821 s | 0.96x |
| 1 | 8 | 2.804 s | 0.97x |
| 4 | 1 | 1.129 s | 2.41x |
| 6 | 1 | 0.946 s | 2.87x |
| 8 | 1 | 0.970 s | 2.80x |
| 6 | 6 | 0.848 s | 3.20x |
| 8 | 4 | 0.811 s | 3.35x |
| 10 | 4 | 0.763 s | 3.56x |
| 12 | 1 | 0.683 s | 3.98x |
| 12 | 4 | 0.664 s | 4.09x |

With sequential images, increasing OpenCV from one to four threads saved only 0.044 seconds; six and eight were slower than one. Four, six, and eight image workers provided the meaningful improvement. Because the workload has exactly 12 images and the processor exposes 16 logical processors, allowing all 12 images to enter the work queue improved it further.

## Correctness

Sixty-six result files were compared against the 1-worker / 1-thread reference. Every run detected 12 complete boards and all 648 `[x,y]` corner coordinates were bit-for-bit identical. Results are stored by source-image index, not task completion order.

`Cv2.SetNumThreads` is process-global and not thread-safe. The extractor calls it once before starting workers and holds a process-wide calibration lock until all OpenCV work finishes.

## Memory tradeoff

| Configuration | Observed peak working set |
|---|---:|
| 1 worker / OpenCV 1 | 179 MiB |
| 6 workers / OpenCV 1 | 298 MiB |
| 8 workers / OpenCV 4 | 354 MiB |
| 12 workers / OpenCV 4 | 417 MiB |

For machines with limited RAM, six image workers are a reasonable low-memory profile and still complete this stage in under one second. Overlay creation should remain a separate stage because full-resolution color images require more memory.

## Native parameter solver

The native C# optimizer is now implemented. It parallelizes the independent algebraic pose initializations, uses an analytic reprojection Jacobian, keeps rotations on SE(3), and solves the dense least-squares systems with MathNet.Numerics. On this machine, 50 warmed parameter solves measured 39.07 ms minimum, 54.74 ms median, 73.19 ms P95, and 77.38 ms maximum. The cold in-process solve, including JIT and numerical-library initialization, measured about 271 ms. Both are below the requested one-second parameter-solve target.

The runtime reports 16 logical processors, hardware-accelerated four-wide `double` SIMD vectors, AVX2, and FMA on the Ryzen 7 5700X. AVX-512F is not available. The solver uses all available logical processors up to the number of independent images during initialization.

The native result matches the Python reference at floating-point noise scale: 1.6984975408642633 px versus 1.6984975408643004 px true 2-D RMSE. The old cold Python reference solve took about 16.64 seconds after corners had already been detected.

Reprojection metric calculation and annotated overlay generation remain separate from the solver-only benchmark. Producing all 12 annotated JPEGs plus the contact sheet takes about 2.41 seconds and is an output/I/O workload, not parameter optimization.

Raw benchmark results are in `artifacts/calibration-validation/threading-benchmark-2026-09-05.json`.
