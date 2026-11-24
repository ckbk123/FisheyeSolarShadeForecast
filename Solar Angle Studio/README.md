# Orientation map

Select **Orientation map** to the left of **Solar Irradiance** in the desktop app. You can also open **index.html** directly from this folder in a modern browser. Keep the six HTML, CSS and JavaScript files together for offline use.

The diagram keeps its own example angles in memory. Choose **Panel**, **Phone** or **Sun** to explore an object, then use **Orbit**, **Top** or **Side** to inspect the geometry. Drag or use arrow keys to orbit a focused scene. Scroll, pinch or use + and − to zoom. Home resets the view.

## Read the angles

- **Panel tilt from horizontal** and **Phone tilt from horizontal** run from 0° flat to 90° vertical. **Panel front azimuth** follows the horizontal projection of the solar-cell front normal. **Lens azimuth** follows the outward optical axis. Both start at geographic south, with west +90°, north ±180° and east −90° or 270°.
- The illustrated phone places its lens end straight uphill and at the top of the oriented image, with zero additional roll. In the **South-facing · 28°** example the panel and phone both display 28° tilt and 0° azimuth.
- A flat panel or phone has an undefined facing azimuth. The **Image-top bearing** of a flat phone starts at true north. Try the flat example and turn this bearing to see the image compass rotate. With north at image top, east appears on the left. When the phone is vertical its image top points straight up and has no horizontal bearing.
- **Copy panel pose to phone** copies the panel’s current pose once within the diagram. A flat panel preserves the photograph’s heading.
- **Solar azimuth** is clockwise from true north. **Solar elevation** starts at the horizon and **solar zenith** at vertical up. The angles sum to 90°. A vertical sun has an undefined azimuth.

The example sun position is manual. The desktop calculation uses a site, date and time. Object sizes and the fixed ideal 180° fisheye illustrate the directions. A real study uses its own lens calibration. The [equipment proposal](../vincent-boitier-shading-conventions.txt) specifies the phone and panel. Solar and image bearings follow the [desktop orientation](../src/ApplicationFrontend/EquipmentOrientation.cs) and [camera projection](../src/SolarPositionAndShading/SolarShade.Shading/SkyShading.cs).

## Developer checks

Run `node --test tests/geometry.test.cjs` in this folder. After a Windows Release build, run `pwsh -File "Solar Angle Studio/tests/native-parity.ps1"` from the repository root to compare the JavaScript output with the compiled native implementation. The parity test uses PowerShell 7.6 with .NET 10 and Node.js.
