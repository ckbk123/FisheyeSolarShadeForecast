# Port assessment and optimization evidence

Reviewed 2026-09-05. This assessment describes the implementation found before this optimization pass; the current production entry point and measured results are documented separately.

## What was already ported

The repository already contained a substantive native C# implementation of the **Poenitz py-omnicalib calibration method**, rather than only a calibrated-image projector. `OmniCalibrator` implemented homogeneous/SVD initialization, orthonormal pose candidate selection, principal-point search, initial-subset refinement, full-dataset refinement with six pose variables per image, and fitting both polynomial directions. Detection and reprojection output existed in separate C# projects. The production dependencies are OpenCvSharp and MathNet, not a Python or MATLAB subprocess.

The algorithmic stages agree with [upstream calibration source](https://github.com/tasptz/py-omnicalib/blob/main/omnicalib/calibrate.py). Upstream refines the incident-angle polynomial, principal point and rigid poses, while converting back to radius-to-axis coefficients for compatibility. C# replaces upstream automatic differentiation with an analytic Jacobian and local SE(3) increments; this preserves the objective but is not an instruction-for-instruction translation. See [upstream optimizer](https://github.com/tasptz/py-omnicalib/blob/main/omnicalib/optim/reprojection.py).

It is **not a complete feature-equivalent port of original MATLAB OCamCalib**. MATLAB includes an affine image correction, interactive corner repair and recalibration, and MAT/text exports. The C# and Poenitz model use a rotationally symmetric mapping plus principal point without fitting MATLAB's affine `c,d,e`. MATLAB uses image row/column conventions that must be transformed before comparison; its polynomial coefficients cannot be compared directly with the incident-angle polynomial. These distinctions follow the [original tutorial, sections 13, 15 and 19](https://sites.google.com/site/scarabotix/ocamcalib-omnidirectional-camera-calibration-toolbox-for-matlab).

Poenitz YAML uses `extrinsics`, `poly_incident_angle_to_radius`, `poly_radius_to_z`, and `principal_point`; the polynomials use ascending powers. Preserve those fields, radians and pixel units, and attach explicit image-orientation and coverage metadata. See [upstream YAML writer](https://github.com/tasptz/py-omnicalib/blob/main/omnicalib/main.py).

## What the evidence establishes

The existing frozen Python observations, solution and C# parity tests support numerical equivalence on one 12-image, 648-corner dataset. Tests compare reprojection metrics, principal point, projected radii, rotation validity and square-size scaling; a synthetic recovery case is also present. This establishes substantially more than merely sharing the Scaramuzza model, but does not establish parity for arbitrary fisheye images or full-hemisphere accuracy.

Initially, `CALIBRATION_VALIDATION.md` referred to two missing oracle adapters and unavailable MATLAB artifacts. They have now been restored verbatim from the historical project at `C:\Users\baokh\OneDrive\Documents\ChatGPT\Fisheye Solar Shading Estimator`:

- `tools/calibration-validation/run_ponitz_reference.py`
- `tools/calibration-validation/run_matlab_ocamcalib_reference.m`
- `artifacts/calibration-validation/matlab-ocamcalib-22mm-official-settings/{result.json,detected_corners.json,result.mat}`
- `artifacts/calibration-validation/matlab-ocamcalib-22mm-lm/{result.json,detected_corners.json,result.mat}`

The MATLAB records identify R2024b, the image paths, detected points, model and optimizer diagnostics. The official-settings artifact reports 1.8554299103 px 2-D RMSE; its optimization reached the evaluation limit. Restoration is not a new MATLAB execution. The restored MATLAB adapter calls an externally supplied OCamCalib checkout and requires MATLAB Computer Vision and Optimization toolboxes; no external toolbox implementation was copied. The adapter currently reproduces the official-settings run; the historical LM settings are not parameterized in this script.

The Python adapter's detector refines on sharpened full-resolution pixels, whereas the original C# detector refines on the original gray pixels. Thus historical detector differences cannot be attributed solely to OpenCV version. Use frozen observations for solver parity. The MATLAB adapter detects its own corners, so the MATLAB result is an independent physical-model comparison, not a same-observation solver equality test. Absolute image paths in historical artifacts remain historical provenance and may require relocation on another machine.

## Highest-value optimizations

| Priority | Existing cost | Improvement and validation requirement |
|---|---|---|
| 1 | Debug writer decodes each full-resolution color JPEG, encodes it, then decodes the output again for a contact sheet | Reuse detection previews, project coordinates once, write compact numeric corner pairs and annotated previews directly; explicitly report output dimensions and await all writes. |
| 2 | Full-resolution sharpening before reduced-image detection; repeated image reads for hashing | Benchmark reduced-image preprocessing and a single-read path; preserve EXIF orientation and quantify changed detections and residuals against the reference mode. |
| 3 | Dense mostly-zero Jacobian, small per-corner matrices and general SVD of every damped normal system | Use scalar/contiguous kernels, incremental polynomial powers, block normal equations or Schur elimination; prefer Cholesky for positive-definite damped systems with a stable fallback. Retain SVD for initialization and numerical parity gates. |
| 4 | Unbounded fallback cost on difficult or missing checkerboards | Expose detection modes and failed-image reporting; a hard deadline must fail explicitly instead of declaring a partial or unconverged calibration successful. |

Parallelism across images is already implemented. Avoid oversubscribing it with native worker pools: OpenCV documents that `setNumThreads` is not thread-safe and must be configured outside concurrent regions. [OpenCV threading and CPU capability APIs](https://docs.opencv.org/4.13.0/db/de0/group__core__utils.html).

OpenCV offers classic and sector-based checkerboard detection; exhaustive search and accuracy flags change its work and must be measured on representative distorted boards. A detector substitution can change observations, so report detection completeness and reprojection quality alongside time. [OpenCV checkerboard API](https://docs.opencv.org/4.13.0/d9/d0c/group__calib3d.html).

Available AVX/FMA support alone does not demonstrate that a managed loop is vectorized. .NET exposes SIMD and hardware-intrinsic capability checks; use contiguous work and verify actual timing before claiming acceleration. [Microsoft SIMD documentation](https://learn.microsoft.com/en-us/dotnet/standard/simd).

A GPU is a candidate only after checking the device, driver and actual native-library build. Hardware presence alone does not provide a GPU checkerboard implementation or accelerate the current CPU calls. Transfers and startup can outweigh this dataset's small optimizer workload.

## One-second acceptance

Historical documentation reported approximately 0.683 s detection, 0.055 s warm solving, and 2.41 s full-resolution debug output, measured under different conditions. These are not a measured sub-second total. The requested target must time one complete call from image discovery/read through completed YAML and debug-file writes, and specify image count/resolution, detection success, debug format, machine, process warm-up and filesystem-cache state. A measured result for these 12 images is not a universal latency guarantee.
