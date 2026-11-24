# SolarShade Preview 0.3.5 calibration audit

The October field images showed two distinct effects. Limited checkerboard coverage explained the green calibration circles. The automatic mask finder could then place a smaller, displaced circle over the sky image. The red **CALIBRATION FAILED** stamp identifies a failed checkerboard run. A successful fit can still have limited angular coverage or a poor automatic mask.

## What each circle measures

The green circle uses the largest angle between the fitted optical axis and any accepted checkerboard corner. The displayed full field is twice that angle. The mask finder separately estimates a disk from image brightness. The usable circular field is the largest complete cone around the fitted optical axis contained in the photo and mask disk. Some directions can extend beyond that cone. [Calibration geometry](../src/SolarShade.Core/Models/OmniCalibrationModels.cs) · [Mask disk detection](../src/SkyPhotoMasking/SkyPhotoMasker.cs) · [Preview calculation](../src/ApplicationFrontend/Workspaces/Irradiance/SkyPhotoFieldOfView.cs)

Eight nominal 235° calibration sets yielded 97 accepted boards and 5,238 corners. Only 30 fitted corners exceeded 90° off-axis. None reached the 117.5° half-angle implied by the lens rating. The green circles represented full fields of **171.87–186.66°**. In a synthetic control with a known 235° lens, the solver recovered 186.66° using the board positions from one field set. Adding eight board poses reaching 117.5° yielded 235.00° with exact observations and 234.96° with 0.35 px of coordinate noise.

## How far the automatic disk moved

We traced the visible scene-to-housing rim independently on 32 sky photos. Sixteen used the nominal 235° lens and sixteen used the nominal 180° lens. Some photos were repeated placements from one setup. The rim center is an approximate image measurement. Verifying the physical optical axis or the angle at that rim requires an independently positioned angular target.

| Check | Nominal 235° | Nominal 180° |
| --- | ---: | ---: |
| Auto disk centers more than 100 px from the traced rim center | 11 of 16 | 0 of 16 |
| Directly dimension-matched skies losing more than 20° of usable circular field with Auto | 6 of 10 | 0 of 10 |

On one 235° image the checkerboard optical center was about 15 px from the traced rim center. The automatic mask center was about 194 px away and its radius was about 202 px smaller than the traced radius. With Auto the usable full field fell from **186.66° to 130.03°**. The directly dimension-matched subset was used for the angular comparison because the other sky images required rotation into the checkerboard frame.

## Checks on the solver

- The upstream Python implementation and SolarShade fitted the same 235 boards across 18 sessions. Their optical centers agreed within 0.000018 px. The original Scaramuzza toolbox model, fitted separately to the same corners from all eight nominal 235° sets, produced centers within 3.82 px of SolarShade. Agreement on the same inputs tests implementation and model differences. It does not establish the true axis. [Upstream Python revision](https://github.com/tasptz/py-omnicalib/tree/7d23c7ff94c4f1f4c2d335982432a432ef38869a) · [Scaramuzza toolbox](https://sites.google.com/site/scarabotix/ocamcalib-omnidirectional-camera-calibration-toolbox-for-matlab)
- The shared initializer loses the quadrant of rays behind the camera plane in one arctangent calculation. Correcting that operation in an isolated comparison moved the final optical centers by less than 0.00000000002 px across these sessions.
- Some accepted checkerboard corners visibly missed their printed intersections. Removing each accepted board in turn moved the fitted center by up to 11.0 px across the 235° sets. The largest mask errors persisted independently of the checkerboard solver.

The first targets for a future change are the automatic disk finder and the labeling of the checkerboard-supported limit. Measurements near 117.5° on a fixed phone and lens assembly are still needed to validate the real attachment's outer field.
