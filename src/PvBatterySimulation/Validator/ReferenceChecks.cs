using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolarShade.PvBattery.Validator;

/// <summary>Offline numerical checks; not a physical battery or weather validation.</summary>
public static class ReferenceChecks
{
    public static void Verify(BatterySimulationResult result)
    {
        var settings = result.Input.Settings;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(result.Input.Irradiance.TimeZoneId);
        var sources = result.Input.Irradiance.Intervals.ToDictionary(i => i.Id);
        decimal capacity = (decimal)settings.BatteryCapacityWh;
        decimal stored = capacity * (decimal)settings.InitialSoc, minimum = stored;
        decimal sumPv = 0, sumLoad = 0, sumUnmet = 0, sumCurtailed = 0;
        var cursor = result.Input.Irradiance.Intervals[0].Start;
        int sourceIndex = 0;
        DateTimeOffset? firstShortfall = null;
        // Independent decimal recurrence: compute the unconstrained balance, then clip it.
        foreach (var step in result.Steps)
        {
            if (step.Start != cursor || step.End <= step.Start) Fail("Simulation steps are not contiguous.");
            var source = sources[step.SourceIntervalId];
            if (source.Id != result.Input.Irradiance.Intervals[sourceIndex].Id ||
                step.Start < source.Start || step.End > source.End) Fail("Step is outside its source interval.");
            if (step.End == source.End) sourceIndex++;
            decimal dt = (decimal)(step.End - step.Start).Ticks / TimeSpan.TicksPerHour;
            int hour = TimeZoneInfo.ConvertTime(step.Start, zone).Hour;
            decimal pv = (decimal)source.MeanWm2 * (decimal)settings.PanelAreaM2 *
                (decimal)settings.PanelEfficiency * (decimal)settings.ConversionEfficiency * dt;
            decimal load = (decimal)settings.HourlyLoadWh[hour] * dt;
            decimal balance = stored + pv - load;
            decimal unmet = Math.Max(0m, -balance), curtailed = Math.Max(0m, balance - capacity);
            Near((double)stored, step.StartStoredWh, "step start energy");
            stored = Math.Clamp(balance, 0, capacity);
            Near((double)stored, step.EndStoredWh, "step end energy");
            Near((double)(stored / capacity * 100), step.EndSocPercent, "step SoC");
            Near((double)pv, step.PvWh, "step PV"); Near((double)load, step.LoadWh, "step load");
            Near((double)unmet, step.UnmetLoadWh, "step unmet"); Near((double)curtailed, step.CurtailedWh, "step curtailed");
            Near((double)(load - unmet), step.ServedLoadWh, "step served");
            minimum = Math.Min(minimum, stored);
            sumPv += pv; sumLoad += load; sumUnmet += unmet; sumCurtailed += curtailed;
            if (unmet > 0) firstShortfall ??= step.Start;
            cursor = step.End;
        }
        if (cursor != result.Input.Irradiance.Intervals[^1].End || sourceIndex != sources.Count) Fail("Study coverage is incomplete.");
        var summary = result.Summary;
        Near((double)stored, summary.FinalStoredWh, "final stored");
        Near((double)(minimum / capacity * 100), summary.MinimumSocPercent, "minimum SoC");
        Near((double)sumPv, summary.PvWh, "total PV"); Near((double)sumLoad, summary.LoadWh, "total load");
        Near((double)sumUnmet, summary.UnmetLoadWh, "total unmet"); Near((double)sumCurtailed, summary.CurtailedWh, "total curtailed");
        Near((double)(sumLoad - sumUnmet), summary.ServedLoadWh, "total served");
        Near(summary.InitialStoredWh + summary.PvWh, summary.FinalStoredWh + summary.ServedLoadWh + summary.CurtailedWh, "global energy balance");
        if (firstShortfall != summary.FirstShortfallIntervalStart) Fail("First shortfall does not match.");

        int index = 0, shortfallHours = 0;
        foreach (var hour in result.Hours)
        {
            if (index >= result.Steps.Count || hour.Start != result.Steps[index].Start) Fail("Hourly coverage is inconsistent.");
            var first = result.Steps[index];
            double pv = 0, load = 0, unmet = 0, curtailed = 0;
            do
            {
                var step = result.Steps[index++];
                if (step.End > hour.End) Fail("Step crosses an output boundary.");
                pv += step.PvWh; load += step.LoadWh; unmet += step.UnmetLoadWh; curtailed += step.CurtailedWh;
            } while (index < result.Steps.Count && result.Steps[index].Start < hour.End);
            var last = result.Steps[index - 1];
            if (last.End != hour.End) Fail("Hourly end does not match step coverage.");
            Near(first.StartStoredWh, hour.StartStoredWh, "hour start"); Near(last.EndStoredWh, hour.EndStoredWh, "hour end");
            Near(last.EndSocPercent, hour.EndSocPercent, "hour SoC"); Near(pv, hour.PvWh, "hour PV");
            Near(load, hour.LoadWh, "hour load"); Near(unmet, hour.UnmetLoadWh, "hour unmet");
            Near(curtailed, hour.CurtailedWh, "hour curtailed"); Near(load - unmet, hour.ServedLoadWh, "hour served");
            if (unmet > 0) shortfallHours++;
        }
        if (index != result.Steps.Count || shortfallHours != summary.HoursContainingShortfall) Fail("Hourly totals are incomplete.");
    }

    public static void VerifyFixture(BatterySimulationResult result, string expectedPath)
    {
        using var stream = File.OpenRead(expectedPath);
        var expected = JsonSerializer.Deserialize<ExpectedFixture>(stream) ?? throw new InvalidDataException("Null expected fixture.");
        if (result.Hours.Count != expected.HourlyEndStoredWh.Length) Fail("Frozen fixture hour count differs.");
        for (int i = 0; i < result.Hours.Count; i++)
            Near(expected.HourlyEndStoredWh[i], result.Hours[i].EndStoredWh, $"frozen fixture hour {i}");
        Near(expected.PvWh, result.Summary.PvWh, "fixture PV");
        Near(expected.LoadWh, result.Summary.LoadWh, "fixture load");
        Near(expected.UnmetLoadWh, result.Summary.UnmetLoadWh, "fixture unmet");
        Near(expected.CurtailedWh, result.Summary.CurtailedWh, "fixture curtailed");
        Near(expected.MinimumSocPercent, result.Summary.MinimumSocPercent, "fixture minimum SoC");
    }

    private static void Near(double expected, double actual, string label)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-7 + Math.Abs(expected) * 1e-9)
            Fail($"{label}: expected {expected:R}, got {actual:R}.");
    }
    private static void Fail(string message) => throw new InvalidDataException(message);

    private sealed record ExpectedFixture(
        [property: JsonRequired] double[] HourlyEndStoredWh, [property: JsonRequired] double PvWh,
        [property: JsonRequired] double LoadWh, [property: JsonRequired] double UnmetLoadWh,
        [property: JsonRequired] double CurtailedWh, [property: JsonRequired] double MinimumSocPercent);
}
