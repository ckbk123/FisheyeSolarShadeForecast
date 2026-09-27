namespace SolarShade.Desktop;

public enum UpdateState { NeedsAttention, UpdateRequired, Updating, UpToDate, Stopped, Failed }

/// <summary>Cheap interface validation only; scientific validation remains library-owned.</summary>
public static class UpdateInputs
{
    public static string? Problem(UserSettings s)
    {
        if (!double.IsFinite(s.Latitude) || Math.Abs(s.Latitude) > 90) return "Latitude must be between -90 and 90 degrees.";
        if (!double.IsFinite(s.Longitude) || Math.Abs(s.Longitude) > 180) return "Longitude must be between -180 and 180 degrees.";
        if (!double.IsFinite(s.Elevation)) return "Enter a finite elevation.";
        if (!double.IsFinite(s.PanelTilt) || s.PanelTilt < 0 || s.PanelTilt > 90) return "Panel tilt must be between 0 and 90 degrees.";
        if (!double.IsFinite(s.PanelAzimuth) || s.PanelAzimuth < 0 || s.PanelAzimuth > 360) return "Panel azimuth must be between 0 and 360 degrees.";
        if (!double.IsFinite(s.BottomAzimuth) || !double.IsFinite(s.CameraTilt) || !double.IsFinite(s.CameraRoll)) return "Enter finite camera angles.";
        if (Math.Abs(s.CameraTilt) > 180) return "Camera tilt must be between -180 and 180 degrees.";
        if (!double.IsFinite(s.CoverageAngle) || s.CoverageAngle < 0 || s.CoverageAngle > 180) return "Coverage must be 0 (use profile) or at most 180 degrees.";
        if (s.Start > s.End) return "Start date must not be after end date.";
        if (s.Start.Year < 1900 || s.End.Year > 2100) return "Choose dates in the supported 1900–2100 range.";
        if (!double.IsFinite(s.ImportIntervalMinutes) || s.ImportIntervalMinutes <= 0) return "Import interval must be greater than zero.";
        if (s.Substeps <= 0) return "Choose a positive number of integration samples.";
        try
        {
            _ = TimeZoneSelection.Resolve(s.Zone);
            if (string.IsNullOrWhiteSpace(s.SkyImage) || !File.Exists(PortablePaths.Resolve(s.SkyImage))) return "Choose an existing sky photograph.";
            if (string.IsNullOrWhiteSpace(s.ProfilePath) || !File.Exists(PortablePaths.Resolve(s.ProfilePath))) return "Load a camera profile or calibrate the camera.";
            if (!string.IsNullOrEmpty(s.ImportPath) && !File.Exists(PortablePaths.Resolve(s.ImportPath))) return "The selected irradiance file is missing.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return ex.Message; }
        return null;
    }
}
