using System.Globalization;
using System.Text;
using System.Xml;

namespace SolarShade.Irradiance;

/// <summary>Reusable, typed row buffer for large diagnostic sheets. No boxed cells or retained row batches.</summary>
public sealed class ScientificRowWriter
{
    private readonly XmlWriter xml;
    private readonly CancellationToken ct;
    private readonly string[] columns;
    private readonly StringBuilder buffer = new(2048);
    private char[] chars = new char[2048];
    private int column;
    private bool open;
    public int RowNumber { get; private set; } = 1;
    internal ScientificRowWriter(XmlWriter xml, int width, CancellationToken ct)
    {
        this.xml = xml; this.ct = ct;
        columns = Enumerable.Range(1, width).Select(index =>
        { string name = ""; while (index > 0) { index--; name = (char)('A' + index % 26) + name; index /= 26; } return name; }).ToArray();
    }
    public void Begin()
    {
        ct.ThrowIfCancellationRequested();
        if (open) throw new InvalidOperationException("Previous row is incomplete.");
        if (++RowNumber > 1048576) throw new ArgumentException("Too many rows for a worksheet.");
        open = true; column = 0; buffer.Clear(); buffer.Append("<row r=\""); Integer(RowNumber); buffer.Append("\">");
    }
    private void Cell(bool text)
    {
        if (!open || column >= columns.Length) throw new ArgumentException("Workbook row width differs from headers.");
        buffer.Append("<c r=\"").Append(columns[column++]); Integer(RowNumber);
        buffer.Append(text ? "\" t=\"inlineStr\"><is><t>" : "\"><v>");
    }
    private void Integer(long value)
    { Span<char> text = stackalloc char[24]; value.TryFormat(text, out int size, provider: CultureInfo.InvariantCulture); buffer.Append(text[..size]); }
    public void Number(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Nonfinite workbook value.");
        Cell(false); Span<char> text = stackalloc char[32];
        value.TryFormat(text, out int size, provider: CultureInfo.InvariantCulture); buffer.Append(text[..size]).Append("</v></c>");
    }
    public void Text(string? value)
    {
        if (value == null)
        { if (!open || ++column > columns.Length) throw new ArgumentException("Workbook row width differs from headers."); return; }
        XmlConvert.VerifyXmlChars(value); Cell(true);
        foreach (char c in value.Replace("\r\n", "\n").Replace('\r', '\n'))
        { if (c == '&') buffer.Append("&amp;"); else if (c == '<') buffer.Append("&lt;"); else if (c == '>') buffer.Append("&gt;"); else buffer.Append(c); }
        buffer.Append("</t></is></c>");
    }
    public void Instant(DateTimeOffset value)
    {
        Cell(true); Span<char> text = stackalloc char[40];
        value.TryFormat(text, out int size, "O", CultureInfo.InvariantCulture); buffer.Append(text[..size]).Append("</t></is></c>");
    }
    public void End()
    {
        if (!open || column != columns.Length) throw new ArgumentException("Workbook row width differs from headers.");
        buffer.Append("</row>");
        if (chars.Length < buffer.Length) chars = new char[buffer.Length];
        buffer.CopyTo(0, chars, 0, buffer.Length); xml.WriteRaw(chars, 0, buffer.Length); open = false;
    }
    internal void Complete() { if (open) throw new InvalidOperationException("Final row is incomplete."); }
}
