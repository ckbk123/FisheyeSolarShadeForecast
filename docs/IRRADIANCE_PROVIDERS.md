# Free historical horizontal irradiance APIs

Research date: 5 September 2026. Audience: researchers building the SolarShade location-and-photo shading estimator.

Six selectable API services are implemented. NASA POWER is an accessible global satellite-based baseline; Open-Meteo supplies convenient ERA5 reanalysis for non-commercial research. For satellite data with finer regional coverage, consider NSRDB or CAMS where the requested coordinates and dates are covered. Oikolab and direct Copernicus CDS provide alternative ERA5 access. These are credible source families, not a claim that one is uniformly most accurate or that every free endpoint worldwide has been enumerated.

## Eligibility and implementation

| API route | Horizontal components | Free access and scope | Evidence / implementation status |
|---|---|---|---|
| NASA POWER hourly | Native `ALLSKY_SFC_SW_DIRH` and `ALLSKY_SFC_SW_DIFF` | No key; global hourly from 2001; recent solar data has latency | Public live May 2025 succeeded. [Hourly API](https://power.larc.nasa.gov/docs/services/api/temporal/hourly/), [solar methodology](https://power.larc.nasa.gov/docs/methodology/energy-fluxes/) |
| Open-Meteo archive / ERA5 | `direct_radiation` / `diffuse_radiation`; not `direct_normal_irradiance` | No key for free **non-commercial** endpoint. CC-BY attribution. Global ERA5 history; 0.25° grid | Live May 2025 succeeded. [Historical API](https://open-meteo.com/en/docs/historical-weather-api), [terms](https://open-meteo.com/en/terms) |
| NSRDB | GHI minus DHI gives BHI; DHI retained | Free API key and email. Satellite-specific footprint/years; never substitute TMY | Discovery and annual CSV adapter; synthetic tests only. New host is `developer.nlr.gov`; old NREL host retired May 2026. [Dataset catalogue](https://developer.nlr.gov/docs/solar/nsrdb/), [discovery](https://developer.nlr.gov/docs/solar/nsrdb/nsrdb_data_query/) |
| CAMS Radiation / SoDa | Native all-sky BHI / DHI | Free registered email, active account; regional satellite coverage includes Himawari SE Asia. Serialized requests | WPS CSV adapter; synthetic tests only. [Automatic API](https://www.soda-pro.com/en/help/cams-services/cams-radiation-service/automatic-access), [CAMS data documentation](https://confluence.ecmwf.int/spaces/CKB/pages/266592908/CAMS+solar+radiation+time-series+data+documentation) |
| Oikolab / ERA5 | `surface_direct_solar_radiation` / `surface_diffuse_solar_radiation` | Free account/key and monthly quota; usage and redistribution terms matter for a public app | Nested JSON adapter; synthetic tests only. [Radiation definitions](https://docs.oikolab.com/parameters/), [API reference](https://docs.oikolab.com/references/) |
| Copernicus CDS / ERA5 time-series | FDIR / 3600 = BHI; (SSRD − FDIR) / 3600 = DHI | Free personal token and manual dataset-licence acceptance; global | Direct asynchronous REST/CSV adapter; synthetic tests only. Full point history is downloaded and filtered. [CSV dataset](https://cds.climate.copernicus.eu/datasets/reanalysis-era5-single-levels-timeseries?tab=overview), [account/API requirements](https://cds.climate.copernicus.eu/how-to-api), [process schema](https://cds.climate.copernicus.eu/api/retrieve/v1/processes/reanalysis-era5-single-levels-timeseries) |

NSRDB's current GOES catalogue includes 2025, but its documented Himawari endpoint lists 2016–2020. A Vietnamese 2025 request cannot assume the same coverage as a US 2025 request. The adapter queries availability at the actual location. [GOES hourly download](https://developer.nlr.gov/docs/solar/nsrdb/nsrdb-GOES-aggregated-v4-0-0-download/), [NSRDB catalogue](https://developer.nlr.gov/docs/solar/nsrdb/).

CAMS access documentation and release notes disagree on daily quota: the older help lists 100/day, while the 17 February 2026 release notes increase it to 500/day. Use the newer release notes and avoid concurrent calls. Registered researchers should check the current account limit before production use. [CAMS release notes](https://www.soda-pro.com/help/cams-services/release-notes-cams).

Oikolab currently advertises 1,500 free units/month, with a unit based on location, parameter and month, and priced usage above that allowance. Free signup is not unlimited API access. Its terms describe internal-business use, so downstream redistribution needs an entitlement check. The adapter does not enforce an account's billing quota. [Oikolab pricing](https://oikolab.com/), [terms](https://docs.oikolab.com/terms/).

## Correct quantities and timestamps

Direct normal irradiance is measured on a plane normal to the sun. This module exports direct **horizontal** irradiance. NASA supplies it natively; NSRDB derives it from GHI − DHI; CDS derives diffuse using its total and direct horizontal energy fields. No point-in-time cosine correction is applied to an hourly-average DNI.

NASA is explicitly requested in UTC because its default local solar time is not a civil time zone. Its response labels the radiation fields Wh/m², so dividing by a one-hour interval yields the same numeric hourly mean W/m². The implementation verifies the returned time standard and units. [NASA time standards](https://power.larc.nasa.gov/docs/services/api/temporal/hourly/).

Open-Meteo radiation is a preceding-hour mean. CAMS observation periods specify integration start/end; the adapter retains the end and requests one-hour periods. CDS radiation is hourly accumulated J/m², converted by 3,600 seconds. These are source labels preserved as instants, not an assertion of identical averaging windows across products. [Open-Meteo definitions](https://open-meteo.com/en/docs/historical-weather-api), [CAMS documentation](https://confluence.ecmwf.int/spaces/CKB/pages/266592908/CAMS+solar+radiation+time-series+data+documentation), [ERA5 time-series product guide, May 2026](https://confluence-stage.ecmwf.int/spaces/CKB/pages/505390919/ERA5+hourly+time-series+data+on+single+levels+from+1940+to+present+Product+User+Guide+PUG).

The target is the computer's **civil time zone**, including historical daylight-saving offsets. Latitude/longitude and UI language do not identify that zone. Dates are defined as start-inclusive/end-exclusive machine-local boundaries. UTC requests include adjacent days when needed, then samples are filtered by their actual UTC instants. Offsets are stored within the timestamp column to avoid ambiguity at repeated local hours.

## Data quality and the shading application

Satellite retrievals and reanalysis are estimates for grid cells, not measurements at the exact camera position. NASA's solar archive includes satellite radiation products; ERA5 combines observations with an atmospheric model. Several selectable routes share ERA5, so agreement between Open-Meteo, Oikolab and CDS is **not independent confirmation**. [NASA solar methodology](https://power.larc.nasa.gov/docs/methodology/energy-fluxes/), [CDS dataset description](https://cds.climate.copernicus.eu/datasets/reanalysis-era5-single-levels-timeseries?tab=overview).

Engineering judgment: retain the irradiance provider as a replaceable input to the shading estimator. Compare candidate datasets to local pyranometer/reference measurements before selecting a scientific default. Model resolution, clouds, aerosols, terrain and retrieval algorithms affect both components. No universal accuracy ranking or Vietnam-specific error estimate is supported by the present transport validation.

Diffuse horizontal irradiance alone does not specify the directional sky distribution: the later shading model still needs a stated isotropic or anisotropic sky assumption. This module downloads background irradiance and makes no claim to reconstruct diffuse radiance from DHI.

## Exclusions

| Candidate | Reason it is not an interchangeable free historical option |
|---|---|
| Visual Crossing | Advanced direct/diffuse solar fields are on Corporate/Enterprise plans, despite free general weather access. [Pricing](https://www.visualcrossing.com/weather-data-pricing/) |
| OpenWeather Solar | Separate paid solar subscription; a generic free weather allowance does not establish free historical component access. [Solar subscription](https://home.openweathermap.org/subscriptions/unauth_subscribe/energy/base) |
| Solcast | Evaluation/trial access does not establish ongoing free arbitrary-location historical downloads. [Provider API workspace](https://www.postman.com/solcast/solcast-s-public-workspace/documentation/zsxj5c2/solcast-api?entity=request-35099640-2e773961-b58e-49b7-b233-6ac4688704ce) |
| HelioClim-1 | Automatic access is bundled with paid HelioClim-3 subscription; limited guest location access is insufficient. [Automatic access terms](https://www.soda-pro.com/en/help/automatic-access/helioclim-1) |
| CAMS McClear | Clear-sky radiation is not the requested historical all-sky weather. [CAMS product distinction](https://confluence.ecmwf.int/spaces/CKB/pages/266592908/CAMS+solar+radiation+time-series+data+documentation) |
| Station-only collections / downloadable atlases | A finite measurement network or map download does not satisfy the same arbitrary-coordinate historical component API contract. These may still be useful validation references. |

## Verification and remaining uncertainty

The retained live validation files are NASA and Open-Meteo for May 2025. Each has 744 rows at 10.8° N, 106.7° E and local UTC+07:00 timestamps. These results validate successful transport, horizontal-field mapping and export integrity at those cases only. The longer date example was made configurable rather than silently treated as one month.

Four credentialed routes are intentionally not claimed live-verified: NSRDB, CAMS, Oikolab and CDS. Their controlled tests cover request/response mapping; real accounts may expose additional schema or coverage issues. Unsupported years are failures, not automatic substitutions. Credential setup was deferred to future app users as requested.

Research stopped after each implemented route had primary documentation for its data fields, API, time handling and free-access conditions, and the principal paid/clear-sky alternatives were distinguished. An exhaustive worldwide census and ground-truth performance study are outside the evidence collected here. Recheck provider terms and schemas when integrating the app, particularly credentialed services.

## Open-Meteo and ERA5 explained

Open-Meteo is an API service operated by OpenMeteo GmbH in Switzerland. ERA5 is the global historical reanalysis dataset produced by ECMWF for the Copernicus Climate Change Service. The Open-Meteo adapter requests that dataset through Open-Meteo. Reanalysis combines observations and atmospheric modelling to estimate past conditions; it is not a local irradiance sensor measurement. Coverage includes Vietnam. [Operator](https://open-meteo.com/en/terms), [ERA5 producer and coverage](https://www.ecmwf.int/en/forecasts/datasets/complete-era5-global-atmospheric-reanalysis).
