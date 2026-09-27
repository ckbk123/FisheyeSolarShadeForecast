namespace SolarShade.Irradiance;

public sealed record ProviderDescription(IrradianceService Service, string DisplayName,
    string Authentication, string RegistrationUrl, string UsageNotice);

/// <summary>UI-ready provider choices. The library neither stores credentials nor opens a login UI.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderDescription> All { get; } = Array.AsReadOnly(new[]
    {
        new ProviderDescription(IrradianceService.NasaPower, "NASA POWER", "None", "https://power.larc.nasa.gov/", "Free; acknowledge NASA POWER. Hourly from 2001, solar latency varies."),
        new ProviderDescription(IrradianceService.OpenMeteo, "Open-Meteo ERA5", "None", "https://open-meteo.com/en/terms", "Free hosted API for non-commercial use only, attribution required; rate limits apply."),
        new ProviderDescription(IrradianceService.Nsrdb, "NSRDB", "API key + email", "https://developer.nlr.gov/signup/", "Register free. Dataset coverage and years depend on location; historical hourly data only, never TMY."),
        new ProviderDescription(IrradianceService.Cams, "CAMS Radiation (SoDa)", "Registered email", "https://www.soda-pro.com/", "Register free and activate CAMS access. Satellite regional coverage; no parallel requests."),
        new ProviderDescription(IrradianceService.Oikolab, "Oikolab ERA5", "API key", "https://oikolab.com/", "Free monthly quota. Account terms and redistribution restrictions require review for an app offered to other users."),
        new ProviderDescription(IrradianceService.CopernicusCds, "Copernicus CDS ERA5", "Personal access token + accepted dataset licence", "https://cds.climate.copernicus.eu/how-to-api", "Free. Accept dataset licence on its website. Asynchronous job; point-timeseries route downloads full history and filters locally.")
    });
}
