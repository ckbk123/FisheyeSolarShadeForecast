using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Irradiance;

namespace SolarShade.Irradiance.Validation;

/// <summary>Independent read-back of every exported cell and timestamp, separate from production writer.</summary>
public static class WorkbookValidator
{
    public static void Validate(string path, IReadOnlyList<IrradianceSample> expected, TimeZoneInfo zone)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(x => x.Name.EndsWith(".xml") || x.Name.EndsWith(".rels")))
        { using var content = entry.Open(); XDocument.Load(content); }
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = XDocument.Load(sheet).Descendants(ns + "row").ToArray();
        if (rows.Length != expected.Count + 1) throw new InvalidDataException("Workbook row count differs from download.");
        for (int i = 0; i < rows.Length; i++)
        {
            var cells = rows[i].Elements(ns + "c").ToArray();
            if (cells.Length != 3) throw new InvalidDataException("Expected exactly three columns.");
            if (i == 0) continue;
            var time = DateTimeOffset.ParseExact(cells[0].Descendants(ns + "t").Single().Value,
                "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
            var sample = expected[i - 1];
            if (time != sample.TimestampUtc || time.Offset != zone.GetUtcOffset(sample.TimestampUtc))
                throw new InvalidDataException("Timestamp instant or local UTC offset changed on export.");
            double direct = double.Parse(cells[1].Element(ns + "v")!.Value, CultureInfo.InvariantCulture);
            double diffuse = double.Parse(cells[2].Element(ns + "v")!.Value, CultureInfo.InvariantCulture);
            if (direct != sample.DirectHorizontal || diffuse != sample.DiffuseHorizontal)
                throw new InvalidDataException("Irradiance changed on export.");
        }
    }
}
