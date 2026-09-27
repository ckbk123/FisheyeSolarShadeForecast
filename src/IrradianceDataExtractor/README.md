# Irradiance data extractor

Independent C# library for historical **direct horizontal (BHI)** and **diffuse horizontal (DHI)** irradiance. The module does not depend on camera calibration or image processing. Targets .NET 8, is consumable by the existing .NET 10 application, and requires no Excel installation or production NuGet packages.

To convert these horizontal values for panel tilt/azimuth, use the separate [unshaded transposition module](../IrradianceTransposition/README.md), with Hay–Davies or Perez–Driesse and the existing solar-position module. Raw downloads retain their original horizontal meaning.

## Simple function

```csharp
using SolarShade.Irradiance;

var client = new IrradianceClient(); // reuse for connection pooling
int status = await client.PullAsync(
    startDate: new DateOnly(2025, 5, 1),
    endDate: new DateOnly(2025, 6, 1), // exclusive: downloads May
    longitude: 106.7,
    latitude: 10.8,
    service: IrradianceService.NasaPower,
    outputPath: Path.Combine(outputDirectory, "irradiance.xlsx"),
    cancellationToken: cancellationToken);
// 1 = complete validated data AND successful XLSX export; 0 = failure/cancellation.
```

Both dates are calendar dates in the machine's time zone (`TimeZoneInfo.Local`), with **start inclusive and end exclusive**. A one-day request uses the following day as end. This avoids interpreting ambiguous strings such as `01/05/2025`. To include June 1, use June 2 as end. No dates are silently changed to an available archive year.

Use `PullDetailedAsync(request, path, token)` for `Status`, `Message`, `OutputPath`, `TimeZoneId`, `TimestampConvention` and the typed samples. Use `FetchAsync(request, token)` to obtain validated data for the future shading application without writing Excel. There is no mutable `LastError` shared between concurrent calls.

## Output and time semantics

Exactly one worksheet and three columns:

1. Timestamp: ISO 8601 **local civil time with UTC offset**, e.g. `2025-05-01T07:00:00+07:00`.
2. Direct horizontal irradiance, numeric W/m².
3. Diffuse horizontal irradiance, numeric W/m².

Timestamps are intentionally text, since Excel serial dates cannot retain UTC offsets. The offset distinguishes the two occurrences of 01:00 on a daylight-saving fall-back day. Language/culture formatting is not a time zone; latitude/longitude does not determine the output time zone. Internally, timestamps are `DateTimeOffset` UTC instants. Optional `IrradianceOptions.TimeZone` allows an explicit app-selected zone; the default is always the machine zone. An ambiguous/nonexistent midnight boundary fails rather than guessing which instant the caller means.

Each provider's original hour label and minute offset is retained and converted to the output zone. **Time zones are normalized; sampling conventions are not resampled.** Open-Meteo/CAMS/CDS use interval-end labels. Do not assume two different providers' equal-looking hour labels refer to exactly the same averaging window. Convention and source information are embedded in workbook document properties and returned by the detailed API. POWER hourly Wh/m² over one hour has the same numeric value as its one-hour mean W/m²; CDS J/m² is divided by 3,600.

No interpolation, missing-value zero fill, negative-value clamping or silent provider fallback occurs. Success requires finite nonnegative components, unique hourly timestamps, and full boundary coverage on the provider's native hourly grid. A long request with a failed chunk fails entirely. A temporary workbook is published by a same-directory rename only after writing completes. An existing target is replaced on success and preserved on failure. Separate concurrent exports should use separate paths.

## Provider selection and researcher credentials

`ProviderCatalog.All` gives the app display names, authentication methods, registration URLs and usage notices. Choices are `NasaPower`, `OpenMeteo`, `Nsrdb`, `Cams`, `Oikolab`, and `CopernicusCds`. See [provider research](../../docs/IRRADIANCE_PROVIDERS.md) for coverage, free-use restrictions and validation limits.

