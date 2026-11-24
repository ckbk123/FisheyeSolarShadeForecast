using SolarShade.Irradiance;

namespace SolarShade.Desktop;

/// <summary>Elapsed study bounds, including Start and excluding End.</summary>
public readonly record struct StudyPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    internal static StudyPeriod FromSource(UserSettings settings, IrradianceDataset source)
    {
        if (WeatherSourceSelection.UsesServices(settings)) return Calendar(settings);
        if (source.Intervals.Count == 0) throw new InvalidDataException("No irradiance intervals supplied.");
        return new(source.Intervals[0].Start, source.Intervals[^1].End);
    }

    internal static StudyPeriod Calendar(UserSettings settings)
    {
        var zone = TimeZoneSelection.Resolve(settings.Zone);
        return new(IrradianceDatasets.Boundary(settings.Start, zone),
            IrradianceDatasets.Boundary(settings.End.Date.AddDays(1), zone));
    }

    internal (DateTime First, DateTime Last) LocalDates(TimeZoneInfo zone) =>
        (TimeZoneInfo.ConvertTime(Start, zone).Date, TimeZoneInfo.ConvertTime(End.AddTicks(-1), zone).Date);

    internal string Describe(TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(Start, zone);
        var end = TimeZoneInfo.ConvertTime(End, zone);
        if (start.TimeOfDay != TimeSpan.Zero || end.TimeOfDay != TimeSpan.Zero)
            return TimeZoneSelection.IntervalLabel(start, end);
        var (first, last) = LocalDates(zone);
        return first == last ? $"{first:dd.MM.yyyy}" : $"{first:dd.MM.yyyy} – {last:dd.MM.yyyy}";
    }
}
