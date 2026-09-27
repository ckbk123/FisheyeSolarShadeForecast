# Transposition validation

Validated on 6 September 2026. The solution test run passed **148 tests**, including 27 new transposition tests. The standalone validator completed all four 744-row provider/model combinations. All outputs represent unshaded front-side direct plus sky diffuse.

## Numerical evidence

1. **1,308 independent pvlib 0.15.2 cases for each model** cover panel azimuth/tilt, zero components, beam behind the panel, overcast skies, zenith near 85°/90°, and all Perez–Driesse spline-knot neighbourhoods. Maximum absolute difference: **1.71e-12 W/m²**.
2. **151 refraction cases** compare the explicit NOAA adapter with the refraction part of pvlib's ephemeris calculation at matching reference atmosphere. Maximum zenith difference: **1.42e-14°**. This checks refraction, not the underlying solar-position ephemeris. The formula is approximate under real atmospheric conditions.
3. **2,976 exported hourly rows** were independently recomputed with pvlib from the C# audit's solar instants/angles and original BHI/DHI. Maximum difference: **7.96e-13 W/m²**. Python independently inferred interval DNI and evaluated extraterrestrial irradiance and both transposition models.
4. **Every exported cell** was read with openpyxl: three headers, row counts, numeric values, timestamp instants, offsets and model metadata matched. Separate C# tests check the repeated hour at a DST transition and fractional seconds. All four sheet layouts were rendered and inspected for legibility.
5. Analytic tests cover integrated beam geometry, sunrise daylight fractions, the horizontal identity case, cosine-weighted isotropic sky view, invalid input, impossible BHI inversion, cancellation/export preservation and serial/parallel equivalence.

Reference fixtures are stored in `Tests/Fixtures/pvlib-0.15.2.json`, with their regeneration script in `Validator/Tools`. Evidence for the executed integration run is in [validation.json](Validator/results/may2025/validation.json) and [pvlib_verification.json](Validator/results/may2025/pvlib_verification.json). Compressed audits retain per-row flags, effective DNI and every quadrature instant/solar angle.

## Tilt-toward-Sun sanity check

Sun: apparent zenith 60°, azimuth 180°. DNI: 800 W/m². DHI: 100 W/m². Extraterrestrial DNI: 1366.1 W/m².

| Receiver | Direct | Hay–Davies sky diffuse | Perez–Driesse sky diffuse |
|---|---:|---:|---:|
| Horizontal | 400.00 | 100.00 | 100.00 |
| 60° tilt toward Sun | 800.00 | 148.20 | 155.23 |
| 60° tilt away from Sun | 0.00 | 31.08 | 59.99 |

All values are W/m². Facing the Sun doubles direct irradiance in this case. Total direct + sky diffuse rises from 500.00 to 948.20 or 955.23 W/m². This is a beam-dominated example; tilting toward the Sun does not guarantee the largest **total** irradiance under diffuse-dominated skies, and a fixed tilt need not improve monthly totals.

## Sample files for manual inspection

Input period: **May 1–June 1, 2025, end exclusive**, HCM City **10.8° N, 106.7° E**, elevation 0 m. Receiver: **30° tilt, azimuth 0° (true north)**. Local export zone in this run: UTC+07. NASA uses following-hour geometry; Open-Meteo uses preceding-hour geometry. NOAA/Meeus geometric positions are explicitly converted to apparent positions using NOAA's standard-atmosphere approximation. These reuse the earlier validated raw downloads; no new download was needed.

| Workbook | Rows | Direct kWh/m² | Sky diffuse kWh/m² |
|---|---:|---:|---:|
| [NASA — Hay–Davies](Validator/results/may2025/NasaPower_HayDavies_tilt30_az0.xlsx) | 744 | 79.064 | 70.753 |
| [NASA — Perez–Driesse](Validator/results/may2025/NasaPower_PerezDriesse_tilt30_az0.xlsx) | 744 | 79.064 | 74.976 |
| [Open-Meteo — Hay–Davies](Validator/results/may2025/OpenMeteo_HayDavies_tilt30_az0.xlsx) | 744 | 105.191 | 53.424 |
| [Open-Meteo — Perez–Driesse](Validator/results/may2025/OpenMeteo_PerezDriesse_tilt30_az0.xlsx) | 744 | 105.191 | 57.611 |

These energy totals are hourly mean W/m² summed and divided by 1000. Direct is identical between models, as expected. The original providers' different beam/diffuse partition remains visible; transposition does not resolve that source uncertainty.

Each run flags 62 rows containing low-Sun daylight substeps and 31 rows containing positive diffuse assigned to below-horizon substeps. There were no rejected rows or capped DNI values. Maximum inferred DNI was 769.668 W/m² for NASA and 840.546 W/m² for Open-Meteo. Inspect the compressed row audits for the affected timestamps.

Doubling quadrature from 60 to 120 substeps changed any hourly component by at most **0.00385 W/m² for NASA** and **0.236 W/m² for Open-Meteo**. The largest change occurs around sunrise/sunset where the daylight boundary is sampled discretely. This is numerical resolution sensitivity, not an estimate of the error caused by unknown intra-hour cloud variability.

## Performance and limits

On the current 16-logical-processor, approximately 64 GiB-memory machine, each 744-row conversion plus XLSX export took **13–31 ms** in the recorded run. Automatic execution selected two workers. These are individual warm-process measurements after reference checks, excluding input reading, audit generation and Python verification; they are not a benchmark guarantee. No GPU/SIMD requirement was introduced. Execution on an older physical machine remains untested.

No field measurements were used. Validation establishes equation parity, interval bookkeeping and file correctness. Accuracy still depends on the provider weather data, constant-within-interval assumptions, solar-angle/refraction accuracy and how well the selected empirical diffuse model represents the local sky. Image shading, ground reflection and electrical output are later stages.
