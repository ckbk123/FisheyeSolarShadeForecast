# NOAA/Meeus replacement - 2026-09-06

The current solar kernel uses the NOAA/Meeus equations from [NOAA main.js](https://gml.noaa.gov/grad/solcalc/main.js) and [calculation details](https://gml.noaa.gov/grad/solcalc/calcdetails.html), plus WGS84 observer parallax to preserve elevation behavior. Public signatures, timestamps, column order, angular conventions, refraction setting and shading calculations are unchanged. Astronomy Engine is no longer a production dependency.

New sample exports and compact comparison evidence are in `Validator/results/meeus/`; original outputs remain as the before-change reference.

- All **111 solution tests passed**, including 29 shading tests.
- 240 independent saved Astropy 8.0.1 cases: maximum direction difference **0.006562 degrees**, mean **0.004364 degrees**; below the existing 1/60-degree acceptance. Previous Astronomy Engine maximum was 0.000534 degrees on these cases, so this replacement is not an accuracy improvement.
- Both 744-row May 2025 solar workbooks: every timestamp string and offset unchanged. Maximum direction difference from the live NOAA Solar Geometry table **0.004339 degrees**, from the previous engine **0.008515 degrees**. NOAA's table and JavaScript calculator are distinct implementations; observer parallax is also additional here. We do not claim bit-for-bit table parity.
- Current validator file-to-file run: first call **711.8 ms**, five subsequent calls **88.6-121.6 ms**; solar preparation **2.24-4.71 ms**. Timing is machine/run-specific, not a controlled speedup measurement.
- Regression tests cover saved Astropy references, NOAA fixtures, poles, leap day, UTC date limits, offset equivalence and existing shading/export behavior.

NOAA states its web calculator is no longer actively maintained. The production library computes offline. This compact approximation does not establish sub-arcsecond accuracy or model atmospheric refraction, terrain, or sub-hour cloud variability. Historical shading-oracle/convergence figures below used the old engine and were not regenerated for this replacement; the current end-to-end validator and regression tests were rerun.

---

## Historical validation before the replacement

# Validation record

Run on 6 September 2026, Windows x64, Ryzen 7 5700X (16 logical processors), RTX 3070, approximately 64 GiB RAM. CPU/SIMD executes this module; the detected GPU is not used for shading. No irradiance APIs were called during validation: both existing May 2025 workbooks were consumed as supplied.

## Inputs and outputs

- `../SkyPhotoMasking/Validator/example_image-efficientnet-b5.png`: 3000×4000, strict binary, full EXIF-oriented coordinates.
- Original photo: `../SkyPhotoMasking/Validator/example_image.jpg`.
- Profile: `../../artifacts/calibration-validation/ready-to-run/calibration.yml`; half-angle limited to the independently observed 66.43°, not the polynomial turning point. Profile/photo pairing is provisional, not field-verified.
- Disk: centre (1521.8751, 2003.1250), radius 882.9167 pixels, from the masking validator's detailed result.
- Both supplied irradiance files: `../IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905`, 744 rows each.
- Default pose: vertically upward, top true north / bottom south. Example location: 10.8 N, 106.7 E; demonstration elevation 0 m, actual site elevation still unspecified.

The Open-Meteo and NASA outputs each include a shading workbook and a separate **744-row solar-position workbook, including nighttime positions**. `Tools/validate_workbooks_and_shading.py` independently reads the exported ZIP/XML, checks every timestamp and original UTC offset, verifies bounds/factors and zero-direct statuses, and compares all exported angles with Astropy. The shading writer leaves unknown factors blank and exports ordered loss bounds; a floating-point endpoint ordering issue found by this independent check was corrected.

## Correctness evidence

| Check | Result / evidence |
|---|---|
| Solution tests | 103 passed: irradiance 24, calibration 43, masking 15, shading 21 |
| Managed SIMD disabled | The initial 18 shading tests also passed with `DOTNET_EnableHWIntrinsic=0`; confirms managed scalar paths, not a physical old-CPU run |
| Global solar oracle | 240 cases, 5 sites including 8849 m altitude, 4 dates, 12 UTC hours; max direction error 0.0005342° against Astropy 8.0.1 |
| Exported solar workbooks | 744 angles per provider; maximum direction error 0.0004214°; all timestamps/offsets match input |
| Independent shading oracle | NumPy + Astropy, 48 spread-out daylight labels; 240 time samples × 2048 disk points; maximum error in a direct-loss bound 0.001097 (0.110 percentage point) |
| Native default convergence | 60×128 versus 480×4096: maximum bound error 0.000946 (0.095 percentage point) |
| Dense reference stability | 240×2048 versus 480×4096: maximum difference 0.00003745 |
| Uniform-region shortcut | Same quadrature with shortcut enabled/disabled: maximum difference 3.33e-16 |
| Diffuse convergence | 65,536 versus 2,097,152 rays: loss bounds differ by approximately 0.000433 |
| Pixel decode | Independent OpenCV comparison across PNG strategies, CRC corruption rejection; scalar and SIMD paths covered |
| Workbook visual inspection | Both sheets in each main workbook rendered and reviewed; metadata wraps and timestamps remain readable |

Sources: `Validator/results/solar-astropy-validation.json`, `independent-validation.json`, `convergence.json`, and the solution test output. The oracle comparisons measure direction-vector angle rather than raw azimuth near zenith, where azimuth becomes ill-conditioned. They establish numerical agreement on these cases, not universal physical accuracy of the lens or segmentation model.

The tests also cover all-white/all-black masks, an analytical half-visible disk, optical-axis/zenith behavior, cardinal directions, tilted camera parity with the existing projector, rearward-ray rejection, incomplete field of view, explicit conservative policy, first/gapped records, DST repeated civil hours, interval-start/end timing, elevation sensitivity, invalid masks/profiles, zero-direct skipping, and standalone solar workbook column order/night rows.

Open-Meteo output: 356 skipped-zero rows, 247 fully covered computed rows, 141 incomplete-coverage rows. NASA output: 384 skipped-zero rows, 248 fully covered computed rows, 112 incomplete-coverage rows. These incomplete rows are a consequence of the supplied calibration limit, not missing timestamps.

The diffuse result has cosine-weighted coverage 0.840103. Default loss bounds are approximately [0.163678, 0.323575]. A single factor is deliberately null until the caller selects a missing-coverage assumption or supplies a wider validated view.

## Performance: complete calls, not kernel-only timing

`ShadingFilePipeline.Compute` times hardware detection (cached after its first probe), XLSX/PNG/YAML reading, mask preparation/diffuse integral, all label positions and daylight interval directions, direct shading, and **both** workbook writes. Mask/calibration/solar arrays are not cached between the file-to-file trials. Normal operating-system filesystem caching and .NET tiered compilation are present. There is no explicit flush of OS caches, so “first process call” is not a cold-disk claim.

Final recorded 30-call default run (`Validator/results/benchmark.json`):

| Scope | Measured milliseconds |
|---|---:|
| First complete call | 834.8 |
| All 30 subsequent calls, median | 125.4 |
| All 30 subsequent calls, P95 (nearest rank) | 158.9 |
| All 30 subsequent calls, range | 65.9–204.8 |
| Last 10 calls, median | 72.9 |
| Last 10 calls, maximum | 90.6 |
| Synthetic 8760-hour year, initial five complete calls | 347–441 |
| Synthetic year, final five-call recheck | 497–580 |

Tiered compilation noticeably improves later calls. Files live under OneDrive, and both filesystem activity and scheduling produce outliers. The **strict 100 ms end-to-end target is not achieved for every call**; the steady-state target and sub-second annual scaling were demonstrated on this PC. It would be misleading to promise that a fresh user invocation always takes under 100 ms. The final application can pass in-memory typed data between already-running modules instead of decoding/re-encoding intermediate files, without changing the mathematical calculation.

### Fifty optimization trials

The first five recorded implementation trials are `trial-01-baseline.json` through `trial-05-probe-and-two-exports.json` in `Validator/results`:

1. Full spherical sampling with OpenCV mask decode.
2. Exact uniform-region tile shortcut.
3. CRC-checked native grayscale PNG decoding with SIMD up-filter reconstruction.
4. SIMD binary-tile classification and tighter conservative footprint bound.
5. Non-allocating D3D12 capability probe and the newly required second workbook export.

The first four measurements predate the user's extra solar workbook requirement; they are useful for diagnosing changes, not final two-export claims. Trial 5 includes the new output contract. Trials 6–50 cover the 3×3×5 grid of 30/60/120 temporal samples, 64/128/256 disk samples and 1/2/4/8/16 workers. Each configuration has three complete file-to-file measurements in `optimization-sweep.json`. The test sweep saves progress and resumes after completed configurations. The three repetitions per configuration are exploratory; use the larger final run for percentiles.

The original sweep stopped after trial 30 because OneDrive temporarily denied replacement of a repeatedly overwritten output file. The target workbook was not truncated. After confirming the process had exited, trials 31–50 resumed using distinct output paths; this change in output publication pattern is a limitation of comparing the two portions. The failed attempt is not counted as a separate successful trial. No machine settings or sync service were changed.

All nine sampling combinations were checked against the dense reference. The 60×128 defaults were retained for tighter accuracy rather than choosing the fastest/coarsest case merely to meet the timing number. The sweep includes limited-worker measurements, but they are not a simulation of an i3's clock rate, cache, memory or disk. Actual weaker-machine measurements remain unverified. Managed SIMD has a tested scalar fallback.

### Annual workload

`AnnualBenchmark.cs` generates 8760 distinct hourly labels and synthetic daylight irradiance from the site geometry, then reads that XLSX and writes both outputs. It is a scaling benchmark **not an annual irradiance forecast** and does not pretend to be downloaded weather. Its generation is outside the timed call, just as API retrieval is outside this library. `annual-benchmark.json` records the initial timed stages and row counts; `annual-final/annual-benchmark.json` contains the final recheck with current source.

## Debug geometry

The original photo carries a red projected solar footprint at **2026-05-31 12:00:00+07:00**. This is civil noon, not solar transit, and is separate from the May 2025 irradiance test period. Elevation 0 m is an explicit demonstration assumption; true height can be supplied via `--elevation`.

| Radius | Centre in full image | Width × height |
|---|---|---|
| 0.25° | (1561.05, 1838.94) px | 7.184 × 7.100 px |
| 2.5° | Same centre | 71.831 × 71.003 px |

Full images keep the original dimensions. Detail previews show a 4× enlarged crop and put labels outside the picture. `overlay-geometry.json` records azimuth 349.1294°, zenith 11.3302° and the full-precision footprint coordinates. No Sun was detected in the image; this is a calculated overlay.

## Scientific and integration limits

- The 0.25° radius is a user-selected fixed approximation; the true apparent solar radius varies slightly through the year. The 2.5° option is a circumsolar comparison, not a claim about the physical Sun.
- Refraction is disabled. Actual photographed apparent position near the horizon can differ; changing this requires a consistent atmospheric model and validation, not just a height correction.
- Hourly averaging assumes constant DNI within each interval, weighting by horizontal incidence. Actual sub-hour cloud changes cannot be recovered from hourly irradiance.
- [NASA POWER's FAQ](https://power.larc.nasa.gov/docs/faqs/other/) specifies start-of-hour labels; Open-Meteo's supplied metadata specifies interval end. The reader resolves those without altering timestamps. Other unspecified provider conventions require an explicit caller choice.
- The constant diffuse factor assumes isotropic radiance on a horizontal receiver. [Perez sky-diffuse modelling](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.irradiance.perez.html) includes circumsolar/horizon components and can produce time-varying diffuse shading. That is a future model extension, not silently included here.
- An inclined-panel module can consume the complete solar-position workbook. Exact inclined-plane hourly shading will require that plane's temporal incidence weights; the horizontal factor is not universally interchangeable.
- Existing calibration and segmentation errors can exceed the numerical quadrature errors. The supplied fit was validated only to 66.43°, and absolute photo pose/lens pairing were not field-measured for this demonstration.

