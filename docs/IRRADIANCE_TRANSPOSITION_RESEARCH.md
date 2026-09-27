# From horizontal irradiance to a shaded tilted panel

Research assessment, 6 September 2026. Scope: front-side broadband irradiance at a specified receiver point, using historical hourly weather, solar position, panel orientation and a calibrated sky-obstruction photograph. This records the broader design recommendation; implementation status is below. No field validation is claimed.

**Implementation scope update:** the current stage is explicitly **unshaded** panel orientation correction. Hay–Davies, Perez–Driesse and an isotropic baseline are now implemented in [IrradianceTransposition](../src/IrradianceTransposition/README.md), with [numerical validation and sample exports](../src/IrradianceTransposition/VALIDATION.md). Image masks and directional shading discussed below are future work; they are not required inputs to this stage. The research remains broader than this implementation and does not claim field validation.

## Recommended direction

Keep the downloaded horizontal data as the weather record. Add a separate plane-of-array (POA) calculation consuming that record and the solar/shading modules. Compute direct, sky diffuse and reflected irradiance separately. A single correction applied to both horizontal components cannot represent their different angular distributions. This decomposition follows [Sandia PVPMC's POA framework](https://pvpmc.sandia.gov/modeling-guide/1-weather-design-inputs/plane-of-array-poa-irradiance/).

The user's coordinate transformation is a sound basis for direct irradiance. The proposed visible-hemisphere construction is also geometrically useful for diffuse irradiance, provided integration includes solid angle, panel-incidence cosine and sky radiance. A panorama is an optional visualization; it is not required for the computation.

Recommended sequence, subject to implementation validation:

1. Establish exact geometry and an isotropic diffuse baseline with analytical tests.
2. Implement unshaded Hay–Davies and Perez–Driesse as independent comparison options.
3. For arbitrary image masks, implement a nonnegative directional broadband sky model normalized to DHI, with separate direct-Sun treatment. Validate against Radiance's solar-radiance mode before making it the preferred detailed option.
4. Report model spread and missing-view uncertainty. Do not claim that the most complex model is locally most accurate without measurements.

## Definitions and geometry

Use east/north/up coordinates. Panel tilt beta is measured from horizontal: 0 degrees faces up, 90 degrees is vertical. Panel azimuth gamma is clockwise from true north and describes the outward front normal. Camera orientation is a separate transformation. For solar zenith Z and azimuth A:

\[
\mathbf{s}=(\sin Z\sin A,\;\sin Z\cos A,\;\cos Z),\qquad
\mathbf{n}=(\sin\beta\sin\gamma,\;\sin\beta\cos\gamma,\;\cos\beta).
\]

Thus cos(AOI) = n dot s = cos(beta)cos(Z) + sin(beta)sin(Z)cos(A-gamma). Trigonometric inputs must use radians internally. Direct-horizontal irradiance BHI, direct-normal irradiance DNI and diffuse-horizontal irradiance DHI are different quantities, all expressed here as W/m² averages. GHI = BHI + DHI when the components are consistent.

The front-side direct term at an instant is

\[
B_p(t)=DNI(t)\,\max(0,\mathbf n\cdot\mathbf s(t))\,V_s(t),
\]

with zero contribution when the Sun is below the chosen horizon. V_s is Sun visibility/transmission, between zero and one. For a resolved solar disk it represents a disk integration; it is not an electrical module shaded-area fraction. The unshaded DNI-times-cosine relation is documented by [Sandia: POA beam](https://pvpmc.sandia.gov/modeling-guide/1-weather-design-inputs/plane-of-array-poa-irradiance/calculating-poa-irradiance/poa-beam/).

For instantaneous BHI and cos(Z)>0, the corresponding multiplier is max(0,n dot s)/cos(Z). It can exceed one: orientation can increase irradiance relative to horizontal. Do not constrain a transposition multiplier to the 0–1 range used for visibility or shading loss. Do not divide by cos(Z) at night or arbitrarily near zero.

## Hourly inputs require an interval model

A solar position at an exported timestamp is an instantaneous position, not the position of the entire hour. Preserve the timestamp label but determine the physical averaging interval separately. The project already distinguishes following-hour NASA and preceding-hour Open-Meteo data; retain those semantics. Timestamp equality alone does not imply equal averaging windows. [pvlib's interval-average example](https://pvlib-python.readthedocs.io/en/v0.14.0/gallery/irradiance-transposition/plot_interval_transposition_error.html) demonstrates the error from transposing with the unadjusted label and the improvement from midpoint positions.

For our application, a stronger engineering approximation is to integrate geometry and visibility within each interval. The following is a derivation under an explicit assumption, not a claim that literature supplies missing cloud data. Let q(t) be a chosen nonnegative relative DNI shape, zero outside daylight, and let mu_h=max(0,cos Z). Recover the amplitude from the supplied hourly BHI:

\[
\bar B_p = \bar B_h
\frac{\int_I q(t)\max(0,\mathbf n\cdot\mathbf s(t))V_s(t)\,dt}
{\int_I q(t)\mu_h(t)\,dt}.
\]

The simplest choice is constant DNI during the daylight part of the interval. A clear-sky shape is another assumption, not measured sub-hour weather. This construction reproduces the input horizontal mean for an unobstructed horizontal receiver. A positive BHI with a zero or extremely small denominator must yield a diagnostic, not an enormous unchecked DNI. Any low-sun threshold or fallback must be explicit and tested.

If provider DNI is available, retain it with its native averaging convention and provenance. It helps avoid unstable BHI inversion, but hourly DNI still does not identify the covariance between clouds, incidence and visibility. Independently averaged DNI and BHI need not satisfy BHI=mean(DNI)*cos(Z at label). Do not silently alter one to force closure. Prefer actual sub-hour data when available; finer geometry alone cannot recover cloud timing.

A finite solar disk and circumsolar diffuse glow are separate. Do not enlarge the direct disk to absorb circumsolar radiation that is already included in DHI.

## Diffuse radiation: the physically useful formulation

Radiance L has units W/(m² sr); irradiance integrates radiance with projected solid angle. For upper-sky directions omega and obstruction transmission V:

\[
D_p(t)=\int_{\omega_z>0}L(\omega,t)V(\omega)
\max(0,\mathbf n\cdot\omega)\,d\Omega.
\]

This applies the irradiance definition in [Physically Based Rendering, Radiometry](https://pbr-book.org/4ed/Radiometry,_Spectra,_and_Color/Radiometry) to a masked sky. The sky model must satisfy the unmasked horizontal constraint

\[
DHI(t)=\int_{\omega_z>0}L(\omega,t)\omega_z\,d\Omega.
\]

Given a nonnegative relative model f, set L = DHI*f / integral(f*omega_z dOmega), using the **unobstructed** upper hemisphere for that denominator. Normalizing against only visible pixels would incorrectly restore radiation lost to obstructions. If DHI is zero, return zero sky diffuse without evaluating undefined brightness ratios.

For a uniform sky L=DHI/pi. With no obstructions, the integral yields D_p=DHI*(1+cos(beta))/2. A vertical panel therefore receives half the isotropic horizontal sky diffuse, not all of it. This baseline is summarized in the [IEA PVPS/Sandia performance-modeling report, section 2.4](https://pvpmc.sandia.gov/app/uploads/sites/243/2022/10/Report-IEA-PVPS-T13-06-2017_PV_Performance_Modeling_Methods_and_Practices_SAND2017-2570-R.pdf).

For a panorama indexed by azimuth A and zenith Z, dOmega=sin(Z)dZ dA; equivalently, with elevation e, dOmega=cos(e)de dA. Equal panorama pixels are not equal solid angles. The complete pixel weight is radiance * transmission * max(0,n dot omega) * pixel solid angle. Exact ring-cell area is deltaA*(cos(Z_low)-cos(Z_high)). These weights follow spherical geometry and the radiometric integral above.

Two useful factors must remain distinct:

- Transposition relative to horizontal: D_p,unshaded / DHI.
- Visibility relative to the same tilted unobstructed panel: D_p,shaded / D_p,unshaded.

The latter is between zero and one for nonnegative radiance; the former need not be. A static mask has a static factor only under a static sky distribution such as isotropic radiance. As the Sun and sky distribution change, anisotropic diffuse shading changes too.

## What the model families actually provide

| Model | Representation | Suitable role and limitation |
|---|---|---|
| Isotropic | Uniform sky | Exact geometric baseline under that assumption; cannot describe circumsolar or horizon structure. |
| Hay–Davies (1980) | Isotropic background plus circumsolar term, with anisotropy index DNI/extraterrestrial DNI | Compact benchmark and possible fast shaded approximation. The circumsolar point-source approximation is weak near narrow obstructions. |
| Perez (1987/1990) | Empirical isotropic, circumsolar and horizon contributions to tilted diffuse | Established aggregate benchmark; discrete coefficient bins can create discontinuities. Its component terms are not automatically a physical radiance map. |
| Perez–Driesse (2024) | Continuous reformulation of Perez 1990 | Good continuous aggregate comparison candidate; continuity does not itself prove greater local accuracy or solve arbitrary-mask shading. |
| Perez all-weather directional sky / Radiance gendaylit | Angular sky distribution constructed from irradiance and Sun position | Better structural fit for integrating arbitrary masks. Still estimates sky brightness; does not recover actual cloud positions from two irradiance scalars. |

The first-party [Hay–Davies implementation documentation](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.irradiance.haydavies.html) gives DHI*[A*Rb+(1-A)*(1+cos(beta))/2], A=DNI/DNI_extra, and identifies its absent horizon term. Its original reference is Hay and Davies, *Calculations of the solar radiation incident on an inclined surface*, 1980 Canadian Solar Radiation Data Workshop.

[pvlib's Perez documentation](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.irradiance.perez.html) specifies DHI, DNI, extraterrestrial DNI, solar angles and relative airmass, and exposes the three diffuse contributions. It cites Perez et al. (1990), *Modeling daylight availability and irradiance components from direct and global irradiance*, Solar Energy 44(5), 271–289. Its expected zenith is apparent/refraction-corrected. Our solar workbook is geometric, so a future implementation must state and test the chosen refraction convention instead of claiming exact parity with mismatched inputs.

Driesse, Jensen and Perez, *A continuous form of the Perez diffuse sky model for forward and reverse transposition*, Solar Energy 267 (2024), 112093, replace coefficient bins with splines. [Open paper](https://backend.orbit.dtu.dk/ws/files/346015606/1-s2.0-S0038092X23007272-main.pdf); [implementation contract](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.irradiance.perez_driesse.html). This is different from Perez, Seals and Michalsky's 1993 angular sky-luminance model, [publisher abstract](https://www.sciencedirect.com/science/article/pii/0038092X9390017I).

For a directional solar implementation, use broadband radiance rather than visible luminance. [Radiance gendaylit's official manual](https://radsite.lbl.gov/radiance/man_html/gendaylit.1.html) accepts DNI/DHI or BHI/DHI, supports full-spectrum solar output with `-O 1`, and can suppress the separate Sun source with `-s`. Its default visible output is not interchangeable with broadband W/m². It is a useful independent development reference; no Radiance runtime dependency is proposed yet.

Shading the sky components separately has precedent in [EnergyPlus Engineering Reference, anisotropic sky and diffuse shadowing](https://energyplus.net/assets/nrel_custom/pdfs/pdfs_v24.2.0/EngineeringReference.pdf). It uses dome, circumsolar and horizon terms with different shadowing factors. Its horizon term can be negative under overcast conditions; therefore those terms cannot simply be treated as three nonnegative light sources with arbitrary independent masks. A faithful component implementation needs its published rules and tests. A nonnegative directional model avoids this particular interpretation problem, but may not reproduce aggregate Perez exactly.

There is no demonstrated universal winner for our HCMC inputs. [Lave, Hayes, Pohl and Hansen (2015), Evaluation of GHI-to-POA models across the United States](https://www.osti.gov/pages/servlets/purl/1235343) found that combinations including Hay–Davies often had the smallest mean bias when only GHI was measured. This does not establish superiority for satellite/reanalysis BHI and DHI in Vietnam. Model ranking depends on input quality, climate, orientation and the chosen error metric.

## Ground, objects and the photograph's limits

An unobstructed, uniformly illuminated Lambertian horizontal ground gives G_p=rho*GHI*(1-cos(beta))/2. Keep this term separate from sky diffuse; rho must be an explicit assumption or supplied value. [pvlib ground-diffuse documentation](https://pvlib-python.readthedocs.io/en/stable/reference/generated/pvlib.irradiance.get_ground_diffuse.html).

Our upward sky mask cannot measure ground albedo, shaded-ground illumination or wall reflections. Setting an obstacle's sky transmission to zero removes its sky contribution, but does not model radiation reflected by that obstacle. Near bright walls, vertical panels, or bifacial configurations, this omission may matter. Treat a simple ground term as an approximation, not a reconstruction of an urban scene.

The first study specifically relevant to this workflow is Alvarez Mira et al. (2021), *Accuracy Evaluation of Horizon Shading Estimation Based on Fisheye Sky Imaging*, IEEE PVSC, 2052–2059. Its [DTU abstract](https://orbit.dtu.dk/en/publications/accuracy-evaluation-of-horizon-shading-estimation-based-on-fishey/) reports sensitivity to horizon errors at short timescales and identifies reflected irradiance as important for vertical surfaces. Only the institutional abstract was inspected; its results are not a numerical accuracy guarantee for this library.

Project-specific limits, based on the current module documentation:

- Calibration is provisionally validated to 66.43 degrees from the optical axis. Unseen directions remain unknown; a conservative blocked policy is an assumption and should retain coverage bounds. Recompute coverage for the actual panel/radiance weighting.
- A single photograph describes one observation point. Nearby obstacles cast different masks over a full panel; area-averaged irradiance requires several viewpoints or scene geometry when that parallax matters.
- Keep cloud-covered sky classified as sky in a persistent obstruction mask. Historical provider data already includes clouds; today's clouds must not become permanent building-like obstructions.
- A 2D horizon curve loses overhangs and disconnected openings; a full direction mask preserves them.
- Photo north, camera pose, seasonal vegetation and the relation between photo position and panel position can dominate small solar-ephemeris differences.

These are geometric/modeling implications, not newly measured errors. The user's original Python irradiance library was not found in the workspace search. The evaluation of its equirectangular method is conditional on the description, not a line-by-line code audit. The accessible Python files are calibration/masking utilities and independent validators.

## Proposed implementation contract, without implementation

Preserve `IrradianceClient` and its original three-column horizontal export. Introduce an independent transformation consuming typed samples, interval metadata, site, panel normal and optional obstruction model. The solar workbook remains useful for debugging, but sub-hour calculation should call the solar module directly or consume its integration directions.

Return separate direct POA, sky-diffuse POA, ground-reflected POA and their total, alongside unshaded counterparts, model name, temporal assumption and coverage/quality status. Do not silently redefine the extractor's existing diffuse column to include ground reflection. Maintain separate names for loss (0=no loss) and transmission (1=fully transmitted).

For fixed panel, camera and mask, precompute direction vectors, pixel lookup, solid angle and panel-cosine weights. Isotropic geometry then reduces to a cached scalar; directional skies require weighted sums with time-varying radiance. Adaptive refinement near solar paths and obstruction edges is more useful than repeatedly resampling a whole panorama. Start with bounded CPU parallelism and scalar fallback; hardware acceleration is an implementation optimization, not part of the scientific model.

This output is incident irradiance. Cover-glass incidence-angle losses, spectrum, soiling and electrical mismatch belong downstream. [Sandia's effective-irradiance definition](https://pvpmc.sandia.gov/modeling-guide/2-dc-module-iv/effective-irradiance/) distinguishes these from POA irradiance. A shadowed fraction does not directly imply the same percentage of electrical power loss.

## Validation required before accepting the module

1. Analytical geometry: horizontal identity, Sun behind front face, Sun-normal incidence, vertical isotropic sky=0.5*DHI, unshaded sky/ground view factors summing to one, and all-open/all-blocked mask limits.
2. Coordinate checks: east/west cases, northern/southern Sun, camera rotation invariance, equal-solid-angle versus equirectangular integration convergence, and no image-area normalization.
3. Temporal checks: both provider windows, sunrise/sunset partial hours, zero/direct anomalies, and exact recovery of horizontal input under the declared DNI-shape assumption. Compare midpoint with converged interval quadrature.
4. Model parity: identical inputs and angular conventions against pinned pvlib versions for unshaded isotropic/Hay–Davies/Perez–Driesse. Separately compare directional sky integrals with Radiance solar output. Cross-model agreement is not field validation.
5. Obstruction tests: horizon bands, overhangs, narrow Sun blockers, a blocked circumsolar region versus an equal-area remote patch, and explicit unseen-region lower/upper bounds.
6. Field validation: coincident horizontal components and a calibrated tilted POA sensor, with orientation and shading surveyed. Report W/m² bias/RMSE and daily/monthly energy bias, stratified by tilt, low Sun and sky conditions; avoid percentage errors near zero. [Sandia's validation framework](https://pvpmc.sandia.gov/model-validation/model-validation-procedure/) recommends residual and baseline comparisons as well as aggregate error.

Potential external data: [Sandia's concurrent sky images, angular irradiance and weather datasets](https://pvpmc.sandia.gov/datasets/) cover Albuquerque and Eugene. They are candidates for an independent benchmark, not HCMC validation. No large dataset has been downloaded during this assessment.

## Evidence status and stopping point

The governing geometry, standard transposition families, directional radiance route and principal input limitations have primary technical support. The equations for interval-preserving BHI conversion and panorama weights above are explicitly derived design proposals. No local model-accuracy ranking or performance claim has been established.

Remaining implementation prerequisites: select the first supported sky model, pin its coefficients and low-sun conventions, decide geometric versus apparent solar angles, specify albedo and coverage behavior, and obtain the original Python script if a direct legacy comparison is wanted. These do not prevent completing the literature assessment. Research stopped after those decision-relevant questions were supported or explicitly bounded; production code was not changed.
