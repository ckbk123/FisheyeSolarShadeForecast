using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace SolarShade.Irradiance;

public sealed partial class IrradianceClient
{
    private const string CdsRoot = "https://cds.climate.copernicus.eu/api/retrieve/v1";
    private async Task<List<IrradianceSample>> Cds(IrradianceRequest request, DateOnly start, DateOnly end, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.CdsJobTimeout);
        ct = deadline.Token;
        // This official time-series collection exposes point + variables, not date subsetting.
        // Request once, stream the CSV locally, and retain only the requested UTC dates.
        var inputs = new
        {
            variable = new[] { "surface_solar_radiation_downwards", "total_sky_direct_solar_radiation_at_surface" },
            data_format = "csv", area = new[] { request.Latitude, request.Longitude, request.Latitude, request.Longitude }
        };
        using var submission = await CdsJson(CdsRoot + "/processes/reanalysis-era5-single-levels-timeseries/execution",
            JsonSerializer.Serialize(new { inputs }), ct).ConfigureAwait(false);
        string id = submission.RootElement.GetProperty("jobID").GetString()!;
        string job = CdsRoot + "/jobs/" + Uri.EscapeDataString(id);
        while (true)
        {
            using var state = await CdsJson(job, null, ct).ConfigureAwait(false);
            string? status = state.RootElement.GetProperty("status").GetString();
            if (status == "successful") break;
            if (status is "failed" or "rejected" or "dismissed") throw new InvalidDataException("CDS job " + status + "; inspect the request in the CDS account.");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        using var result = await CdsJson(job + "/results", null, ct).ConfigureAwait(false);
        var asset = result.RootElement.GetProperty("asset").GetProperty("value");
        var uri = new Uri(asset.GetProperty("href").GetString()!);
        if (uri.Scheme != "https" || uri.IsLoopback || System.Net.IPAddress.TryParse(uri.Host, out _))
            throw new InvalidDataException("Unexpected CDS result URL.");
        string temp = Path.Combine(Path.GetTempPath(), "solarshade-cds-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Credentials are sent only to CDS control endpoints, never to the signed result URL.
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var block = new byte[65536]; long bytes = 0; int n;
                while ((n = await stream.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
                {
                    bytes += n;
                    if (bytes > 256L * 1024 * 1024) throw new InvalidDataException("CDS point result exceeds 256 MiB.");
                    await file.WriteAsync(block.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            using var input = File.OpenRead(temp);
            bool zipped = input.ReadByte() == 'P' && input.ReadByte() == 'K'; input.Position = 0;
            if (!zipped) { using var reader = new StreamReader(input); return ReadCdsCsv(reader, start, end, ct); }
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var entries = zip.Entries.Where(x => x.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToArray();
            // Both selected radiation variables belong to the same collection group.
            if (entries.Length != 1 || entries[0].Length > 256L * 1024 * 1024)
                throw new InvalidDataException("Unexpected CDS archive layout; expected one radiation CSV.");
            using var csv = new StreamReader(entries[0].Open()); return ReadCdsCsv(csv, start, end, ct);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task<JsonDocument> CdsJson(string url, string? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(options.RequestTimeout);
        using var message = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
        message.Headers.Add("PRIVATE-TOKEN", options.CdsPersonalAccessToken);
        if (body != null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"CDS HTTP {(int)response.StatusCode}; check token, accepted dataset licence, and service status.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    private static List<IrradianceSample> ReadCdsCsv(TextReader reader, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var headers = Csv(reader.ReadLine() ?? "", ',');
        int Find(params string[] names) => Array.FindIndex(headers, x => names.Contains(x));
        int time = Find("valid_time", "time"), b = Find("total_sky_direct_solar_radiation_at_surface", "fdir"),
            g = Find("surface_solar_radiation_downwards", "ssrd");
        if (time < 0 || b < 0 || g < 0) throw new InvalidDataException("CDS CSV lacks expected time/SSRD/FDIR columns.");
        var rows = new List<IrradianceSample>(); string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = Csv(line, ',');
            var instant = DateTimeOffset.Parse(cells[time], Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var date = DateOnly.FromDateTime(instant.UtcDateTime);
            if (date < start || date > end) continue;
            double direct = double.Parse(cells[b], Invariant), global = double.Parse(cells[g], Invariant);
            rows.Add(new(instant, direct / 3600.0, global < 0 ? double.NaN : (global - direct) / 3600.0));
        }
        return rows;
    }
}
