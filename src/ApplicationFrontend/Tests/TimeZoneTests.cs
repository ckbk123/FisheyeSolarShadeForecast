using System.Text.Json;
using SolarShade.Desktop;
using Xunit;

public class TimeZoneTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

    [Fact]
    public void ChoicesIncludePlainOffsetsAndAllRegionsInUtcOrder()
    {
        var choices = TimeZoneSelection.Choices();
        Assert.All(choices.Zip(choices.Skip(1)), p => Assert.True(p.First.BaseUtcOffset <= p.Second.BaseUtcOffset));
        Assert.True(choices.Count(z => z.BaseUtcOffset == TimeSpan.FromHours(7) && !z.Id.StartsWith("Fixed/")) > 1);
        foreach (int hour in Enumerable.Range(-12, 27)) Assert.Contains(choices, z => z.Id == TimeZoneSelection.FixedOffset(TimeSpan.FromHours(hour)).Id);
        Assert.Equal(choices.Length, choices.Select(z => z.Id).Distinct().Count());
    }

    [Theory][InlineData(1)][InlineData(2)][InlineData(5.5)][InlineData(5.75)][InlineData(-3.5)]
    public void FixedOffsetsSurviveSavingAndNeverUseDaylightSaving(double hours)
    {
        var fixedZone = TimeZoneSelection.FixedOffset(TimeSpan.FromHours(hours));
        var settings = new UserSettings { Zone = fixedZone.Id, UseSystemTimeZone = false };
        var saved = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        var resolved = TimeZoneSelection.Resolve(saved.Zone);
        Assert.False(resolved.SupportsDaylightSavingTime);
        foreach (var date in new[] { new DateTime(2023, 3, 26), new DateTime(2023, 7, 2), new DateTime(2023, 10, 29) })
        {
            var start = SolarShade.Irradiance.IrradianceDatasets.Boundary(date, resolved); var end = SolarShade.Irradiance.IrradianceDatasets.Boundary(date.AddDays(1), resolved);
            Assert.Equal(24, (end - start).TotalHours);
            Assert.Equal(TimeSpan.FromHours(hours), resolved.GetUtcOffset(start));
            Assert.Equal(new DateTimeOffset(date, TimeSpan.FromHours(hours)), start);
        }
        Assert.False(TimeZoneSelection.ApplySystemZone(saved, Paris));
    }

    [Fact]
    public void LegacySavedExampleZoneFollowsWindowsOnUpgrade()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("""{"Zone":"SE Asia Standard Time","Latitude":43.5,"Longitude":1.5}""")!;
        Assert.True(settings.UseSystemTimeZone);
        Assert.True(TimeZoneSelection.ApplySystemZone(settings, Paris));
        Assert.Equal(Paris.Id, settings.Zone);
        Assert.Equal(43.5, settings.Latitude);
        Assert.Equal(1.5, settings.Longitude);
        Assert.False(TimeZoneSelection.ApplySystemZone(settings, Paris));
    }

    [Fact]
    public void AutomaticModeFollowsLaterWindowsChangesButManualModeStaysPinned()
    {
        var settings = new UserSettings { Zone = Vietnam.Id };
        TimeZoneSelection.ApplySystemZone(settings, Paris);
        Assert.Equal(Paris.Id, settings.Zone);
        var snapshot = settings with { };
        TimeZoneSelection.ApplySystemZone(settings, Vietnam);
        Assert.Equal(Vietnam.Id, settings.Zone);
        Assert.Equal(Paris.Id, snapshot.Zone); // An in-flight evaluation stays internally consistent.
        settings.UseSystemTimeZone = false;
        Assert.False(TimeZoneSelection.ApplySystemZone(settings, Paris));
        var restored = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        Assert.False(restored.UseSystemTimeZone);
        Assert.False(TimeZoneSelection.ApplySystemZone(restored, Paris));
        Assert.Equal(Vietnam.Id, restored.Zone);
    }

    [Theory][InlineData(3, 2, 1)][InlineData(7, 2, 2)]
    public void ParisHistoricalOffsetsAffectBoundariesImportsAndCaption(int month, int day, int offset)
    {
        var date = new DateTime(2023, month, day);
        var start = SolarShade.Irradiance.IrradianceDatasets.Boundary(date, Paris); var end = SolarShade.Irradiance.IrradianceDatasets.Boundary(date.AddDays(1), Paris);
        Assert.Equal(new DateTimeOffset(date, TimeSpan.FromHours(offset)), start);
        var time = SolarShade.Irradiance.IrradianceDatasetFiles.ParseTime($"{day:00}.{month:00}.2023 12:00", Paris);
        Assert.Equal(12 - offset, time.UtcDateTime.Hour);
        Assert.Contains($"UTC+0{offset}:00", TimeZoneSelection.Describe(Paris, start, end));
        var sameInstant = SolarShade.Irradiance.IrradianceDatasetFiles.ParseTime($"2023-{month:00}-{day:00}T12:00:00+07:00", Paris);
        Assert.Equal(5, sameInstant.UtcDateTime.Hour); // Explicit input offsets must not be reinterpreted.
    }

    [Fact]
    public void CaptionShowsBothOffsetsAcrossParisClockChange()
    {
        var start = SolarShade.Irradiance.IrradianceDatasets.Boundary(new(2023, 3, 26), Paris); var end = SolarShade.Irradiance.IrradianceDatasets.Boundary(new(2023, 3, 27), Paris);
        Assert.Equal(23, (end - start).TotalHours);
        var caption = TimeZoneSelection.Describe(Paris, start, end);
        Assert.Contains("UTC+01:00", caption); Assert.Contains("UTC+02:00", caption);
    }
}
