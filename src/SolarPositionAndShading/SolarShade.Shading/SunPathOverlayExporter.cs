using System.Text.Json;
using SolarShade.Irradiance;

namespace SolarShade.Shading;

public static class SunPathOverlayExporter
{
    /// <summary>Exports the exact generated PNG and compact retained vertices. Full selected solar samples remain in the solar-stage workbooks.</summary>
    public static IReadOnlyList<string> Export(SunPathOverlayResult result, string directory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result); ct.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string image = Path.Combine(directory, "sun-path-overlay.png"), workbook = Path.Combine(directory, "sun-path-projection.xlsx"), details = Path.Combine(directory, "sun-path-details.json");
        AtomicWrite(image, result.Png, ct);
        string[] headers = ["Track ID", "Display local date", "Source interval ID", "Selected sample index (zero based)", "Sample timestamp", "Projected x (native pixels)", "Projected y (native pixels)"];
        var sheets = new List<ScientificSheet>();
        const int chunkSize = 100000;
        for (int offset = 0; offset < Math.Max(1, result.Vertices.Count); offset += chunkSize)
            sheets.Add(new(offset == 0 ? "Rendered vertices" : $"Rendered vertices {offset/chunkSize+1}", headers, VertexRows(result, offset, chunkSize)));
        ScientificWorkbook.Write(workbook, sheets,
            $"Rendered vertices selected from existing solar timeline's selected Samples only. Display timezone {result.DisplayTimeZoneId}; native image {result.Width}x{result.Height}. Simplification maximum point-to-segment deviation {result.Options.SimplificationTolerancePixels} pixels; subpixel raster precision 1/256 pixel. Visual yellow line, not the solar disk. No new solar calculations or weather observations. Dataset fingerprint {result.DatasetFingerprint}. {result.GeometryConvention}", ct);
        AtomicWrite(details, JsonSerializer.SerializeToUtf8Bytes(new
        {
            result.Width, result.Height, result.Options, result.Timings, result.SelectedSampleCount, result.ProjectedSampleCount,
            result.NightSampleCount, result.UnprojectedSampleCount, result.TrackCount, RenderedVertexCount = result.Vertices.Count,
            result.DisplayTimeZoneId, result.GeometryConvention, result.DatasetFingerprint,
            Color = "Golden yellow (#FFD200), straight-alpha PNG", SelectedSamplesOnly = true,
            RasterCoordinateQuantizationMaximumPixels = Math.Sqrt(2) / 512,
            Breaks = "Display-local date, source interval gaps, missing selected samples, night, and invalid/outside calibrated projection.",
            Reference = "Source interval ID plus zero-based selected sample index refers to the existing solar timeline and its integration-sample diagnostic exports.",
            LineMeaning = "Display annotation only, not physical solar-disk size.",
            Artifacts = new[] { Path.GetFileName(image), Path.GetFileName(workbook) }
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        return new[] { image, workbook, details };
    }

    private static IEnumerable<object?[]> VertexRows(SunPathOverlayResult result, int offset, int count)
    {
        int end = Math.Min(result.Vertices.Count, offset + count);
        for (int i = offset; i < end; i++)
        {
            var v = result.Vertices[i];
            yield return [v.TrackId, v.LocalDate.ToString("yyyy-MM-dd"), v.IntervalId, v.SampleIndex, v.Timestamp, v.PixelX, v.PixelY];
        }
    }

    private static void AtomicWrite(string path, byte[] bytes, CancellationToken ct)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { ct.ThrowIfCancellationRequested(); File.WriteAllBytes(temporary, bytes); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
