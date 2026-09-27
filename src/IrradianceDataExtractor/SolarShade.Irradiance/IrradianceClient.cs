using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SolarShade.Irradiance;

/// <summary>Thread-safe point time-series downloader. Reuse a client for connection pooling.</summary>
public sealed partial class IrradianceClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 2
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http;
    private readonly IrradianceOptions options;
    private readonly SemaphoreSlim gate;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public IrradianceClient(IrradianceOptions? options = null, HttpClient? httpClient = null)
    {
        this.options = options ?? new();
        if (this.options.MaxConcurrency is < 1 or > 4 || this.options.RequestTimeout <= TimeSpan.Zero || this.options.CdsJobTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        http = httpClient ?? SharedHttp;
        gate = new(this.options.MaxConcurrency);
    }

    /// <summary>Returns 1 only after downloading, validating and publishing the XLSX; otherwise 0.
    /// Existing files are preserved on failure and replaced only on success. End date is exclusive.</summary>
    public async Task<int> PullAsync(DateOnly startDate, DateOnly endDate, double longitude,
        double latitude, IrradianceService service, string outputPath, CancellationToken cancellationToken = default)
        => (await PullDetailedAsync(new(startDate, endDate, longitude, latitude, service), outputPath,
            cancellationToken).ConfigureAwait(false)).Status;

    public async Task<IrradianceResult> PullDetailedAsync(IrradianceRequest request, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var result = await FetchAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return result;
        try
        {
            if (!string.Equals(Path.GetExtension(outputPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must have an .xlsx extension.");
            XlsxExporter.Write(outputPath, result.Samples, options.TimeZone, request,
                result.TimestampConvention, cancellationToken);
            return result with { OutputPath = Path.GetFullPath(outputPath), Message = $"OK: exported {result.Samples.Count} records." };
        }
        catch (Exception ex) when (Recoverable(ex))
        { return Failure("Export failed: " + SafeMessage(ex), request.Service); }
    }

    /// <summary>Downloads validated data without forcing Excel export; suitable for application composition.</summary>
    public async Task<IrradianceResult> FetchAsync(IrradianceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enum.IsDefined(request.Service) || !double.IsFinite(request.Longitude) ||
                !double.IsFinite(request.Latitude) || Math.Abs(request.Longitude) > 180 || Math.Abs(request.Latitude) > 90)
                throw new ArgumentException("Invalid service or coordinates.");
            if (request.StartDate >= request.EndDate || request.StartDate.Year < 1940 || request.EndDate.Year > 2100)
                throw new ArgumentException("Use an increasing date range within 1940–2100; end is exclusive.");
            var start = Boundary(request.StartDate);
            var end = Boundary(request.EndDate);
            if ((end - start).TotalHours >= 1_048_576)
                throw new ArgumentException("Range exceeds one Excel worksheet.");
            RequireCredentials(request.Service);
            var chunks = new List<(DateOnly Start, DateOnly End)>();
            var day = DateOnly.FromDateTime(start.UtcDateTime);
            var lastDay = DateOnly.FromDateTime(end.AddTicks(-1).UtcDateTime);
            while (day <= lastDay)
            {
                var last = request.Service == IrradianceService.CopernicusCds ? lastDay : request.Service == IrradianceService.Nsrdb
                    ? new DateOnly(day.Year, 12, 31) : day.AddDays(30);
                if (last > lastDay) last = lastDay;
                chunks.Add((day, last));
                day = last.AddDays(1);
            }
            // Parallel.ForEachAsync does not create one waiting task per chunk for long ranges.
            var batches = new System.Collections.Concurrent.ConcurrentBag<List<IrradianceSample>>();
            await Parallel.ForEachAsync(chunks, new ParallelOptions
            { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = cancellationToken }, async (chunk, ct) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { batches.Add(await DownloadChunk(request, chunk.Start, chunk.End, ct).ConfigureAwait(false)); }
                finally { gate.Release(); }
            }).ConfigureAwait(false);
            var rows = batches.SelectMany(x => x).Where(x => x.TimestampUtc >= start && x.TimestampUtc < end)
                .OrderBy(x => x.TimestampUtc).ToArray();
            Validate(rows, start, end);
            return new(1, $"OK: downloaded {rows.Length} records.", Array.AsReadOnly(rows),
                options.TimeZone.Id, Convention(request.Service))
            {
                // These adapters explicitly request the providers' hourly endpoints and validate that response cadence above.
                NativeCadence = TimeSpan.FromHours(1),
                Label = request.Service switch
                {
                    IrradianceService.NasaPower => TimestampLabel.Start,
                    IrradianceService.OpenMeteo or IrradianceService.Cams or IrradianceService.CopernicusCds => TimestampLabel.End,
                    _ => null
                }
            };
        }
        catch (Exception ex) when (Recoverable(ex)) { return Failure(SafeMessage(ex), request.Service); }
    }

    private DateTimeOffset Boundary(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (options.TimeZone.IsInvalidTime(local) || options.TimeZone.IsAmbiguousTime(local))
            throw new ArgumentException("Date boundary is ambiguous or nonexistent in the selected time zone.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, options.TimeZone), TimeSpan.Zero);
    }

    private static void Validate(IrradianceSample[] rows, DateTimeOffset start, DateTimeOffset end)
    {
        if (rows.Length == 0) throw new InvalidDataException("No data for the requested local date range.");
        if (rows[0].TimestampUtc - start >= TimeSpan.FromHours(1) || end - rows[^1].TimestampUtc > TimeSpan.FromHours(1))
            throw new InvalidDataException("Provider returned incomplete boundary coverage.");
        for (int i = 0; i < rows.Length; i++)
        {
            if (!double.IsFinite(rows[i].DirectHorizontal) || !double.IsFinite(rows[i].DiffuseHorizontal) ||
                rows[i].DirectHorizontal < 0 || rows[i].DiffuseHorizontal < 0)
                throw new InvalidDataException($"Missing or invalid irradiance at {rows[i].TimestampUtc:O}.");
            if (i > 0 && rows[i].TimestampUtc - rows[i - 1].TimestampUtc != TimeSpan.FromHours(1))
                throw new InvalidDataException($"Duplicate timestamp or missing hour at {rows[i].TimestampUtc:O}.");
        }
    }

    private async Task<string> Get(string url, CancellationToken ct, string? apiHeader = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.UserAgent.ParseAdd("SolarShade-Irradiance/1.0");
                if (apiHeader != null) message.Headers.Add("api-key", apiHeader);
                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(2);
                    if (delay > TimeSpan.FromSeconds(30)) throw new HttpRequestException("Provider requests a longer retry delay; try later.");
                    await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }
                // Bound payload memory even if a provider or proxy returns an unexpected body.
                using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] block = new byte[32768];
                int count;
                while ((count = await body.ReadAsync(block, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > 64 * 1024 * 1024) throw new InvalidDataException("Provider response exceeds 64 MiB.");
                    buffer.Write(block, 0, count);
                }
                string text = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Provider HTTP {(int)response.StatusCode}: {Redact(text[..Math.Min(text.Length, 600)])}");
                return text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt == 0) { }
            catch (HttpRequestException ex) when (ex.StatusCode == null && attempt == 0 && !ex.Message.StartsWith("Provider")) { }
        }
    }

    private string Redact(string value)
    {
        foreach (var secret in new[] { options.NsrdbApiKey, options.NsrdbEmail, options.CamsEmail, options.OikolabApiKey, options.CdsPersonalAccessToken })
            if (!string.IsNullOrEmpty(secret))
                value = value.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase)
                    .Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.OrdinalIgnoreCase)
                    .Replace(Uri.EscapeDataString(Uri.EscapeDataString(secret)), "[redacted]", StringComparison.OrdinalIgnoreCase);
        return value;
    }
    private string SafeMessage(Exception ex) => ex is OperationCanceledException ? "Cancelled or timed out; no export." : Redact(ex.Message);
    private static bool Recoverable(Exception ex) => ex is not OutOfMemoryException and not StackOverflowException;
    private IrradianceResult Failure(string message, IrradianceService service) => new(0, message,
        Array.Empty<IrradianceSample>(), options.TimeZone.Id, Convention(service));
    private void RequireCredentials(IrradianceService service)
    {
        if (service == IrradianceService.Nsrdb && (string.IsNullOrWhiteSpace(options.NsrdbApiKey) || string.IsNullOrWhiteSpace(options.NsrdbEmail)))
            throw new ArgumentException("NSRDB requires a free API key and registered email (NsrdbApiKey / NsrdbEmail).");
        if (service == IrradianceService.Cams && string.IsNullOrWhiteSpace(options.CamsEmail))
            throw new ArgumentException("CAMS requires a free registered SoDa email (CamsEmail).");
        if (service == IrradianceService.Oikolab && string.IsNullOrWhiteSpace(options.OikolabApiKey))
            throw new ArgumentException("Oikolab requires a free API key (OikolabApiKey).");
        if (service == IrradianceService.CopernicusCds && string.IsNullOrWhiteSpace(options.CdsPersonalAccessToken))
            throw new ArgumentException("Copernicus CDS requires a free personal access token and prior dataset licence acceptance (CdsPersonalAccessToken).");
    }
    private static string Convention(IrradianceService service) => service switch
    {
        IrradianceService.NasaPower => "Native NASA POWER hourly UTC label; Wh/m² over one hour divided by 1 h gives W/m². No phase shift.",
        IrradianceService.OpenMeteo => "Native UTC label: preceding-hour mean W/m²; timestamp is interval end.",
        IrradianceService.Cams => "UTC integration interval end; hourly Wh/m² divided by 1 h gives W/m².",
        IrradianceService.Nsrdb => "Native NSRDB UTC label, hourly W/m²; direct horizontal = GHI - DHI.",
        IrradianceService.Oikolab => "Native Oikolab Unix UTC timestamp; horizontal W/m², provider interval convention retained.",
        IrradianceService.CopernicusCds => "ERA5 native UTC interval end; hourly SSRD and FDIR in J/m² divided by 3600; diffuse = (SSRD - FDIR)/3600.",
        _ => "Unknown service"
    };
    private static string Url(string endpoint, params (string Key, string Value)[] parameters) => endpoint + "?" +
        string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static DateTimeOffset ParseUtc(string value, string format) => DateTimeOffset.ParseExact(value, format,
        Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static double Number(JsonElement item) => item.ValueKind == JsonValueKind.Number ? item.GetDouble() : double.NaN;
}
