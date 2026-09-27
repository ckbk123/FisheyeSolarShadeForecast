# Fisheye calibration validation baseline

## Decision

Keep the Scaramuzza central omnidirectional polynomial camera model. Do not replace it with OpenCV's different fisheye model. Treat checkerboard detection, numerical calibration, and reprojection validation as three separate modules with JSON at the boundaries.

The present 12-image set is sufficient to verify a C# port inside the photographed angular range, but it is not sufficient to authorize full-hemisphere solar projection. Its furthest fitted checkerboard corner is only 66.43 degrees from the optical axis, and the fitted fourth-order incident-angle polynomial has a turning point at 79.48 degrees. A polynomial beyond the observations is extrapolation, not calibration.

## Recovered board and coordinate conventions

The historical workbook `C:\Users\baokh\OneDrive\fisheye_to_equirectangular_v2\SystemData\System_Specifications.xlsx`, `Sheet1!A1:C2`, records:

- 6 short-side inner corners;
- 9 long-side inner corners;
- 22 mm square side length.

That means 54 observed intersections and 7 x 10 physical squares. The OpenCV-oriented images are 3000 x 4000 pixels and use zero-based `[x, y]`. The JPEG files themselves are stored as 4000 x 3000 with EXIF orientation 6; MATLAB reads those raw pixels, so its result must be rotated before comparing principal points.

Changing the square size from 22 mm to 33 mm was tested. Intrinsics, rotations, and reprojection errors stayed equal to numerical precision; translations scaled by exactly `33 / 22 = 1.5`. The physical square size therefore establishes the units of camera-to-board translation. It does not affect the recovered lens mapping when all squares are equal.

## Reference implementations

Three implementations were inspected:

1. The original Scaramuzza OCamCalib v3.0 MATLAB toolbox. It uses a linear initialization followed by `lsqnonlin`, includes affine terms `c,d,e`, and represents the inverse projection as an ascending radius-to-axis polynomial.
2. Thomas Poenitz's MIT-licensed `py-omnicalib`. It constrains rotations on SE(3), refines an incident-angle-to-radius polynomial with Levenberg-Marquardt, and is the cleanest source basis for a native C# implementation.
3. Jakarto's GPL-2.0 `py-OCamCalib`. It is useful as a third numerical comparison, but must not be copied into the product. Its logged “RMS” is actually mean Euclidean point distance, its bundle adjustment exposes unconstrained matrix entries, and its `world2cam` affine update deserves an upstream correctness review.

The GPL MATLAB and Jakarto sources remain outside the repository and are used only as test oracles. The in-repository adapters contain no copied GPL implementation.

## Results on `CalibrationImages`

All methods use a fourth-order polynomial and all 12 images / 648 points unless noted.

| Run | Corners | Mean distance | True 2-D RMSE | P95 | Maximum |
|---|---:|---:|---:|---:|---:|
| Poenitz reference | Python OpenCV 5.0 | 1.41384 px | 1.69850 px | 3.18587 px | 5.34258 px |
| Poenitz solver with C# OpenCV 4.13 corners | C# | 1.40430 px | 1.72054 px | 3.16629 px | 10.05487 px |
| Original MATLAB equations and official optimizer settings | MATLAB | 1.48656 px | 1.85543 px | 3.33041 px | 12.79515 px |
| Original MATLAB equations, LM diagnostic run | MATLAB | 1.47553 px | 1.84299 px | 3.33705 px | 12.65002 px |
| Jakarto reference | Python OpenCV | 2.07218 px reported mean | not reported correctly | — | — |

The C# reprojection validator recalculates the Poenitz reference as `1.6984975408643004` px true 2-D RMSE, versus `1.6984975408642997` px in Python. This approximately `7e-16` px difference verifies the cross-language transformation, incident-angle polynomial evaluation, projection, and metric definitions.

The Python and C# detectors agree to 0.188 px mean and 0.396 px at P95. One corner in `1.jpg` differs by 10.868 px because the detector configurations enter different local sub-pixel minima (both preprocessing and OpenCV versions differ). This is why observations are frozen before comparing solvers, and why the production detector needs local-grid/outlier quality checks.

The Poenitz principal point in oriented coordinates is `[1531.0873, 1997.9947]`. The official-settings MATLAB result transforms to approximately `[1528.0085, 1997.8359]`. Within the observed range, their predicted radial mappings differ by no more than about 1.46 px through the 95th-percentile angle (56.12 degrees) and 6.1 px at the single furthest angle of 66.43 degrees. This is strong independent evidence that both implementations recover the same physical lens behavior where data exists.

The original MATLAB limit of `100 * numberOfVariables` function evaluations was reached (`exit_flag = 0`) after 98 iterations. A longer Levenberg-Marquardt diagnostic run also reached its configured evaluation limit, but improved true 2-D RMSE only from 1.85543 to 1.84299 px. These are useful reference fits, not evidence of formal optimizer convergence; a production solver must persist termination reason as well as residuals.

Do not advertise the current fit as “sub-pixel calibration.” The mean is about 1.4 px and the true vector RMSE is about 1.7 px. Both values must be reported.

## Test endpoint

The native console endpoint is `src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Cli`:

```powershell
dotnet run --project src\OmnicalibFisheyeLensParameterExtractor\SolarShade.Calibration.Cli -- detect CalibrationImages artifacts\calibration-validation\csharp-observations.json 6 9 22

dotnet run --project src\OmnicalibFisheyeLensParameterExtractor\SolarShade.Calibration.Cli -- validate artifacts\calibration-validation\ponitz-22mm\observations.json artifacts\calibration-validation\ponitz-22mm\result.json artifacts\calibration-validation\csharp-reprojection
```

