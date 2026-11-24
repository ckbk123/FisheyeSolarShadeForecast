SOLARSHADE - COLLEAGUE EVALUATION PREVIEW 0.3.5

Windows x64. Extract this entire ZIP to a writable local folder, then open
APPLICATION.exe. Keep Example, Data and Debug Data beside the application.
No separate .NET installation is required. The first launch/model use can take
longer while Windows extracts the bundled runtime and model.

Hover over a study control for guidance, or focus it and press F1. Escape closes
keyboard help. Open Help for the full guide while working on your study.

The Orientation map tab illustrates the phone, panel and sun in 3D. Its angles
apply only to the illustration. The map works offline with Microsoft Edge
WebView2 or through the tab's option to open it locally in a browser.

1. Open Solar Irradiance. Load the example or select your study inputs.
   Set each top heading clockwise from true north as if the device lay flat.
   The phone top is its lens end. Raise the marked top edge to set the tilt.
   You can also turn either top while flat. Copy panel pose to phone aligns
   both top headings and tilts once. The lens and panel-front azimuths are
   measured clockwise from south and are undefined at zero tilt.
   Existing studies keep their saved pose.

   Choose sky photo selects a photograph directly. Import existing mask accepts
   an opaque, exact black/white grayscale PNG at the oriented photo dimensions
   without AI. Choose white or black for open sky, name the mask, then import it.
2. Click Update results. Editing inputs alone does not calculate.
   Use Edit mask at the top right of the mask preview to paint a new variant.
   The editor lets you paint across the whole photo and save those strokes.
   Zoom either preview to inspect the compass or the mask and its sun path.
   The green guide on the sky photo and in the editor shows the calibrated FOV.
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

This preview supports imported and edited masks with source provenance and restores
verified accepted studies when you return to matching photos, masks and inputs.
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
