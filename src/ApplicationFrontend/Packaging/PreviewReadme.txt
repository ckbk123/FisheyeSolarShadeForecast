SOLARSHADE - COLLEAGUE EVALUATION PREVIEW 0.2.0

Windows x64. Extract this entire ZIP to a writable local folder, then open
APPLICATION.exe. Keep Example, Data and Debug Data beside the application.
No separate .NET installation is required. The first launch/model use can take
longer while Windows extracts the bundled runtime and model.

1. Open Solar Irradiance. Load the example or select your study inputs.
2. Click Update results. Editing inputs alone does not calculate.
   Use Edit mask on the mask preview to paint a new black/white variant in a
   separate window. Adjust mask opacity and zoom while editing, then save.
   Select the mask for calculation below the preview and update results.
3. Once a complete shaded irradiance result is ready, open PV Autonomy.
4. Enter 24 hourly load values in Wh, panel area and efficiencies, battery
   capacity in Wh and initial charge. Click Evaluate system.
5. Export results from the workspace you are evaluating.

The 24 load values repeat by local hour throughout the entire study. The battery
is not reset at midnight. No unmet load in historical data does not guarantee
supply during future weather. See Help for the model assumptions.

This preview adds immutable edited masks with AI-source provenance and restores
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
