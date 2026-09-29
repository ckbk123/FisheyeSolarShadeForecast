using System.Security.Cryptography;
using System.Text.Json;

namespace SolarShade.PvBattery;

public static class BatterySimulator
{
    public const string ModelVersion = "1.0";
    /// <summary>Computes the complete supplied period. Invalid data and cancellation throw; no partial success is returned.</summary>
    public static BatterySimulationResult Compute(BatterySimulationRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Settings);
        var settings = request.Settings;
        ValidateSettings(settings);
        var zone = ValidateSeries(request.Irradiance, ct);
        var intervals = request.Irradiance.Intervals;
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { ModelVersion, request })));
        double energy = settings.BatteryCapacityWh * settings.InitialSoc;
        double initial = energy, minimum = energy;
        var steps = new List<BatteryStep>();
        var hours = new List<BatteryHour>();
        var cursor = intervals[0].Start.ToUniversalTime();
        var finish = intervals[^1].End.ToUniversalTime();
        var hourStart = cursor;
        var hourEnd = NextReportingBoundary(cursor, finish, zone);
        double hourInitial = energy, pv = 0, load = 0, served = 0, unmet = 0, curtailed = 0;
        DateTimeOffset? firstShortfall = null;

        foreach (var interval in intervals)
        {
            ct.ThrowIfCancellationRequested();
            double power = Finite(interval.MeanWm2 * settings.PanelAreaM2 * settings.PanelEfficiency * settings.ConversionEfficiency);
            while (cursor < interval.End)
            {
                ct.ThrowIfCancellationRequested();
                var end = interval.End < hourEnd ? interval.End.ToUniversalTime() : hourEnd;
                var localStart = TimeZoneInfo.ConvertTime(cursor, zone);
                var localEnd = TimeZoneInfo.ConvertTime(end, zone);
                double dt = (end - cursor).TotalHours;
                double requested = Finite(settings.HourlyLoadWh[localStart.Hour] * dt);
                double generated = Finite(power * dt);
                double previous = energy, shortfall = 0, spill = 0;
                // Branch on the net flow to avoid adding a large PV quantity to an already full battery.
                if (generated >= requested)
                {
                    double surplus = generated - requested;
                    double accepted = Math.Min(surplus, settings.BatteryCapacityWh - energy);
                    energy = Math.Min(settings.BatteryCapacityWh, energy + accepted);
                    spill = surplus - accepted;
                }
                else
                {
                    double deficit = requested - generated;
                    double delivered = Math.Min(deficit, energy);
                    energy -= delivered;
                    shortfall = deficit - delivered;
                }
                double supplied = requested - shortfall;
                minimum = Math.Min(minimum, energy);
                if (shortfall > 0) firstShortfall ??= localStart;
                steps.Add(new(interval.Id, localStart, localEnd, interval.MeanWm2, settings.HourlyLoadWh[localStart.Hour],
                    generated, requested, supplied, shortfall, spill, previous, energy, energy / settings.BatteryCapacityWh * 100));
                pv = Finite(pv + generated); load = Finite(load + requested); served = Finite(served + supplied);
                unmet = Finite(unmet + shortfall); curtailed = Finite(curtailed + spill);
                cursor = end;
                if (cursor == hourEnd)
                {
                    var localHourStart = TimeZoneInfo.ConvertTime(hourStart, zone);
                    bool partial = hourEnd - hourStart != TimeSpan.FromHours(1) ||
                        localHourStart.TimeOfDay.Ticks % TimeSpan.TicksPerHour != 0 || localEnd.TimeOfDay.Ticks % TimeSpan.TicksPerHour != 0;
                    hours.Add(new(localHourStart, localEnd, partial, hourInitial, energy,
                        energy / settings.BatteryCapacityWh * 100, pv, load, served, unmet, curtailed));
                    if (cursor < finish)
                    {
                        hourStart = cursor;
                        hourEnd = NextReportingBoundary(cursor, finish, zone);
                        hourInitial = energy; pv = load = served = unmet = curtailed = 0;
                    }
                }
            }
        }
        var summary = new BatterySummary(initial, energy, minimum / settings.BatteryCapacityWh * 100,
            Finite(hours.Sum(h => h.PvWh)), Finite(hours.Sum(h => h.LoadWh)), Finite(hours.Sum(h => h.ServedLoadWh)),
            Finite(hours.Sum(h => h.UnmetLoadWh)), Finite(hours.Sum(h => h.CurtailedWh)),
            hours.Count(h => h.UnmetLoadWh > 0), firstShortfall);
        ct.ThrowIfCancellationRequested();
        return new(fingerprint, request, steps, hours, summary);
    }

    public static void ValidateSettings(BatterySimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!double.IsFinite(settings.PanelAreaM2) || settings.PanelAreaM2 < 0 ||
            !double.IsFinite(settings.BatteryCapacityWh) || settings.BatteryCapacityWh <= 0 ||
            !Fraction(settings.PanelEfficiency, false) || !Fraction(settings.ConversionEfficiency, false) || !Fraction(settings.InitialSoc, true))
            throw new ArgumentException("Area must be finite and nonnegative, capacity positive, efficiencies in (0,1], and initial SoC in [0,1].");
        if (settings.HourlyLoadWh.Count != 24 || settings.HourlyLoadWh.Any(v => !double.IsFinite(v) || v < 0))
            throw new ArgumentException("Exactly 24 finite nonnegative hourly load values (Wh per nominal hour) are required.");
    }

    /// <summary>Validates interval means and gap-free selected bounds. Never fills gaps or substitutes unshaded data.</summary>
    public static TimeZoneInfo ValidateSeries(ShadedIrradianceSeries series, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(series);
        if (string.IsNullOrWhiteSpace(series.TimeZoneId)) throw new ArgumentException("An explicit study time zone is required.");
        var zone = ResolveTimeZone(series.TimeZoneId);
        if (series.Intervals.Count == 0) throw new InvalidDataException("No shaded irradiance intervals supplied.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        ShadedIrradianceInterval? previous = null;
        foreach (var row in series.Intervals)
        {
            ct.ThrowIfCancellationRequested();
            if (row is null || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id))
                throw new InvalidDataException("Interval IDs must be nonempty and unique.");
            if (!double.IsFinite(row.MeanWm2) || row.MeanWm2 < 0 || row.End <= row.Start)
                throw new InvalidDataException($"Invalid irradiance or bounds for interval '{row.Id}'.");
            if (previous is not null && row.Start != previous.End)
                throw new InvalidDataException($"Irradiance must be ordered and contiguous: gap, overlap or duplicate at '{row.Id}'.");
            if (row.SourceStart.HasValue != row.SourceEnd.HasValue ||
                row.SourceStart is { } start && row.SourceEnd is { } end && (start > row.Start || end < row.End || end <= start))
                throw new InvalidDataException($"Invalid source bounds for interval '{row.Id}'.");
            // Avoid timezone clamping at DateTime's extreme years.
            if (row.Start.UtcDateTime.Year < 2 || row.End.UtcDateTime.Year > 9998)
                throw new InvalidDataException("Study instants must be within UTC years 2 through 9998.");
            previous = row;
        }
        return zone;
    }

    /// <summary>System zone or explicit fixed offset used by the desktop study selector.</summary>
    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(id, @"^Fixed/UTC([+-])(\d{2}):(\d{2})$");
        if (!match.Success) return TimeZoneInfo.FindSystemTimeZoneById(id);
        int hours = int.Parse(match.Groups[2].Value), minutes = int.Parse(match.Groups[3].Value);
        if (minutes > 59 || hours * 60 + minutes > 840) throw new ArgumentException("Fixed UTC offset must be within 14 hours.");
        var offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (match.Groups[1].Value == "-" ? -1 : 1));
        return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }

    private static bool Fraction(double value, bool allowZero) => double.IsFinite(value) && value <= 1 && (allowZero ? value >= 0 : value > 0);
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new ArithmeticException("Simulation energy/power exceeds the finite numeric range.");

    // System timezone offset transitions can also split a civil hour (e.g. Lord Howe's 30-minute DST).
    // Find a transition precisely rather than sampling load at a provider's possibly end-labelled timestamp.
    private static DateTimeOffset NextReportingBoundary(DateTimeOffset start, DateTimeOffset finish, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(start, zone);
        long remainingTicks = TimeSpan.TicksPerHour - local.TimeOfDay.Ticks % TimeSpan.TicksPerHour;
        var candidate = start.AddTicks(Math.Min(remainingTicks, (finish - start).Ticks));
        var offset = zone.GetUtcOffset(start);
        if (zone.GetUtcOffset(candidate) == offset) return candidate;
        long low = start.UtcTicks, high = candidate.UtcTicks;
        while (high - low > 1)
        {
            long middle = low + (high - low) / 2;
            if (zone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == offset) low = middle;
            else high = middle;
        }
        return new DateTimeOffset(high, TimeSpan.Zero);
    }
}
