# Camera model policy

The desktop rewrite preserves the repository's OmniCalib central polynomial model as the authoritative fisheye intrinsic model. It must not silently substitute OpenCV's Kannala–Brandt fisheye model.

For a camera-frame ray `q = (X, Y, Z)`, the retained projection is:

```text
theta = acos(Z / ||q||)
rho(theta) = c0 + c1*theta + c2*theta^2 + ...
pixel = principal_point + unit(X, Y) * rho(theta)
```

The current native desktop code can:

- detect checkerboards and solve the lens model and all board poses in C#;
- initialize partial extrinsics, search the principal point, and fit the Scaramuzza polynomial;
- jointly refine six SE(3) variables per pose, the principal point, and incident-angle polynomial coefficients;
- import the legacy `calibration.yml` fields `poly_incident_angle_to_radius`, `principal_point`, and `fov`;
- save the imported calibration as a versioned JSON profile;
- project solar directions with that polynomial for direct and diffuse shading calculations;
- reject rays beyond the calibrated maximum incident angle.

OpenCV remains useful for image decoding, checkerboard detection, sub-pixel corner refinement, and mask processing. Its fisheye calibration routine is not used to generate lens intrinsics.

The native implementation is `src/OmnicalibFisheyeLensParameterExtractor/SolarShade.Calibration.Solver/OmniCalibCSharpPort.cs`. It is a port of Thomas Pönitz's MIT-licensed `py-omnicalib` equations, informed by the original Scaramuzza toolbox. It uses an analytic reprojection Jacobian, local SE(3) increments, parallel per-image initialization, and MathNet linear algebra. OpenCV is used only for detection and image operations.

The importer requires an explicit `fov` value, interpreted as the maximum validated incident half-angle. It does not infer this limit from a polynomial turning point or the rectangular image boundary. See [CALIBRATION_VALIDATION.md](CALIBRATION_VALIDATION.md) for the measured coverage and native-solver acceptance gates.
