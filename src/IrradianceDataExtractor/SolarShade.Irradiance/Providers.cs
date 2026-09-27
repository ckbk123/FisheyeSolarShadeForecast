using System.Globalization;
using System.Text.Json;

namespace SolarShade.Irradiance;

public sealed partial class IrradianceClient
{
    private static readonly SemaphoreSlim CamsGate = new(1);
    private async Task<List<IrradianceSample>> DownloadChunk(IrradianceRequest request, DateOnly start, DateOnly end, CancellationToken ct)
    {
        string lat = request.Latitude.ToString("R", Invariant), lon = request.Longitude.ToString("R", Invariant);
        string from = start.ToString("yyyy-MM-dd", Invariant), to = end.ToString("yyyy-MM-dd", Invariant);
        if (request.Service == IrradianceService.CopernicusCds) return await Cds(request, start, end, ct).ConfigureAwait(false);
        if (request.Service == IrradianceService.Nsrdb) return await Nsrdb(request, start.Year, ct).ConfigureAwait(false);
        if (request.Service == IrradianceService.Cams)
        {
            await CamsGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string inputs = $"latitude={lat};longitude={lon};altitude=-999;date_begin={from};date_end={to};time_ref=UT;summarization=PT01H;username={Uri.EscapeDataString(options.CamsEmail!)}";
                return ParseCams(await Get(Url("https://api.soda-solardata.com/service/wps",
                    ("Service", "WPS"), ("Request", "Execute"), ("Identifier", "get_cams_radiation"), ("version", "1.0.0"),
                    ("DataInputs", inputs), ("RawDataOutput", "irradiation")), ct).ConfigureAwait(false));
            }
            finally { CamsGate.Release(); }
        }
        string url = request.Service switch
        {
            IrradianceService.NasaPower => Url("https://power.larc.nasa.gov/api/temporal/hourly/point",
                ("parameters", "ALLSKY_SFC_SW_DIRH,ALLSKY_SFC_SW_DIFF"), ("community", "RE"), ("longitude", lon),
                ("latitude", lat), ("start", start.ToString("yyyyMMdd", Invariant)), ("end", end.ToString("yyyyMMdd", Invariant)),
                ("format", "JSON"), ("time-standard", "UTC")),
            IrradianceService.OpenMeteo => Url("https://archive-api.open-meteo.com/v1/archive", ("latitude", lat),
                ("longitude", lon), ("start_date", from), ("end_date", to), ("hourly", "direct_radiation,diffuse_radiation"),
                ("timezone", "GMT"), ("timeformat", "unixtime"), ("models", options.OpenMeteoModel)),
            IrradianceService.Oikolab => Url("https://api.oikolab.com/weather", ("param", "surface_direct_solar_radiation"),
                ("param", "surface_diffuse_solar_radiation"), ("lat", lat), ("lon", lon), ("start", from + "T00:00:00"),
                ("end", to + "T23:00:00"), ("freq", "H"), ("model", "era5"), ("format", "json")),
            _ => throw new ArgumentException("Unsupported provider.")
        };
        using var doc = JsonDocument.Parse(await Get(url, ct,
            request.Service == IrradianceService.Oikolab ? options.OikolabApiKey : null).ConfigureAwait(false));
        var root = doc.RootElement;
        var rows = new List<IrradianceSample>();
        switch (request.Service)
        {
            case IrradianceService.NasaPower:
                if (root.GetProperty("header").GetProperty("time_standard").GetString() != "UTC")
                    throw new InvalidDataException("NASA did not return UTC.");
                foreach (var name in new[] { "ALLSKY_SFC_SW_DIRH", "ALLSKY_SFC_SW_DIFF" })
                    RequireUnit(root.GetProperty("parameters").GetProperty(name).GetProperty("units").GetString(), "Wh/m^2");
                var data = root.GetProperty("properties").GetProperty("parameter");
                var diffuse = data.GetProperty("ALLSKY_SFC_SW_DIFF");
                var direct = data.GetProperty("ALLSKY_SFC_SW_DIRH");
                if (direct.EnumerateObject().Count() != diffuse.EnumerateObject().Count())
                    throw new InvalidDataException("NASA component timestamps do not match.");
                foreach (var point in direct.EnumerateObject())
                    rows.Add(new(ParseUtc(point.Name, "yyyyMMddHH"), Number(point.Value), Number(diffuse.GetProperty(point.Name))));
                break;
            case IrradianceService.OpenMeteo:
                if (root.GetProperty("utc_offset_seconds").GetInt32() != 0) throw new InvalidDataException("Expected UTC response.");
                foreach (var name in new[] { "direct_radiation", "diffuse_radiation" })
                    RequireUnit(root.GetProperty("hourly_units").GetProperty(name).GetString(), "W/m²");
                var h = root.GetProperty("hourly");
                rows = ParseArrays(h.GetProperty("time"), h.GetProperty("direct_radiation"), h.GetProperty("diffuse_radiation"));
                break;
            case IrradianceService.Oikolab:
                using (var inner = JsonDocument.Parse(root.GetProperty("data").GetString()!))
                {
                    var table = inner.RootElement;
                    var columns = table.GetProperty("columns").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    int b = Array.FindIndex(columns, x => x.StartsWith("surface_direct_solar_radiation", StringComparison.Ordinal));
                    int d = Array.FindIndex(columns, x => x.StartsWith("surface_diffuse_solar_radiation", StringComparison.Ordinal));
                    if (b < 0 || d < 0) throw new InvalidDataException("Missing Oikolab horizontal components.");
                    var times = table.GetProperty("index"); var values = table.GetProperty("data");
                    if (times.GetArrayLength() != values.GetArrayLength()) throw new InvalidDataException("Oikolab array mismatch.");
                    for (int i = 0; i < times.GetArrayLength(); i++) rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(times[i].GetInt64()),
                        Number(values[i][b]), Number(values[i][d])));
                }
                break;
        }
        return rows;
    }

    private static void RequireUnit(string? actual, string expected)
    { if (actual != expected) throw new InvalidDataException($"Unexpected units '{actual}'; expected '{expected}'."); }

    private static List<IrradianceSample> ParseArrays(JsonElement times, JsonElement direct, JsonElement diffuse)
    {
        if (times.GetArrayLength() != direct.GetArrayLength() || times.GetArrayLength() != diffuse.GetArrayLength())
            throw new InvalidDataException("Provider arrays have unequal lengths.");
        var rows = new List<IrradianceSample>(times.GetArrayLength());
        for (int i = 0; i < times.GetArrayLength(); i++)
            rows.Add(new(DateTimeOffset.FromUnixTimeSeconds(times[i].GetInt64()),
                Number(direct[i]), Number(diffuse[i])));
        return rows;
    }

    private async Task<List<IrradianceSample>> Nsrdb(IrradianceRequest request, int year, CancellationToken ct)
    {
        using var discovery = JsonDocument.Parse(await Get(Url("https://developer.nlr.gov/api/solar/nsrdb_data_query.json",
            ("api_key", options.NsrdbApiKey!), ("lat", request.Latitude.ToString("R", Invariant)),
            ("lon", request.Longitude.ToString("R", Invariant))), ct).ConfigureAwait(false));
        string? link = null;
        foreach (var dataset in discovery.RootElement.GetProperty("outputs").EnumerateArray())
            foreach (var candidate in dataset.GetProperty("links").EnumerateArray())
                if (candidate.GetProperty("year").ToString() == year.ToString(Invariant) &&
                    candidate.GetProperty("interval").ToString() == "60" && candidate.GetProperty("link").GetString()!.Contains(".csv"))
                { link ??= candidate.GetProperty("link").GetString(); }
        if (link == null) throw new InvalidDataException($"NSRDB has no hourly historical dataset for {year} at this location.");
        var uri = new Uri(link);
        if (uri.Scheme != "https" || uri.Host is not ("developer.nlr.gov" or "developer.nrel.gov"))
            throw new InvalidDataException("Unexpected NSRDB download host.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]),
                x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "");
        query["api_key"] = options.NsrdbApiKey!; query["email"] = options.NsrdbEmail!;
        query["attributes"] = "ghi,dhi"; query["utc"] = "true"; query["leap_day"] = "true";
        query["interval"] = "60"; query["names"] = year.ToString(Invariant);
        query["wkt"] = $"POINT({request.Longitude.ToString("R", Invariant)} {request.Latitude.ToString("R", Invariant)})";
        return ParseNsrdb(await Get(Url("https://developer.nlr.gov" + uri.AbsolutePath,
            query.Select(x => (x.Key, x.Value)).ToArray()), ct).ConfigureAwait(false));
    }

    private static List<IrradianceSample> ParseNsrdb(string text)
    {
        using var reader = new StringReader(text);
        var metadata = Csv(reader.ReadLine() ?? "", ',');
        var metadataValues = Csv(reader.ReadLine() ?? "", ',');
        int tz = Array.FindIndex(metadata, x => x == "Time Zone");
        if (tz < 0 || double.Parse(metadataValues[tz], Invariant) != 0)
            throw new InvalidDataException("NSRDB did not confirm UTC (Time Zone = 0).");
        var header = Csv(reader.ReadLine() ?? "", ',');
        int Column(string name)
        {
            int index = Array.FindIndex(header, x => x.Equals(name, StringComparison.OrdinalIgnoreCase) || x.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : throw new InvalidDataException("Missing NSRDB column " + name);
        }
        int y = Column("Year"), m = Column("Month"), d = Column("Day"), h = Column("Hour"), min = Column("Minute"), ghi = Column("GHI"), dhi = Column("DHI");
        var rows = new List<IrradianceSample>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = Csv(line, ',');
            int Int(int index) => int.Parse(cells[index], Invariant);
            double global = double.Parse(cells[ghi], Invariant), diffuse = double.Parse(cells[dhi], Invariant);
            // Do not clamp physically inconsistent data or missing values into fabricated zeros.
            rows.Add(new(new DateTimeOffset(Int(y), Int(m), Int(d), Int(h), Int(min), 0, TimeSpan.Zero),
                global < 0 ? double.NaN : global - diffuse, diffuse));
        }
        return rows;
    }

    private static List<IrradianceSample> ParseCams(string text)
    {
        using var reader = new StringReader(text);
        string[]? headers = null;
        var rows = new List<IrradianceSample>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains("Observation period") && line.Contains(';')) { headers = Csv(line.TrimStart('#', ' '), ';'); continue; }
            if (headers == null || line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            int b = Array.FindIndex(headers, x => x == "BHI" || x.StartsWith("BHI "));
            int d = Array.FindIndex(headers, x => x == "DHI" || x.StartsWith("DHI "));
            if (b < 0 || d < 0) throw new InvalidDataException("Missing CAMS all-sky BHI/DHI columns.");
            var cells = Csv(line, ';');
            var interval = cells[0].Split('/');
            var a = DateTimeOffset.Parse(interval[0], Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var z = DateTimeOffset.Parse(interval[1], Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (z - a != TimeSpan.FromHours(1)) throw new InvalidDataException("CAMS interval is not one hour.");
            rows.Add(new(z, double.Parse(cells[b], Invariant), double.Parse(cells[d], Invariant)));
        }
        if (headers == null) throw new InvalidDataException("CAMS did not return radiation CSV; check account activation and coverage.");
        return rows;
    }

    private static string[] Csv(string line, char separator)
    {
        var cells = new List<string>(); var value = new System.Text.StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == separator && !quoted) { cells.Add(value.ToString().Trim()); value.Clear(); }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed CSV quote.");
        cells.Add(value.ToString().Trim()); return cells.ToArray();
    }
}
