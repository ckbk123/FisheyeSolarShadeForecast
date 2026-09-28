using System.Globalization;
using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace SolarShade.Desktop;

/// <summary>Presentation of saved library metadata and totals. No scientific values are recomputed here.</summary>
internal static class SummaryPdf
{
    internal static void Write(string directory, DebugDataRun.Manifest snapshot, DateTimeOffset exported)
    {
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "06-shading/panel-results.json")));
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "01-calibration/camera-profile.json")));
        using var mask = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "02-sky-mask/mask-details.json")));
        var r = results.RootElement; var c = profile.RootElement.GetProperty("Calibration"); var m = mask.RootElement.GetProperty("Mask");
        var s = snapshot.Settings;
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Value(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "Not recorded";
        static string Total(JsonElement e, string key) => e.GetProperty(key).ValueKind == JsonValueKind.Null ? "N/A" : e.GetProperty(key).GetDouble().ToString("0.000", CultureInfo.InvariantCulture);
        using var document = new PdfDocument();
        document.Info.Title = "SolarShade - completed dataset summary";
        document.Info.Subject = "Result " + snapshot.Id;
        document.Info.Creator = "SolarShade " + typeof(SummaryPdf).Assembly.GetName().Version;
        var page = document.AddPage(); page.Size = PdfSharp.PageSize.A4;
        using var g = XGraphics.FromPdfPage(page);
        var ink = new XSolidBrush(XColor.FromArgb(25, 48, 58));
        var teal = new XSolidBrush(XColor.FromArgb(15, 125, 111));
        var muted = new XSolidBrush(XColor.FromArgb(75, 93, 99));
        var normal = new XFont("Arial", 9); var bold = new XFont("Arial", 9, XFontStyleEx.Bold);
        var small = new XFont("Arial", 8); var heading = new XFont("Arial", 11, XFontStyleEx.Bold);
        const double left = 38, width = 519;
        double y = 38;
        void Text(string text, XFont font, XBrush brush, double x, double top, double w) =>
            g.DrawString(text, font, brush, new XRect(x, top, w, 18), XStringFormats.TopLeft);
        // Bound every field, including arbitrary filenames and provider metadata, to keep one readable page.
        void Wrapped(string text, double x, double w, int lines, XFont font, XBrush brush)
        {
            text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            for (int line = 0; line < lines && text.Length > 0; line++)
            {
                int end = text.Length;
                while (end > 0 && g.MeasureString(text[..end] + (end < text.Length && line == lines - 1 ? "..." : ""), font).Width > w) end--;
                if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
                if (end == 0) throw new InvalidDataException("Summary text cannot fit its column.");
                if (end < text.Length && line < lines - 1)
                { int space = text.LastIndexOf(' ', end - 1, end); if (space > end / 2) end = space; }
                Text(text[..end] + (end < text.Length && line == lines - 1 ? "..." : ""), font, brush, x, y, w);
                text = text[end..].TrimStart(); y += 12;
            }
        }
        void Row(string label, string value, int lines = 1)
        {
            double top = y; Text(label, bold, ink, left, y, 104);
            Wrapped(value, left + 108, width - 108, lines, normal, ink);
            y = Math.Max(y, top + 12) + 3;
        }
        void Section(string title)
        {
            y += 10; g.DrawLine(new XPen(XColor.FromArgb(217, 228, 229)), left, y, left + width, y);
            y += 7; Text(title, heading, teal, left, y, width); y += 21;
        }
        Text("SOLARSHADE", new XFont("Arial", 23, XFontStyleEx.Bold), ink, left, y, width); y += 32;
        Text("Completed dataset / parameter recap", heading, teal, left, y, width); y += 24;
        Row("Result ID", snapshot.Id);
        Row("Updated / exported", $"{snapshot.LastSuccessfulUpdateUtc:yyyy-MM-dd HH:mm:ss} / {exported:yyyy-MM-dd HH:mm:ss} UTC");
        Row("Application / build", $"v{typeof(SummaryPdf).Assembly.GetName().Version} / {snapshot.Software[..12]} | Update {snapshot.UpdateNumber} | {r.GetProperty("Rows").GetArrayLength()} intervals");

        y += 8;
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(237, 247, 244)), left, y, width, 58);
        string[] labels = ["BEFORE SHADING", "AFTER SHADING", "ENERGY LOSS"];
        string[] totals = [Total(r, "BeforeEnergy") + " kWh/m²", Total(r, "AfterEnergy") + " kWh/m²", Total(r, "LossPercent") + (r.GetProperty("LossPercent").ValueKind == JsonValueKind.Null ? " (zero baseline)" : " %")];
        for (int i = 0; i < 3; i++)
        {
            Text(labels[i], small, muted, left + 12 + i * 173, y + 9, 157);
            Text(totals[i], new XFont("Arial", 13, XFontStyleEx.Bold), ink, left + 12 + i * 173, y + 29, 157);
        }
        y += 61;
        Section("01 / Site and irradiance");
        Row("Location / height", $"{N(s.Latitude)}°, {N(s.Longitude)}°  |  {N(s.Elevation)} m");
        Row("Calendar / zone", $"{s.Start:yyyy-MM-dd} to {s.End:yyyy-MM-dd}  |  {s.Zone} ({(s.UseSystemTimeZone ? "system at update" : "manual")})", 2);
        Row("Data source", Value(r, "Source"), 2);
        Row("Cadence / labels", (r.GetProperty("NativeCadence").ValueKind == JsonValueKind.Null ? "Variable / explicit intervals" : Value(r, "NativeCadence")) + " | " + Value(r, "TimestampConvention"), 2);
        string fetched = snapshot.Inputs.TryGetValue("source-dataset", out var source) ? Value(source, "FetchedUtc") : "Not recorded";
        Row("Retrieved / location", fetched + " | Source coordinates: " + Value(r, "SourceLatitude") + ", " + Value(r, "SourceLongitude"), 2);

        Section("02 / Camera, mask and panel");
        Row("Sky image", Path.GetFileName(s.SkyImage), 2);
        Row("Active profile", Path.GetFileName(s.ProfilePath), 2);
        Row("Effective coverage", Value(c, "MaximumIncidentAngleDegrees") + "° maximum incident angle | " + (s.CoverageAngle > 0 ? "user override" : "saved profile"));
        Row("Camera pose", $"Bottom bearing {N(s.BottomAzimuth)}° | tilt {N(s.CameraTilt)}° | roll {N(s.CameraRoll)}°");
        Row("Mask", $"{s.Model}, {Value(m, "InputSize")} px | {Value(m, "Width")} x {Value(m, "Height")} image | disk: {Value(m, "DiskMethod")}");
        var disk = m.GetProperty("Disk");
        Row("Effective disk", $"Center ({N(disk.GetProperty("CenterX").GetDouble())}, {N(disk.GetProperty("CenterY").GetDouble())}) px | radius {N(disk.GetProperty("Radius").GetDouble())} px");
        Row("Panel / model", $"Tilt {N(s.PanelTilt)}° | azimuth {N(s.PanelAzimuth)}° | {Value(r, "Model")} | {s.Substeps} integration samples");

        Section("03 / Reading this export");
        Wrapped("01 calibration; 02 mask and orientation; 03 horizontal irradiance; 04 solar positions and sun paths; 05 unshaded panel; 06 shading factors and panel results. Workbooks and images are exact copies of the completed debug data.", left, width, 3, small, muted);
        y += 4;
        Wrapped("Totals are panel irradiation, not electrical generation. Bearings use true north. White mask = sky; unobserved sky is blocked; no ground reflection. Source interval metadata takes priority over import fallback settings. Checkerboard setup applies only to a future calibration, not the active profile.", left, width, 3, small, muted);
        y += 4;
        Wrapped("Full saved interface settings, filenames, library versions and fingerprints: run.json. Detailed assumptions and effective calibration: stage JSON files. Export time and integrity hashes: export.json. Long fields in this recap may be shortened.", left, width, 3, small, muted);
        if (y > 794) throw new InvalidDataException("The summary exceeds its single-page layout.");
        g.DrawLine(new XPen(XColor.FromArgb(217, 228, 229)), left, 801, left + width, 801);
        Text("SolarShade / completed update " + snapshot.UpdateNumber, small, muted, left, 808, width);
        Text("1 / 1", small, muted, left + width - 25, 808, 25);
        document.Save(Path.Combine(directory, "Summary.pdf"));
    }
}
