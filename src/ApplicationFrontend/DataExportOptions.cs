namespace SolarShade.Desktop;

[Flags]
public enum IrradianceFields
{
    None = 0, PanelDirect = 1, PanelDiffuse = 2, DirectFactor = 4, DiffuseFactor = 8, ShadedTotal = 16,
    GlobalHorizontal = 32, DirectHorizontal = 64, DiffuseHorizontal = 128,
    Panel = PanelDirect | PanelDiffuse | DirectFactor | DiffuseFactor | ShadedTotal,
    Weather = GlobalHorizontal | DirectHorizontal | DiffuseHorizontal
}

/// <summary>Timeseries is included with every selection. The default is the original six-column workbook.</summary>
public sealed record DataExportOptions(bool Irradiance = true, IrradianceFields Fields = IrradianceFields.Panel, bool SourceIntervalBounds = false)
{
    internal bool IncludesWeather => (Fields & IrradianceFields.Weather) != 0;

    internal void Validate()
    {
        if (!Irradiance || Fields == IrradianceFields.None || (Fields & ~(IrradianceFields.Panel | IrradianceFields.Weather)) != 0)
            throw new ArgumentException("Select at least one valid data column to export.");
    }
}

internal sealed record IrradianceExportColumn(IrradianceFields Field, string Header, string Help, double Width)
{
    internal static IrradianceExportColumn[] ForModel(string model) =>
    [
        new(IrradianceFields.PanelDirect, "Direct (W/m²)", "Unshaded direct irradiance on the panel.", 24),
        new(IrradianceFields.PanelDiffuse, $"Diffuse {model} (W/m²)", "Unshaded sky diffuse irradiance on the panel.", 30),
        new(IrradianceFields.DirectFactor, "Shading factor direct", "Shaded divided by unshaded direct irradiance. Blank when the denominator is zero.", 24),
        new(IrradianceFields.DiffuseFactor, "Shading factor diffuse", "Shaded divided by unshaded diffuse irradiance. Blank when the denominator is zero.", 24),
        new(IrradianceFields.ShadedTotal, "Final total (W/m²)", "Shaded direct and diffuse irradiance on the panel.", 24),
        new(IrradianceFields.GlobalHorizontal, "Source GHI (W/m²)", "Global horizontal irradiance as supplied for the source interval. Missing source GHI stays blank.", 24),
        new(IrradianceFields.DirectHorizontal, "Direct horizontal (W/m²)", "Direct irradiance on a horizontal surface from the saved weather input.", 28),
        new(IrradianceFields.DiffuseHorizontal, "Diffuse horizontal (W/m²)", "Diffuse irradiance on a horizontal surface from the saved weather input.", 28)
    ];
}
