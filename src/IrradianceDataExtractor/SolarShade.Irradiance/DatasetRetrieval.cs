namespace SolarShade.Irradiance;

public sealed partial class IrradianceClient
{
    /// <summary>Fetches source-native rows with boundary padding for start/end/center labels; does not alter their cadence.</summary>
    public async Task<IrradianceDataset> FetchDatasetAsync(DateOnly startDate, DateOnly endInclusiveDate,
        double longitude, double latitude, IrradianceService service, CancellationToken ct = default)
    {
        if (endInclusiveDate < startDate) throw new ArgumentException("Start date must not follow end date.");
        var request = new IrradianceRequest(startDate.AddDays(-1), endInclusiveDate.AddDays(2), longitude, latitude, service);
        var result = await FetchAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return IrradianceDatasets.FromResult(result, service) with { Latitude = latitude, Longitude = longitude };
    }
}
