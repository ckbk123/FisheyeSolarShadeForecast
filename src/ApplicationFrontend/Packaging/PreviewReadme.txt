SOLARSHADE - COLLEAGUE EVALUATION PREVIEW 0.3.16

Windows x64. Extract this entire ZIP to a writable local folder, then open
APPLICATION.exe. Keep Example, Data and Debug Data beside the application.
No separate .NET installation is required. The first launch/model use can take
longer while Windows extracts the bundled runtime and model.

Hover over a study control for guidance, or focus it and press F1. Escape closes
keyboard help. Open Help for the full guide while working on your study.

In Orientation map, Apply angles to Solar Irradiance copies the four numbered
phone and panel angles in degrees. The example sun stays in the map. The map
runs offline using Microsoft Edge WebView2. A standalone browser can also display it.

1. Load the example, or open Images & calibration to prepare your own sky photo.
   Calibrate with checkerboards or load camera-profile.json from the same camera,
   lens and oriented image geometry. Choose a sky photo, then Generate mask.
   Solar Irradiance shows the same mask. Set the panel and phone angles there.
   Set each top heading clockwise from true north as if the device lay flat.
   The phone top is its lens end. Raise the marked top edge to set the tilt.
   You can also turn either top while flat. Copy panel pose to phone aligns
   both top headings and tilts once. The map shows the two devices side by side. The lens and panel-front azimuths are
   measured clockwise from south and are undefined at zero tilt.
   Existing studies keep their saved pose.

   Choosing a different sky photo clears the previous photo's masks, saved edits
   and study results. To keep its selected mask, copy 02-sky-mask/sky-mask.png
   from Debug Data after Update results and before switching. Import mask accepts
   a mask image at the oriented photo dimensions
   without AI. Choose white or black for open sky, name the mask, then import it.
2. Click Update results. Open Study details beneath the graph for weather and run information.
   Use Edit mask at the top right of the mask preview to paint a new variant.
   The editor lets you paint across the whole photo and save those strokes.
   Show sky grid marks the lens center, 30-degree rings and dotted compass lines.
   Zoom either preview to inspect the sky grid or the mask and its sun path.
   The green circle shows the study edge. To adjust it, choose Refine edge,
   move the slider or enter the full FOV (up to 270 degrees), then Use edge for shading.
   Update results uses that edge for shading and the sun path.
   A CALIBRATION FAILED stamp on the sky photo means the checkerboard run
   failed. Open Review checkerboard photos for the cause. PROFILE UNUSABLE
   means the selected profile could not be applied to this sky photo.
   Select the mask for calculation below the preview and update results.
3. Once a complete shaded irradiance result is ready, open PV Autonomy.
4. Enter 24 hourly load values in Wh, panel area and efficiencies, battery
   capacity in Wh and initial charge. Click Evaluate system.
5. In Solar Irradiance, click Export data and select the Excel columns to include.
   Choose a destination folder and confirm. The default includes panel irradiance
   and shading factors. Add original source GHI or the horizontal direct and
   diffuse components to inspect the weather data. The Weather source sheet
   gives the units and tells you which interval each timestamp represents.
   PV Autonomy has its own Export PV results action.

The 24 load values repeat by local hour throughout the entire study. The battery
is not reset at midnight. No unmet load in historical data does not guarantee
supply during future weather. See Help for the model assumptions.

For the active photo, SolarShade can restore a previously calculated mask and
study configuration. Selecting a different photo starts a new working study.
Full scientific diagnostics are retained.
A full-year update still takes longer than a month; Stop cancels acceptance of
pending results even if an active native operation must finish in the background.

Data contains your saved session. Debug Data contains the current calculation
and PV results. The sibling Debug Data.lock file prevents simultaneous writers;
it may remain empty after closing and is not an error. Old historical run folders
are retained rather than scanned each time you edit. Close a previous instance
before opening another copy that uses the same output folder.

For feedback, report the preview version, Windows version, approximate PC specs,
study date range, action taken, elapsed time, and any exact error message.
Include screenshots or exported results if appropriate to share.

Source: https://github.com/ckbk123/FisheyeSolarShadeForecast
