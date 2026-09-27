namespace SolarShade.Desktop;

public static class TimeZoneSelection
{
    private const string FixedPrefix = "Fixed/UTC";

    public static TimeZoneInfo FixedOffset(TimeSpan offset)
    {
        if (offset.TotalMinutes % 1 != 0 || Math.Abs(offset.TotalHours) > 14) throw new ArgumentOutOfRangeException(nameof(offset));
        string suffix = DateTimeOffset.UnixEpoch.ToOffset(offset).ToString("zzz", System.Globalization.CultureInfo.InvariantCulture);
        string name = $"UTC{suffix} · fixed, no daylight saving";
        return TimeZoneInfo.CreateCustomTimeZone(FixedPrefix + suffix, offset, name, name);
    }

    public static TimeZoneInfo Resolve(string id)
    {
        if (!id.StartsWith(FixedPrefix, StringComparison.Ordinal)) return TimeZoneInfo.FindSystemTimeZoneById(id);
        var match = System.Text.RegularExpressions.Regex.Match(id, @"^Fixed/UTC([+-])(\d{2}):(\d{2})$");
        if (!match.Success) throw new TimeZoneNotFoundException("Invalid fixed UTC offset: " + id);
        int hours = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (minutes > 59 || hours * 60 + minutes > 840) throw new TimeZoneNotFoundException("Invalid fixed UTC offset: " + id);
        return FixedOffset(TimeSpan.FromMinutes((hours * 60 + minutes) * (match.Groups[1].Value == "-" ? -1 : 1)));
    }

    public static TimeZoneInfo[] Choices()
    {
        var regions = TimeZoneInfo.GetSystemTimeZones();
        var offsets = Enumerable.Range(-12, 27).Select(h => TimeSpan.FromHours(h)).Concat(regions.Select(z => z.BaseUtcOffset)).Distinct();
        return regions.Concat(offsets.Select(FixedOffset)).OrderBy(z => z.BaseUtcOffset)
            .ThenBy(z => z.Id.StartsWith(FixedPrefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(z => z.DisplayName, StringComparer.CurrentCulture).ToArray();
    }

    public static TimeZoneInfo CurrentSystemZone()
    {
        // TimeZoneInfo.Local otherwise retains the zone cached before a Windows setting change.
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    }

    public static bool ApplySystemZone(UserSettings settings, TimeZoneInfo systemZone)
    {
        if (!settings.UseSystemTimeZone || settings.Zone == systemZone.Id) return false;
        settings.Zone = systemZone.Id;
        return true;
    }

    public static string Describe(TimeZoneInfo zone, DateTimeOffset start, DateTimeOffset end)
    {
        if (zone.Id.StartsWith(FixedPrefix, StringComparison.Ordinal)) return zone.DisplayName;
        string name = zone.DisplayName;
        if (name.StartsWith("(UTC", StringComparison.Ordinal) && name.IndexOf(')') is var closing && closing >= 0)
            name = name[(closing + 1)..].Trim();
        var offsets = new HashSet<TimeSpan> { zone.GetUtcOffset(start), zone.GetUtcOffset(end > start ? end.AddTicks(-1) : start) };
        if (zone.SupportsDaylightSavingTime)
            for (var time = start.AddDays(1); time < end; time = time.AddDays(1)) offsets.Add(zone.GetUtcOffset(time));
        return name + " · " + string.Join(" / ", offsets.Order().Select(offset => "UTC" +
            DateTimeOffset.UnixEpoch.ToOffset(offset).ToString("zzz", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
