using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Irradiance.Transposition.Validation;
using Xunit;

public class TranspositionTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2025-05-01T12:00:00+07:00", CultureInfo.InvariantCulture);
    private static readonly PanelOrientation Panel = new(60, 180);

    [Fact]
    public void CallbackFailuresReturnZeroWithTimestampContext()
    {
        var result = TranspositionModule.Compute([new(Time, 1, 1)], Panel,
            _ => throw new KeyNotFoundException("Solar timestamp missing"), SamplingWindow.Instant);
        Assert.Equal(0, result.Status); Assert.Empty(result.Samples);
        Assert.Contains("Row 1", result.Message); Assert.Contains("Solar timestamp missing", result.Message);
    }

    [Fact]
    public void InvalidXmlMetadataCannotPublishACorruptWorkbook()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            File.WriteAllText(path, "existing");
            var result = TranspositionModule.ComputeToWorkbook([new(Time, 1, 2)], new(0, 0),
                _ => new(60, 180), SamplingWindow.Instant, path, options: new() { Provenance = "invalid\u0001" });
            Assert.Equal(0, result.Status); Assert.Equal("existing", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoaaRefractionIsExplicitAndRaisesApparentSun()
    {
        Assert.Equal(new SunPosition(0, 123), SunPosition.FromGeometricNoaa(0, 123));
        var horizon = SunPosition.FromGeometricNoaa(90, 123);
        Assert.Equal(90 - 1735d / 3600, horizon.ZenithDegrees, 12);
        Assert.Equal(123, horizon.AzimuthDegrees);
        Assert.Throws<ArgumentOutOfRangeException>(() => SunPosition.FromGeometricNoaa(double.NaN, 0));
    }

    [Fact]
    public void IndependentPvlibReference1308CasesBothModels()
    {
        var result = ReferenceChecks.Run(Path.Combine(AppContext.BaseDirectory, "Fixtures/pvlib-0.15.2.json"));
        Assert.Equal(1308, result.Cases);
        Assert.InRange(result.MaximumAbsoluteErrorWm2, 0, 1e-8);
    }

    [Theory]
    [InlineData(DiffuseModel.HayDavies)]
    [InlineData(DiffuseModel.PerezDriesse)]
    public void TiltTowardSunDoublesBeamAndBeamDominatedTotalIncreases(DiffuseModel model)
    {
        var flat = SkyTransposition.EvaluateDni(new(0, 180), new(60, 180), 800, 100, 1366.1, model);
        var tilted = SkyTransposition.EvaluateDni(Panel, new(60, 180), 800, 100, 1366.1, model);
        Assert.Equal(400, flat.Direct, 10); Assert.Equal(800, tilted.Direct, 10);
        Assert.True(tilted.Total > flat.Total);
        var away = SkyTransposition.EvaluateDni(new(60, 0), new(60, 180), 800, 100, 1366.1, model);
        Assert.Equal(0, away.Direct);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(60, .75)] [InlineData(90, .5)]
    public void IsotropicUsesCosineWeightedSkyView(double tilt, double factor)
    {
        var actual = SkyTransposition.EvaluateDni(new(tilt, 123), new(60, 90), 0, 100, 1366.1, DiffuseModel.Isotropic);
        Assert.Equal(100 * factor, actual.SkyDiffuse, 10);
    }

    [Fact]
    public void HorizontalBatchPreservesMeasuredComponentsEvenNearHorizon()
    {
        var result = TranspositionModule.Compute([new(Time, 1, 30)], new(0, 90), _ => throw new Exception("Not needed"), SamplingWindow.Instant);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Samples[0].Direct); Assert.Equal(30, result.Samples[0].SkyDiffuse);
        Assert.Equal(TranspositionFlags.HorizontalIdentity, result.Samples[0].Flags);
    }

    [Fact]
    public void IntervalBeamUsesIntegratedProjectionAndPreservesLabel()
    {
        // Zenith sweeps from 30 to 60 degrees in the south: independently integrate with the sine antiderivative.
        SunPosition Position(DateTimeOffset t) => new(30 + (t - Time).TotalMinutes / 2, 180);
        double meanCosine = (Math.Sin(Math.PI / 3) - Math.Sin(Math.PI / 6)) / (Math.PI / 6);
        var result = TranspositionModule.Compute([new(Time, 800 * meanCosine, 100)], Panel, Position,
            SamplingWindow.FollowingHour(1000), DiffuseModel.Isotropic);
        Assert.True(result.Succeeded, result.Message);
        double meanPanelCosine = (Math.Sin(Math.PI / 6) - Math.Sin(0)) / (Math.PI / 6);
        Assert.Equal(800 * meanPanelCosine, result.Samples[0].Direct, 7);
        Assert.Equal(75, result.Samples[0].SkyDiffuse, 7);
        Assert.Equal(Time, result.Samples[0].Timestamp);
        Assert.True(result.Samples[0].DirectFactor > 1);
    }

    [Fact]
    public void SunriseUsesDaylightFractionToInferDni()
    {
        var result = TranspositionModule.Compute([new(Time, 200, 100)], Panel,
            t => new(t < Time.AddMinutes(30) ? 100 : 60, 180), SamplingWindow.FollowingHour(2), DiffuseModel.Isotropic);
        Assert.True(result.Succeeded, result.Message);
        var row = result.Samples[0];
        Assert.Equal(800, row.EffectiveDni!.Value, 10); Assert.Equal(400, row.Direct, 10); Assert.Equal(75, row.SkyDiffuse, 10);
        Assert.True(row.Flags.HasFlag(TranspositionFlags.NightDiffuseIsotropic));
    }

    [Theory]
    [InlineData(90, 1)] [InlineData(120, 200)] [InlineData(89.9999, 1)] [InlineData(60, 1000)]
    public void InconsistentBeamFailsWithoutPartialRows(double zenith, double bhi)
    {
        var result = TranspositionModule.Compute([new(Time, 0, 0), new(Time.AddHours(1), bhi, 10)], Panel,
            _ => new(zenith, 180), SamplingWindow.Instant);
        Assert.False(result.Succeeded); Assert.Empty(result.Samples); Assert.Contains("Row 2", result.Message);
    }

    [Fact]
    public void NightDiffuseFallbackIsExplicit()
    {
        var result = SkyTransposition.EvaluateDni(Panel, new(100, 180), 0, 20, 1366.1);
        Assert.Equal(15, result.SkyDiffuse, 10); Assert.Equal(0, result.Direct);
        Assert.True(result.Flags.HasFlag(TranspositionFlags.NightDiffuseIsotropic));
        Assert.Throws<ArgumentException>(() => SkyTransposition.EvaluateDni(Panel, new(100, 180), 1, 20, 1366.1));
    }

    [Fact]
    public void ZeroComponentsHaveNoInventedFactorsOrSolarDependency()
    {
        var result = TranspositionModule.Compute([new(Time, 0, 0)], Panel, _ => throw new Exception("Not needed"), SamplingWindow.Instant);
        Assert.True(result.Succeeded); Assert.Null(result.Samples[0].DirectFactor); Assert.Null(result.Samples[0].DiffuseFactor);
    }

    [Fact]
    public void DiffuseOnlyDayStillUsesSolarPositionForPerez()
    {
        int calls = 0;
        var result = TranspositionModule.Compute([new(Time, 0, 100)], Panel,
            _ => { calls++; return new(30, 180); }, SamplingWindow.Instant);
        Assert.True(result.Succeeded); Assert.Equal(1, calls);
        var expected = SkyTransposition.EvaluateDni(Panel, new(30, 180), 0, 100, SkyTransposition.ExtraterrestrialDni(Time));
        Assert.Equal(expected.SkyDiffuse, result.Samples[0].SkyDiffuse);
    }

    [Fact]
    public void OffsetEquivalentDuplicateInstantsRejected()
    {
        var result = TranspositionModule.Compute([new(Time, 100, 20), new(Time.ToUniversalTime(), 100, 20)], Panel,
            _ => new(60, 180), SamplingWindow.Instant);
        Assert.False(result.Succeeded); Assert.Empty(result.Samples);
        Assert.Equal(SkyTransposition.ExtraterrestrialDni(Time), SkyTransposition.ExtraterrestrialDni(Time.ToOffset(TimeSpan.FromHours(-12))));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void InvalidIrradianceRejected(double value)
    {
        Assert.False(TranspositionModule.Compute([new(Time, value, 20)], Panel, _ => new(60, 180), SamplingWindow.Instant).Succeeded);
        Assert.Throws<ArgumentOutOfRangeException>(() => SkyTransposition.EvaluateDni(Panel, new(60, 180), 100, value, 1366));
    }

    [Fact]
    public void InvalidAnglesAndWindowRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SkyTransposition.EvaluateDni(new(91, 0), new(60, 180), 1, 1, 1366));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkyTransposition.EvaluateDni(Panel, new(60, -1), 1, 1, 1366));
        Assert.False(TranspositionModule.Compute([new(Time, 1, 1)], Panel, _ => new(60, 180), new(0, 0, 2)).Succeeded);
        Assert.Null(SamplingWindow.ForService(IrradianceService.Nsrdb));
        Assert.Equal(0, SamplingWindow.ForService(IrradianceService.NasaPower)!.StartOffsetMinutes);
        Assert.Equal(-60, SamplingWindow.ForService(IrradianceService.OpenMeteo)!.StartOffsetMinutes);
    }

    [Fact]
    public void ParallelAndSerialAgreeInOrder()
    {
        var rows = Enumerable.Range(0, 150).Select(i => new IrradianceSample(Time.AddHours(i), 400, 100)).ToArray();
        var serial = TranspositionModule.Compute(rows, Panel, _ => new(60, 180), SamplingWindow.FollowingHour());
        var parallel = TranspositionModule.Compute(rows, Panel, _ => new(60, 180), SamplingWindow.FollowingHour(), options: new() { MaxDegreeOfParallelism = 2 });
        Assert.True(serial.Succeeded && parallel.Succeeded);
        Assert.Equal(serial.Samples, parallel.Samples);
    }

    [Fact]
    public void FailedAndCancelledExportsPreserveExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            File.WriteAllText(path, "existing");
            var failed = TranspositionModule.ComputeToWorkbook([new(Time, 1, 1)], Panel, _ => new(100, 180), SamplingWindow.Instant, path);
            Assert.Equal(0, failed.Status); Assert.Equal("existing", File.ReadAllText(path));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => TranspositionModule.ComputeToWorkbook([new(Time, 1, 1)], Panel,
                _ => new(60, 180), SamplingWindow.Instant, path, cancellationToken: cancellation.Token));
            Assert.Equal("existing", File.ReadAllText(path));
            Assert.Equal(0, TranspositionModule.TryExport([new(Time, 1, 1)], Panel, _ => new(60, 180),
                SamplingWindow.Instant, path, cancellationToken: cancellation.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WorkbookHasThreeCorrectColumnsAndPreservesDstInstantsAndFractionalSeconds()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var t = new DateTimeOffset(2025, 11, 2, 5, 30, 0, TimeSpan.Zero).AddTicks(1234567);
        try
        {
            var result = TranspositionModule.ComputeToWorkbook([new(t, 1, 2), new(t.AddHours(1), 3, 4)], new(0, 0),
                _ => new(60, 180), SamplingWindow.Instant, path, options: new() { TimeZone = zone });
            Assert.True(result.Succeeded, result.Message);
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = XDocument.Load(stream).Descendants(n + "row").ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal("Direct on panel (W/m²)", rows[0].Elements(n + "c").ElementAt(1).Descendants(n + "t").Single().Value);
            for (int i = 1; i <= 2; i++)
            {
                var cells = rows[i].Elements(n + "c").ToArray(); Assert.Equal(3, cells.Length);
                var actual = DateTimeOffset.Parse(cells[0].Descendants(n + "t").Single().Value, CultureInfo.InvariantCulture);
                Assert.Equal(t.AddHours(i - 1), actual); Assert.Equal(TimeSpan.FromHours(i == 1 ? -4 : -5), actual.Offset);
                Assert.Equal(2 * i - 1, double.Parse(cells[1].Element(n + "v")!.Value, CultureInfo.InvariantCulture));
                Assert.Equal(2 * i, double.Parse(cells[2].Element(n + "v")!.Value, CultureInfo.InvariantCulture));
            }
        }
        finally { File.Delete(path); }
    }
}
