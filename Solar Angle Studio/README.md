# Orientation map

Select **Orientation map** to the left of **Solar Irradiance** in the desktop app. You can also open **index.html** directly from this folder in a modern browser. Keep the six HTML, CSS and JavaScript files together for offline use.

The diagram keeps its own example angles in memory. In the desktop tab, **Apply angles to Solar Irradiance** copies Panel tilt (1), Panel top heading (2), Phone tilt (3) and Phone top heading (4) in degrees. The forecast uses the illustrated lens-end-uphill pose. Click **Update results** to calculate. The illustrated sun stays in the map. Choose **Panel**, **Phone** or **Sun** to explore an object, then use **Orbit**, **Top** or **Side** to inspect the geometry. Drag or use arrow keys to orbit a focused scene. Scroll, pinch or use + and − to zoom. Home resets the view.

## Read the angles

- **Panel top heading** and **Phone top heading** are measured with the device laid flat. Follow the in-plane arrow to its marked top edge, clockwise from true north. The phone’s lens end aligns with the top of the oriented image (zero additional roll). With a flat phone pointing north, east appears on the left in the photograph.
- **Panel tilt from horizontal** and **Phone tilt from horizontal** raise the marked edge from 0° flat to 90° vertical. The top heading always stays editable. At 90° the physical arrow points straight up and has no horizontal bearing. Its heading when laid flat still sets which way the face points.
- **Panel front azimuth** follows the horizontal projection of the solar-cell front normal. **Lens azimuth** follows the outward optical axis. Both are derived, measured clockwise from geographic south, with west +90°, north ±180° and east −90° or 270°. At 0° tilt both normals point up and have undefined azimuths. In the **South-facing · 28°** example both top edges point north and both front azimuths read 0° (south).
- **Copy panel pose to phone** copies the illustrated panel’s tilt and top heading once, including when flat. The **Flat · tops point east** example lets you rotate the phone image or panel independently, then match their marked ends again.
- **Solar azimuth** is clockwise from true north. **Solar elevation** starts at the horizon and **solar zenith** at vertical up. The angles sum to 90°. A vertical sun has an undefined azimuth.

The example sun position is manual. The desktop calculation uses a site, date and time. Object sizes and the fixed ideal 180° fisheye illustrate the directions. A real study uses its own lens calibration. Vincent Boitier’s equipment convention defines equipment azimuth using the outward normals and places the phone’s lens end uphill. The application’s marked top headings express that pose with one input per device, including a flat panel. Solar and image bearings follow the [desktop orientation](../src/ApplicationFrontend/EquipmentOrientation.cs) and [camera projection](../src/SolarPositionAndShading/SolarShade.Shading/SkyShading.cs).

## Developer checks

Run `node --test tests/geometry.test.cjs` in this folder. After a Windows Release build, run `pwsh -File "Solar Angle Studio/tests/native-parity.ps1"` from the repository root to compare the JavaScript output with the compiled native implementation. The parity test uses PowerShell 7.6 with .NET 10 and Node.js.