```csharp
var client = new IrradianceClient(new IrradianceOptions
{
    // Populate from the researcher's app session / secure credential store.
    NsrdbApiKey = credentials.NsrdbKey,
    NsrdbEmail = credentials.NsrdbEmail,
    CamsEmail = credentials.RegisteredSodaEmail,
    OikolabApiKey = credentials.OikolabKey,
    CdsPersonalAccessToken = credentials.CdsToken
});
var result = await client.PullDetailedAsync(
    new IrradianceRequest(start, end, longitude, latitude, selectedService), path);
```

These are input ports for the future app, not implemented browser/OAuth login flows. The library does not persist credentials or read environment variables; only the validator reads them. Configure a new client when a user/account changes. Never serialize `IrradianceOptions` to logs: it contains credentials. Provider error messages redact configured secrets. Researchers must register/activate their accounts themselves; CDS also requires accepting the dataset licence on its website.

Default Open-Meteo dataset: `era5`, providing global coverage including Vietnam. Options expose dataset selection. NSRDB discovers the first available hourly chronological dataset for each requested year/location; it never substitutes a typical meteorological year. Its coverage differs by satellite. CDS queues a job and streams full point history from its CSV collection, retaining only requested dates; it can be slower and uses a temporary file, deleted after parsing. CDS has a configurable 20-minute total job deadline.

## Validator

```powershell
dotnet run --project src/IrradianceDataExtractor/SolarShade.Irradiance.Validation -c Release
dotnet run --project src/IrradianceDataExtractor/SolarShade.Irradiance.Validation -c Release -- --start 2025-05-01 --end 2026-06-01 --lat 10.8 --lon 106.7 --service NasaPower
dotnet test src/IrradianceDataExtractor/tests/SolarShade.Irradiance.Tests -c Release
```

Default: Ho Chi Minh City (10.8 N, 106.7 E), May 2025. The prompt's longer May 2025–June 2026 example is supported by explicit dates, subject to provider availability. Default output is inside `SolarShade.Irradiance.Validation/results/<run-time>/`; `--output <directory>` overrides it. `--service All` is default. Optional environment variables: `NSRDB_API_KEY`, `NSRDB_EMAIL`, `CAMS_EMAIL`, `OIKOLAB_API_KEY`, `CDS_PERSONAL_ACCESS_TOKEN`.

For every successful export, `WorkbookValidator` independently reopens the ZIP/XML package and compares **every** timestamp, offset and numeric value to the downloaded series, also enforcing three columns. Each run writes `validation-summary.json`. Missing credentials yield status 0 and no workbook. The validator's **process exit code** is conventional: 0 if every case passes, 1 if any fails/unconfigured. This differs intentionally from the library's 0/1 success value.

Live evidence is in `SolarShade.Irradiance.Validation/results/live-20260905`: NASA POWER and Open-Meteo May 2025, each 744 rows. Credentialed adapters have synthetic contract tests; live service verification is outstanding as agreed. Download validation verifies transport and data integrity, not scientific accuracy against ground measurements.

## Performance and compatibility

`HardwareCapabilities.Detect()` reports logical CPUs, runtime memory budget and SIMD availability. The default download concurrency is two on machines with at least four logical CPUs and 1 GiB available memory; otherwise one. It can be reduced explicitly. This is an I/O workload: GPU/AVX requirements would add no useful acceleration. HTTP connections are pooled, payloads use gzip/deflate/Brotli when available, JSON requests are bounded to 31 UTC days (annual-only APIs use one year), and Excel XML is streamed with fast ZIP compression. CAMS requests are serialized across clients within this process. The caller must coordinate account quotas across processes/users.

Ordinary requests have a configurable 90-second timeout, one bounded retry for transient network/server failures, and respect short `Retry-After` delays. CDS control requests have no automatic job-submission retry to avoid duplicate jobs. Successful HTTP responses containing malformed/incomplete data still fail. Payload limits are 64 MiB for ordinary APIs and 256 MiB for streamed CDS assets. Entire selected data must fit one Excel worksheet; the library holds selected records in memory, so very long ranges need more memory.

Builds as AnyCPU with scalar fallbacks; .NET 8 runtime support determines supported OS/CPU combinations. Actual execution on an old physical machine has not been tested. The existing calibration module remains .NET 10/Windows and is unchanged.
