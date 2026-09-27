using SolarShade.Irradiance;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;
using Xunit;

public class CorrectionTests
{
    private static readonly DateTimeOffset Noon = new(2025, 5, 1, 12, 0, 0, TimeSpan.FromHours(7));
    private static VisibilityEstimate Factor(double? value) => new(value, 0, 1, 1);
    private static ShadingRow Row(DateTimeOffset timestamp, double? factor) => new(timestamp, null, Factor(factor), "Test");
    private static ShadingResult Shading(params ShadingRow[] rows) => new(rows, Factor(0.2), 0.25, 128, TimeSampling.Instant);

    [Fact]
    public void MatchesUpstreamRowsByInstantAndPreservesInputLabels()
    {
        IrradianceSample[] samples = [new(Noon, 800, 100), new(Noon.AddHours(1), 600, 200)];
        var result = ShadingCorrectionModule.Apply(samples,
            Shading(Row(Noon.AddHours(1).ToUniversalTime(), 1), Row(Noon.ToUniversalTime(), 0.25)));
        Assert.Equal(new CorrectedIrradianceSample(Noon, 600, 80), result[0]);
        Assert.Equal(680d, result[0].TotalHorizontal);
        Assert.Equal(Noon.Offset, result[0].TimestampUtc.Offset);
        Assert.Equal(new CorrectedIrradianceSample(Noon.AddHours(1), 0, 160), result[1]);
    }

    [Fact]
    public void UnknownVisibilityDoesNotInventEnergyOrHideKnownComponents()
    {
        var result = ShadingCorrectionModule.Apply(new(Noon, 100, 50), null, 0);
        Assert.Null(result.DirectHorizontal);
        Assert.Equal(50d, result.DiffuseHorizontal);
        Assert.Null(result.TotalHorizontal);
        Assert.Equal(0d, ShadingCorrectionModule.Apply(new(Noon, 0, 0), null, null).TotalHorizontal);
    }

    [Fact]
    public void RejectsMissingAndDuplicateInstants()
    {
        IrradianceSample[] samples = [new(Noon, 100, 50), new(Noon.AddHours(1), 100, 50)];
        Assert.Throws<ArgumentException>(() => ShadingCorrectionModule.Apply(samples, Shading(Row(Noon, 0))));
        Assert.Throws<ArgumentException>(() => ShadingCorrectionModule.Apply(samples, Shading(Row(Noon, 0), Row(Noon.AddHours(2), 0))));
        Assert.Throws<ArgumentException>(() => ShadingCorrectionModule.Apply(samples, Shading(Row(Noon, 0), Row(Noon.ToUniversalTime(), 0))));
        Assert.Throws<ArgumentException>(() => ShadingCorrectionModule.Apply([samples[0], samples[0]], Shading(Row(Noon, 0), Row(Noon.AddHours(1), 0))));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidFactorsEvenWithZeroEnergy(double factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadingCorrectionModule.Apply(new(Noon, 0, 0), factor, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadingCorrectionModule.Apply(new(Noon, 0, 0), 0, factor));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidIrradiance(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadingCorrectionModule.Apply(new(Noon, value, 0), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadingCorrectionModule.Apply(new(Noon, 0, value), 0, 0));
    }
}