`detect` writes image hashes, the exact detector settings, oriented image dimensions, board dimensions, physical object points, and all detected `[x,y]` points. `validate` consumes frozen observations plus a calibration result, computes independently labelled error statistics, and produces observed-versus-fitted overlays.

The reproducible Python and MATLAB oracle adapters are:

- `tools/calibration-validation/run_ponitz_reference.py`
- `tools/calibration-validation/run_matlab_ocamcalib_reference.m`

The Python adapter accepts `--observations-json`, allowing the same C#-detected points to be calibrated without mixing detector differences into solver comparisons.

## Native C# solver result

The native solver is implemented in `src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs` and exposed through `SolarShade.Calibration.Cli` and the production library API. On the frozen Python observations it produces:

- true 2-D RMSE: `1.6984975408642633 px` (Python: `1.6984975408643004 px`);
- mean Euclidean error: `1.4138438697246254 px`;
- principal-point difference from Python: less than `0.000002 px` per coordinate;
- maximum radial-mapping difference through 66.43 degrees: less than `0.000002 px`;
- orthonormal rotations with determinant `+1`;
- exactly `1.5x` translations when square size changes from 22 mm to 33 mm, with unchanged intrinsics and reprojection metrics.

On the C#-detected observations it produces `1.7205368633714988 px` true 2-D RMSE, matching the Python solve on those same corners.

The hardware-aware benchmark command performs one unmeasured warm-up and up to 50 measured solves. On the development AMD Ryzen 7 5700X (`8` cores / `16` logical processors, AVX2 and FMA available, four doubles per SIMD vector), 50 measured solves gave `39.07 ms` minimum, `54.74 ms` median, `73.19 ms` P95, and `77.38 ms` maximum. A cold in-process solve including JIT initialization measured about `271 ms`. Image decoding and checkerboard detection are deliberately excluded from parameter-solver timings.

## Acceptance checklist for the native C# solver

### Input and observations

- [x] Use explicit `inner columns`, `inner rows`, and `square size in mm`; never call all three “checkerboard size.”
- [x] Apply EXIF orientation before producing zero-based `[x,y]` observations.
- [x] Hash input images and persist detected points independently of the solver.
- [x] Detect all 12 current boards in C#.
- [ ] Add a local-grid consistency score and flag isolated corner refinements such as the outlier in `1.jpg`.
- [ ] Make board dimensions configurable and test another physical board size/layout.

### Initialization and optimization

- [x] Port the MIT Poenitz linear partial-extrinsic initializer, orthonormal candidate selection, polynomial fit, and principal-point search.
- [x] Parameterize each pose as six SE(3) variables; never optimize twelve unconstrained entries of a 3 x 4 matrix.
- [x] Jointly refine all poses, principal point, and the non-constant incident-angle polynomial coefficients with a least-squares solver.
- [x] Require orthonormal rotations (`R^T R = I`, determinant `+1`) and correct board cheirality after every solve.
- [x] Add deterministic initialization and convergence/iteration diagnostics.
- [ ] Add optional robust loss or explicit bad-corner rejection, while retaining an unweighted reference mode for exact parity tests.

### Numerical parity gates

- [x] Define true 2-D RMSE as `sqrt(mean(dx^2 + dy^2))` and also report mean, median, P95, maximum, and per-image values.
- [x] Verify the C# projector against the saved Python solution (current difference is at floating-point noise level).
- [x] On the frozen Python observations, keep C# mean reprojection error within 0.05 px of the reference and true 2-D RMSE no worse than 0.05 px above it.
- [x] Compare projected radii over the observed angular range, rather than requiring polynomial coefficients to match term-by-term.
- [x] Verify that changing 22 mm to 33 mm changes translations by 1.5 and leaves intrinsics, rotations, and pixel errors unchanged.
- [ ] Split images into calibration and held-out validation sets; report both errors to expose overfitting.
- [x] Add synthetic-camera recovery tests with known intrinsics and poses.
- [ ] Extend synthetic recovery tests with noise and deliberately corrupted corners.

### Field-of-view safety

- [x] Persist observed radial and fitted angular coverage.
- [x] Detect polynomial turning points; never infer physical FOV by maximizing an unconstrained fitted polynomial.
- [ ] Capture additional boards whose corners reach at least 85 degrees, distributed around the entire image circle.
- [ ] Determine the usable image-circle boundary independently from the checkerboard optimizer.
- [ ] Reject solar rays outside the smaller of validated angular coverage and usable lens-circle coverage.
- [ ] Only enable horizon-to-horizon solar overlays after the new capture passes this coverage gate.

## Required next capture

Keep the existing images, then add images with the board close to the north, east, south, and west parts of the circular rim, plus diagonal rim positions. Maintain full focus and avoid clipped inner corners. A board may be smaller or viewed closer if necessary; metric square size can be entered afterward. The key missing information is angular/radial coverage, not another cluster of central views.

## References and provenance

- Original toolbox documentation and download: <https://sites.google.com/site/scarabotix/ocamcalib-omnidirectional-camera-calibration-toolbox-for-matlab>
- Jakarto comparison implementation: <https://github.com/jakarto3d/py-OCamCalib>
- MIT implementation used as the port basis: <https://github.com/tasptz/py-omnicalib>
- Downloaded reference revisions used locally: Jakarto `2fb7bfdf86f8a723b3a796a972011b537d7c7250`; Poenitz `7d23c7ff94c4f1f4c2d335982432a432ef38869a`.

