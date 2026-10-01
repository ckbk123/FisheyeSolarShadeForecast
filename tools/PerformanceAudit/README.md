# Performance audit harness

This console tool measures the existing Release-mode services/libraries; it does not modify the installed executable. Run one scenario at a time, without another benchmark or build competing for CPU/disk. Each output directory must be new to preserve cold-cache meaning. Keep results outside the installed `Data` and `Debug Data` folders.

```powershell
dotnet build tools/PerformanceAudit -c Release
# The model must be hydrated from Git LFS, not an LFS pointer.
# If this checkout skipped LFS, copy the real efficientnet-b5.onnx from the
# authoritative checkout into the harness's SkyPhotoModels folder AFTER building.
dotnet tools/PerformanceAudit/bin/Release/net10.0-windows/PerformanceAudit.dll 'C:/path/to/Deliverable-rewritten' 'C:/audit/new-month' month
```

Modes: `month`, `year`, `legacy`, `calibration`, `cpu-mask`.

- `month`: imported bundled example, then unchanged and panel-edit updates, isolated component/cache/output/render probes. Uses W. Europe Standard Time.
- `year`: same flow over deterministic synthetic hourly input in UTC; not measured weather. Larger output and memory demand.
- `legacy`: reads only historical manifests/directory topology from the supplied installation and creates read-only miniature fixture trees in the output folder. Measures repeated `PrepareInputs` calls; does not clean the user's historical directories.
- `calibration`: three full frontend calibration operations on the bundled 12-image set.
- `cpu-mask`: B5 at 1024 and 512, two ONNX CPU threads, three runs each. The quality settings are different and the CPU is not an emulated old machine.

Output: JSON lines on stdout and `measurements.json`. Stage medians are not an exclusive decomposition of end-to-end time. Render probes include offscreen layout/rasterization. Allocation is process-wide cumulative managed allocation, and cache totals include additional probe entries. The live app is not driven by this harness; dispatcher gaps are measured around service calls on an STA dispatcher.

The 30 September audit also tested an isolated, reverted prototype changing the legacy classification predicate in `DebugDataStore.PrepareInputs` from `Where(IsLegacyRun)` to `Where(path => deferredCleanup.Contains(path) || IsLegacyRun(path))`. This is not a released fix: initial discovery remains slow, and production migration/inventory semantics require the tests specified in the plan. Raw prototype measurements are retained with the baseline evidence.
