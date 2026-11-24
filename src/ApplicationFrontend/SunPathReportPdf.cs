using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace SolarShade.Desktop;

/// <summary>Visual review of the saved mask and solar geometry; no projection is recomputed.</summary>
internal static class SunPathReportPdf
{
    internal static void Write(string directory, DebugDataRun.Manifest snapshot)
    {
        string maskPath = Path.Combine(directory, "02-sky-mask", "sky-mask.png");
        string pathPath = Path.Combine(directory, "04-solar-positions", "sun-path-overlay.png");
        string cardinalPath = Path.Combine(directory, "02-orientation", "cardinal-directions-overlay.png");

        using var mask = XImage.FromFile(maskPath);
        using var sunPath = XImage.FromFile(pathPath);
        using var cardinals = XImage.FromFile(cardinalPath);
        if (mask.PixelWidth != sunPath.PixelWidth || mask.PixelHeight != sunPath.PixelHeight ||
            mask.PixelWidth != cardinals.PixelWidth || mask.PixelHeight != cardinals.PixelHeight)
            throw new InvalidDataException("The mask and solar/orientation overlays have different image dimensions.");

        using var document = new PdfDocument();
        document.Info.Title = "SolarShade - sky mask and sun-path review";
        document.Info.Subject = "Result " + snapshot.Id;
        document.Info.Creator = "SolarShade " + typeof(SunPathReportPdf).Assembly.GetName().Version;
        var page = document.AddPage(); page.Size = PdfSharp.PageSize.A4;
        using var g = XGraphics.FromPdfPage(page);
        var ink = new XSolidBrush(XColor.FromArgb(25, 48, 58));
        var muted = new XSolidBrush(XColor.FromArgb(75, 93, 99));
        var title = new XFont("Arial", 16, XFontStyleEx.Bold);
        var small = new XFont("Arial", 8);
        g.DrawString("Sky mask and sun path", title, ink, new XRect(38, 30, 520, 25), XStringFormats.TopLeft);
        var s = snapshot.Settings;
        var angles = snapshot.EquipmentAngles ?? throw new InvalidDataException("Saved equipment angles are invalid.");
        var period = snapshot.StudyPeriod ?? StudyPeriod.Calendar(s);
        g.DrawString($"{s.Latitude:0.######}° N, {s.Longitude:0.######}° E  |  {period.Describe(TimeZoneSelection.Resolve(s.Zone))}  |  {s.Zone}",
            small, muted, new XRect(38, 58, 520, 14), XStringFormats.TopLeft);
        g.DrawString(angles.DescribePhone(),
            small, muted, new XRect(38, 73, 520, 14), XStringFormats.TopLeft);
        g.DrawString(angles.DescribeImage(),
            small, muted, new XRect(38, 86, 520, 14), XStringFormats.TopLeft);

        // All three PNGs were exported at the original image size. Draw them in one rectangle
        // to preserve the application's exact projection and the transparent overlay pixels.
        const double top = 106, maxWidth = 519, maxHeight = 658;
        double scale = Math.Min(maxWidth / mask.PixelWidth, maxHeight / mask.PixelHeight);
        double width = mask.PixelWidth * scale, height = mask.PixelHeight * scale;
        double left = (page.Width.Point - width) / 2;
        g.DrawImage(mask, left, top, width, height);
        g.DrawImage(sunPath, left, top, width, height);
        g.DrawImage(cardinals, left, top, width, height);

        g.DrawString("White = visible sky     Black = obstruction     Yellow = modeled sun path     N/E/S/W = true compass directions",
            small, ink, new XRect(38, 773, 520, 14), XStringFormats.TopLeft);
        g.DrawString("On the sky grid, the cross marks the fitted lens center. Each ring adds 30° from the lens axis.",
            small, muted, new XRect(38, 788, 520, 14), XStringFormats.TopLeft);
        g.DrawString("SolarShade / " + snapshot.Id, small, muted, new XRect(38, 809, 520, 14), XStringFormats.TopLeft);
        document.Save(Path.Combine(directory, "SunPathReport.pdf"));
    }
}
