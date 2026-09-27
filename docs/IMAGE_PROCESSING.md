# Image-processing optimization

The production change is in `src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs`. OpenCV itself was not rewritten or rebuilt. We changed which existing detector runs first and eliminated a duplicate source-file read.

## What the profile found

The old sequence ran classic checkerboard detection, followed by exhaustive sector-based (SB) detection if classic failed. On the current 12-photo fisheye dataset, classic failed on 11 images. Its failed work was followed by another successful detector, making this the largest avoidable cost.

Five profiled warm runs gave these mean per-image elapsed times while images were processed concurrently:

| Operation | Old sequence |
|---|---:|
| File read and full-resolution grayscale decode | 86.8 ms |
| Full-resolution sharpening | 19.5 ms |
| Downsampling | 7.5 ms |
| Small-image sharpening | 0.28 ms |
| Classic detector | 228.8 ms |
| SB fallback, averaged over all images | 74.1 ms |
| Full-resolution subpixel refinement | 0.87 ms |
| Preview resizing | 8.1 ms |
| Hashing, including a second file read | 4.1 ms |

These values overlap across image workers. **Do not sum them across images to infer pipeline latency.** The recorded wall time is the latency measurement, and CPU contention affects each operation's elapsed time.

## New default sequence

1. Read the compressed file once into a byte array.
2. Decode with OpenCV `ImDecode`, retaining EXIF orientation.
3. Keep full-resolution sharpening, downsampling, and small-image sharpening.
4. Try normal SB detection first, without the exhaustive flag.
5. If necessary, retry SB with exhaustive search, then the classic detector.
6. Retain full-resolution `CornerSubPix` refinement and preview creation.
7. Hash the same compressed bytes that were decoded.

All 12 current boards succeed at step 4. Harder boards retain the other detector fallbacks. The baseline remains available through `CheckerboardDetectionSettings.CreateVerificationDefault()`; set `ImageWorkerCount` explicitly for a parallel reference run. Defaults and effective detector order are recorded in `observations.json`.

OpenCV's [checkerboard documentation](https://docs.opencv.org/4.13.0/d9/d0c/group__calib3d.html) describes exhaustive search as an additional search for improving detection rate. Its [image codec API](https://docs.opencv.org/4.13.0/d4/da8/group__imgcodecs.html) supports in-memory decoding. The pinned [OpenCV 4.13 source](https://github.com/opencv/opencv/blob/4.13.0/modules/imgcodecs/src/loadsave.cpp) applies EXIF orientation in the decode path; tests verify all eight orientations against file decoding.

## Interleaved comparison

Two warmups and five measured runs per variant, with rotating execution order. Each run includes file reading, full detection/refinement and 1,000-pixel preview construction; solving and output writes are excluded here.

| Variant | Median | Boards | Maximum aligned corner change |
|---|---:|---:|---:|
| Original classic-first pipeline | 536.54 ms | 12/12 | Reference |
| Only change: read once | 507.55 ms | 12/12 | 0 px |
| Read once, exhaustive SB first | 337.59 ms | 12/12 | 0.000977 px |
| **Read once, normal SB first (new default)** | **299.77 ms** | **12/12** | **0.000977 px** |
| Remove full sharpening, exhaustive SB first | 292.13 ms | 12/12 | 11.21 px |
| Remove full sharpening, normal SB first | 323.07 ms | 12/12 | 11.21 px |

The accepted default reduces image-processing time by **44%** in this comparison. Skipping full-resolution sharpening moved one corner substantially. Although that variant's training reprojection RMSE improved, there is insufficient independent evidence to adopt that localization change, and its extra speed was inconsistent. Full-resolution sharpening therefore remains enabled. `SharpenFullResolution=false` is available only as an explicit experimental setting.

One board's point ordering reverses between classic and SB detection. The comparison aligns the equivalent 180-degree board orientation; this is not a movement of physical corners. The solver fits that board's pose accordingly. Mean aligned coordinate change is 0.0000106 px, and 95% of corners are unchanged.

Original/new independently calculated 2-D RMSE: **1.7205368634 / 1.7205412793 px**. Principal-point change is 0.00000630 px. Maximum radial-mapping change through the observed 66.43 degrees is 0.00000263 px. This preserves existing accuracy; it does not imply subpixel calibration or validate unobserved lens angles.

## Complete calibration

With the new default and SIMD solver, ten complete warm library calls measured:

- Median **310.40 ms**, P95 **380.67 ms**, maximum **397.25 ms**.
- Initial call in that process: **747.41 ms**.
- Three additional fresh-process library calls: **744.80–749.06 ms**.
- Those three calls including command-line process startup: **857.36–862.56 ms**.

All calls included YAML, observation/result/diagnostic JSON, 12 corner CSVs and 12 annotated preview JPEGs. No writes were deferred beyond return. Filesystem caches were warm; physical-media flushing and OneDrive synchronization were not measured. These are measurements on the Ryzen 7 5700X / Windows 11 host with 12 images at 3000 x 4000 oriented pixels. They are not a one-second guarantee for arbitrary images or older hardware.

Raw artifacts: `artifacts/calibration-validation/image-processing/variants/benchmark.json`, `pipeline/benchmark.json`, `pipeline/fresh-processes.json`, `quality-summary.json`, and the before/after profiles in the same parent directory.

## Verification and reproduction

43 tests pass, including synthetic checkerboards across worker counts, JPEG orientation 1–8, byte-exact single-read parity, correct source hashes, and detector fallback attempts on blank images. Empty files are reported as rejected by profiling. Tests do not use the user's photo paths.

```powershell
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release -- profile-detection <images> <profile.json> 5
dotnet run --project src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli -c Release -- benchmark-detectors <images> <report-directory> 5
./tools/calibration-validation/benchmark_port.ps1 -ImageDirectory <images> -OutputDirectory <pipeline-output>
```

The profiling/benchmark endpoints currently use the default 6 x 9 inner corners and 22 mm squares. The library API accepts other settings through `ProfileDetection(imageDirectory, settings)` and `Calibrate(...)`.

After the change, normal SB detection and JPEG decoding remain the largest per-image costs. Reduced JPEG decoding would require retaining or re-decoding full-resolution pixels for the current refinement and preprocessing; it was not implemented or claimed as an additional measured improvement. The next generalization check should use more cameras, lighting conditions and board layouts, rather than assume this dataset's preferred detector order wins universally.
