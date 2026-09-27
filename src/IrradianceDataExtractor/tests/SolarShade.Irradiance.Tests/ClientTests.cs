using System.Globalization;
using System.Net;
using System.Text.Json;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Validation;
using Xunit;

namespace SolarShade.Irradiance.Tests;

public sealed class ClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int calls;
        public int Calls => calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(message, Interlocked.Increment(ref calls))); }
    }
    private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body is string text ? text : JsonSerializer.Serialize(body)) };
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(x => x.Split('=', 2)).GroupBy(x => Uri.UnescapeDataString(x[0]))
        .ToDictionary(x => x.Key, x => Uri.UnescapeDataString(x.First()[1]));
    private static IrradianceRequest Request(IrradianceService service = IrradianceService.OpenMeteo) => new(new(2025, 5, 1), new(2025, 5, 2), 106.7, 10.8, service);
    private static IrradianceOptions Options(TimeZoneInfo? zone = null) => new() { TimeZone = zone ?? TimeZoneInfo.Utc, MaxConcurrency = 1 };
    private static object Meteo(HttpRequestMessage message, string? defect = null)
    {
        var q = Query(message.RequestUri!); Assert.Equal("GMT", q["timezone"]); Assert.Equal("unixtime", q["timeformat"]);
        var a = DateTimeOffset.Parse(q["start_date"] + "T00:00:00Z", CultureInfo.InvariantCulture);
        var z = DateTimeOffset.Parse(q["end_date"] + "T23:00:00Z", CultureInfo.InvariantCulture);
        var times = Enumerable.Range(0, (int)(z - a).TotalHours + 1).Select(i => a.AddHours(i).ToUnixTimeSeconds()).ToList();
        if (defect == "gap") times.RemoveAt(12);
        if (defect == "duplicate") times[12] = times[11];
        if (defect == "boundary") times.RemoveAt(0);
        var direct = times.Select(_ => (double?)123.456789).ToArray();
        if (defect == "null") direct[12] = null;
        if (defect == "negative") direct[12] = -999;
        return new { utc_offset_seconds = 0, hourly_units = new { direct_radiation = defect == "units" ? "kWh/m²" : "W/m²", diffuse_radiation = "W/m²" },
            hourly = new { time = times, direct_radiation = direct, diffuse_radiation = times.Select(_ => 45.6).ToArray() } };
    }

    [Fact] public async Task PublicFunctionReturnsOneAndWritesThreeColumns()
    {
        using var handler = new Handler((r, _) => Response(Meteo(r))); using var http = new HttpClient(handler);
        var client = new IrradianceClient(Options(), http); string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            Assert.Equal(1, await client.PullAsync(new(2025, 5, 1), new(2025, 5, 2), 106.7, 10.8, IrradianceService.OpenMeteo, path));
            var data = await client.FetchAsync(Request()); WorkbookValidator.Validate(path, data.Samples, TimeZoneInfo.Utc);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("gap")][InlineData("duplicate")][InlineData("boundary")][InlineData("null")][InlineData("negative")][InlineData("units")]
    public async Task InvalidProviderDataNeverOverwritesExistingFile(string defect)
    {
        using var handler = new Handler((r, _) => Response(Meteo(r, defect))); using var http = new HttpClient(handler);
        var client = new IrradianceClient(Options(), http); string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        await File.WriteAllTextAsync(path, "existing");
        try { Assert.Equal(0, (await client.PullDetailedAsync(Request(), path)).Status); Assert.Equal("existing", await File.ReadAllTextAsync(path)); }
        finally { File.Delete(path); }
    }

    [Theory][InlineData(3, 9, 23)][InlineData(11, 2, 25)]
    public async Task DaylightSavingDaysPreserveInstantsAndOffsets(int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        using var handler = new Handler((r, _) => Response(Meteo(r))); using var http = new HttpClient(handler);
        var client = new IrradianceClient(Options(zone), http); var date = new DateOnly(2025, month, day);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            var result = await client.PullDetailedAsync(new(date, date.AddDays(1), 106.7, 10.8, IrradianceService.OpenMeteo), path);
            Assert.True(result.Succeeded, result.Message); Assert.Equal(hours, result.Samples.Count);
            WorkbookValidator.Validate(path, result.Samples, zone);
        }
        finally { File.Delete(path); }
    }

    [Fact] public async Task FractionalOffsetAndNonEnglishCultureDoNotShiftData()
    {
        var previous = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu");
            using var handler = new Handler((r, _) => { Assert.Equal("106.7", Query(r.RequestUri!)["longitude"]); return Response(Meteo(r)); });
            using var http = new HttpClient(handler); var client = new IrradianceClient(Options(zone), http);
            var result = await client.FetchAsync(Request()); Assert.True(result.Succeeded, result.Message);
            Assert.Equal(24, result.Samples.Count); Assert.Equal(45, TimeZoneInfo.ConvertTime(result.Samples[0].TimestampUtc, zone).Minute);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(IrradianceService.OpenMeteo, 3, 2, 24, 1)]
    [InlineData(IrradianceService.NasaPower, 3, 2, 24, 1)]
    [InlineData(IrradianceService.OpenMeteo, 7, 2, 24, 2)]
    [InlineData(IrradianceService.NasaPower, 7, 2, 24, 2)]
    [InlineData(IrradianceService.OpenMeteo, 3, 26, 23, 1)]
    [InlineData(IrradianceService.NasaPower, 3, 26, 23, 1)]
    [InlineData(IrradianceService.OpenMeteo, 10, 29, 25, 2)]
    [InlineData(IrradianceService.NasaPower, 10, 29, 25, 2)]
    public async Task ParisFrenchLocalePreservesProviderUtcAndExportsLocalOffsets(IrradianceService service, int month, int day, int hours, int firstOffset)
    {
        var previous = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
            var date = new DateOnly(2023, month, day);
            using var handler = new Handler((r, _) =>
            {
                var q = Query(r.RequestUri!); Assert.Equal("43.5", q["latitude"]); Assert.Equal("1.5", q["longitude"]);
                if (service == IrradianceService.OpenMeteo) return Response(Meteo(r));
                Assert.Equal("UTC", q["time-standard"]);
                var a = DateTime.ParseExact(q["start"], "yyyyMMdd", CultureInfo.InvariantCulture);
                var b = DateTime.ParseExact(q["end"], "yyyyMMdd", CultureInfo.InvariantCulture).AddDays(1);
                var times = Enumerable.Range(0, (int)(b - a).TotalHours).Select(i => a.AddHours(i).ToString("yyyyMMddHH", CultureInfo.InvariantCulture)).ToArray();
                return Response(new { header = new { time_standard = "UTC" }, parameters = new Dictionary<string, object>
                    { ["ALLSKY_SFC_SW_DIRH"] = new { units = "Wh/m^2" }, ["ALLSKY_SFC_SW_DIFF"] = new { units = "Wh/m^2" } },
                    properties = new { parameter = new Dictionary<string, object> { ["ALLSKY_SFC_SW_DIRH"] = times.ToDictionary(x => x, _ => 100.0),
                        ["ALLSKY_SFC_SW_DIFF"] = times.ToDictionary(x => x, _ => 40.0) } } });
            });
            using var http = new HttpClient(handler);
            var result = await new IrradianceClient(Options(zone), http).PullDetailedAsync(new(date, date.AddDays(1), 1.5, 43.5, service), path);
            Assert.True(result.Succeeded, result.Message); Assert.Equal(hours, result.Samples.Count);
            Assert.Equal(zone.Id, result.TimeZoneId);
            var first = TimeZoneInfo.ConvertTime(result.Samples[0].TimestampUtc, zone);
            Assert.Equal(date.ToDateTime(TimeOnly.MinValue), first.DateTime);
            Assert.Equal(TimeSpan.FromHours(firstOffset), first.Offset);
            WorkbookValidator.Validate(path, result.Samples, zone);
        }
        finally { CultureInfo.CurrentCulture = previous; File.Delete(path); }
    }

    [Theory][InlineData(IrradianceService.Nsrdb)][InlineData(IrradianceService.Cams)][InlineData(IrradianceService.Oikolab)][InlineData(IrradianceService.CopernicusCds)]
    public async Task MissingCredentialsFailBeforeNetwork(IrradianceService service)
    {
        using var handler = new Handler((_, _) => throw new Exception("Must not call network")); using var http = new HttpClient(handler);
        var result = await new IrradianceClient(Options(), http).FetchAsync(Request(service)); Assert.Equal(0, result.Status); Assert.Equal(0, handler.Calls);
    }

    [Fact] public async Task RateLimitRetriesOnce()
    {
        using var handler = new Handler((r, n) => n == 1 ? new(HttpStatusCode.TooManyRequests)
        { Headers = { RetryAfter = new(TimeSpan.FromMilliseconds(1)) } } : Response(Meteo(r)));
        using var http = new HttpClient(handler); Assert.True((await new IrradianceClient(Options(), http).FetchAsync(Request())).Succeeded);
        Assert.Equal(2, handler.Calls);
    }

    [Fact] public async Task ClientErrorsAreNotRetriedAndSecretsAreRedacted()
    {
        using var handler = new Handler((_, _) => new(HttpStatusCode.BadRequest) { Content = new StringContent("example-secret example%40mail.com") });
        using var http = new HttpClient(handler); var client = new IrradianceClient(Options() with { NsrdbApiKey = "example-secret", NsrdbEmail = "example@mail.com" }, http);
        var result = await client.FetchAsync(Request(IrradianceService.Nsrdb)); Assert.Equal(0, result.Status); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("example", result.Message);
    }

    [Fact] public async Task CancellationReturnsZeroAndCreatesNoWorkbook()
    {
        using var ct = new CancellationTokenSource(); ct.Cancel();
        using var handler = new Handler((r, _) => Response(Meteo(r))); using var http = new HttpClient(handler);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        Assert.Equal(0, (await new IrradianceClient(Options(), http).PullDetailedAsync(Request(), path, ct.Token)).Status);
        Assert.False(File.Exists(path));
    }

    [Fact] public async Task NasaUsesNativeDirectHorizontalAndPreservesOneHourEnergyMagnitude()
    {
        var times = Enumerable.Range(0, 24).Select(i => $"20250501{i:00}").ToArray();
        using var handler = new Handler((r, _) =>
        {
            Assert.Equal("UTC", Query(r.RequestUri!)["time-standard"]);
            Assert.Equal("ALLSKY_SFC_SW_DIRH,ALLSKY_SFC_SW_DIFF", Query(r.RequestUri!)["parameters"]);
            return Response(new { header = new { time_standard = "UTC" }, parameters = new Dictionary<string, object>
                { ["ALLSKY_SFC_SW_DIRH"] = new { units = "Wh/m^2" }, ["ALLSKY_SFC_SW_DIFF"] = new { units = "Wh/m^2" } },
                properties = new { parameter = new Dictionary<string, object> { ["ALLSKY_SFC_SW_DIRH"] = times.ToDictionary(x => x, _ => 100.0),
                    ["ALLSKY_SFC_SW_DIFF"] = times.ToDictionary(x => x, _ => 40.0) } } });
        });
        using var http = new HttpClient(handler); var result = await new IrradianceClient(Options(), http).FetchAsync(Request(IrradianceService.NasaPower));
        Assert.True(result.Succeeded, result.Message); Assert.Equal(100, result.Samples[0].DirectHorizontal);
    }

    [Theory][InlineData(1)][InlineData(2)]
    public async Task RetiredServiceIdsFailBeforeNetwork(int id)
    {
        using var handler = new Handler((_, _) => throw new Exception("Must not call network"));
        using var http = new HttpClient(handler);
        var result = await new IrradianceClient(Options(), http).FetchAsync(Request((IrradianceService)id));
        Assert.Equal(0, result.Status); Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(ProviderCatalog.All, x => (int)x.Service == id);
    }

    [Fact] public async Task CamsUsesAllSkyColumnsAndIntervalEnd()
    {
        using var handler = new Handler((r, _) =>
        {
            Assert.Contains("username=person%40example.com", Query(r.RequestUri!)["DataInputs"]);
            var a = new DateTimeOffset(2025, 4, 30, 23, 0, 0, TimeSpan.Zero);
            string csv = "# Observation period;Clear sky BHI;BHI;DHI\n" + string.Join('\n', Enumerable.Range(0, 25)
                .Select(i => $"{a.AddHours(i):O}/{a.AddHours(i + 1):O};900;100;40"));
            return Response(csv);
        });
        using var http = new HttpClient(handler); var result = await new IrradianceClient(Options() with { CamsEmail = "person@example.com" }, http).FetchAsync(Request(IrradianceService.Cams));
        Assert.True(result.Succeeded, result.Message); Assert.Equal(24, result.Samples.Count); Assert.Equal(100, result.Samples[0].DirectHorizontal);
    }

    [Fact] public async Task NsrdbDiscoversYearAndDerivesHorizontalNotDni()
    {
        using var handler = new Handler((r, n) =>
        {
            if (n == 1) return Response(new { outputs = new[] { new { links = new[] { new { year = 2025, interval = 60,
                link = "https://developer.nlr.gov/api/solar/test-download.csv?api_key=yourapikey&email=youremail" } } } } });
            var q = Query(r.RequestUri!); Assert.Equal("true", q["utc"]); Assert.Equal("true", q["leap_day"]); Assert.Equal("ghi,dhi", q["attributes"]);
            return Response("Source,Time Zone\nNSRDB,0\nYear,Month,Day,Hour,Minute,GHI,DHI\n" + string.Join('\n',
                Enumerable.Range(0, 24).Select(i => $"2025,5,1,{i},0,300,50")));
        });
        using var http = new HttpClient(handler); var result = await new IrradianceClient(Options() with { NsrdbApiKey = "test", NsrdbEmail = "test@example.com" }, http)
            .FetchAsync(Request(IrradianceService.Nsrdb)); Assert.True(result.Succeeded, result.Message); Assert.Equal(250, result.Samples[0].DirectHorizontal);
    }

    [Fact] public async Task OikolabDecodesNestedJsonAndReorderedColumns()
    {
        using var handler = new Handler((r, _) =>
        {
            Assert.Equal("test", r.Headers.GetValues("api-key").Single());
            return Response(new { data = JsonSerializer.Serialize(new { index = Enumerable.Range(0, 24).Select(i => new DateTimeOffset(2025, 5, 1, i, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()),
                columns = new[] { "surface_diffuse_solar_radiation (W/m^2)", "surface_direct_solar_radiation (W/m^2)" }, data = Enumerable.Range(0, 24).Select(_ => new[] { 40, 100 }) }) });
        });
        using var http = new HttpClient(handler); var result = await new IrradianceClient(Options() with { OikolabApiKey = "test" }, http).FetchAsync(Request(IrradianceService.Oikolab));
        Assert.True(result.Succeeded, result.Message); Assert.Equal(100, result.Samples[0].DirectHorizontal);
    }

    [Fact] public async Task CdsSubmitsPollsAndConvertsJoulesWithoutLeakingTokenToAsset()
    {
        using var handler = new Handler((r, n) =>
        {
            if (n < 4) Assert.Equal("test", r.Headers.GetValues("PRIVATE-TOKEN").Single());
            return n switch
            {
                1 => Response(new { jobID = "fixture" }), 2 => Response(new { status = "successful" }),
                3 => Response(new { asset = new { value = new Dictionary<string, object> { ["href"] = "https://download.ecmwf.int/fixture.csv" } } }),
                _ => Asset()
            };
            HttpResponseMessage Asset()
            {
                Assert.False(r.Headers.Contains("PRIVATE-TOKEN"));
                return Response("valid_time,ssrd,fdir\n" + string.Join('\n', Enumerable.Range(0, 24).Select(i => $"2025-05-01 {i:00}:00:00,540000,360000")));
            }
        });
        using var http = new HttpClient(handler); var result = await new IrradianceClient(Options() with { CdsPersonalAccessToken = "test" }, http).FetchAsync(Request(IrradianceService.CopernicusCds));
        Assert.True(result.Succeeded, result.Message); Assert.Equal(100, result.Samples[0].DirectHorizontal); Assert.Equal(50, result.Samples[0].DiffuseHorizontal);
    }
}
