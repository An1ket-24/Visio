using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace VisioGenerator;

/// <summary>
/// Writes a .vsdx by taking a Visio-authored package (<c>templates/blank.vsdx</c>) and
/// replacing only the page content. The container — document part, style sheets, window
/// state, masters, relationships, docProps — is copied byte-for-byte, because Visio
/// validates those against its own schema and rejects anything it did not write.
///
/// Needs no Visio installation at run time.
/// </summary>
public static class VsdxWriter
{
    const string NsMain = "http://schemas.microsoft.com/office/visio/2012/main";
    const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string F(double v) => v.ToString("0.####", Inv);
    static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static void Write(string path, Diagram d)
    {
        string template = TemplatePath();

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        using var source = ZipFile.OpenRead(template);
        using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);

        bool replacedPage = false, patchedPages = false;

        foreach (var entry in source.Entries)
        {
            switch (entry.FullName)
            {
                case "visio/pages/page1.xml":
                    AddText(zip, entry, PageXml(d));
                    replacedPage = true;
                    break;

                case "visio/pages/pages.xml":
                    AddText(zip, entry, PatchPages(ReadText(entry), d));
                    patchedPages = true;
                    break;

                default:
                    AddBinary(zip, entry);
                    break;
            }
        }

        if (!replacedPage || !patchedPages)
            throw new InvalidDataException(
                $"Template '{template}' is missing visio/pages/page1.xml or visio/pages/pages.xml.");
    }

    static string TemplatePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "templates", "blank.vsdx"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "templates", "blank.vsdx"),
        };

        foreach (var candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        throw new FileNotFoundException(
            "VSDX template not found. Expected 'templates/blank.vsdx' next to the executable.",
            candidates[0]);
    }

    // ---------------------------------------------------------------- package

    static void AddText(ZipArchive zip, ZipArchiveEntry source, string content)
    {
        var entry = zip.CreateEntry(source.FullName, CompressionLevel.Optimal);
        // Keep the template's timestamp so the same spec always produces the same bytes.
        entry.LastWriteTime = source.LastWriteTime;
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    static void AddBinary(ZipArchive zip, ZipArchiveEntry source)
    {
        var entry = zip.CreateEntry(source.FullName, CompressionLevel.Optimal);
        entry.LastWriteTime = source.LastWriteTime;
        using var input = source.Open();
        using var stream = entry.Open();
        input.CopyTo(stream);
    }

    static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    static string PatchPages(string xml, Diagram d)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XNamespace m = NsMain;

        var page = doc.Descendants(m + "Page").First();
        page.SetAttributeValue("ViewScale", "1");
        page.SetAttributeValue("ViewCenterX", F(d.Width / 2));
        page.SetAttributeValue("ViewCenterY", F(d.Height / 2));

        var sheet = page.Element(m + "PageSheet");
        if (sheet is not null)
        {
            void Set(string name, string value)
            {
                var cell = sheet.Elements(m + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == name);
                if (cell is not null)
                {
                    cell.SetAttributeValue("V", value);
                    return;
                }

                var newCell = new XElement(m + "Cell",
                    new XAttribute("N", name), new XAttribute("V", value));

                var lastCell = sheet.Elements(m + "Cell").LastOrDefault();
                if (lastCell is not null) lastCell.AddAfterSelf(newCell);
                else sheet.AddFirst(newCell);
            }

            Set("PageWidth", F(d.Width));
            Set("PageHeight", F(d.Height));
            Set("PrintPageOrientation", d.Height >= d.Width ? "1" : "2");
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
             + doc.ToString(SaveOptions.DisableFormatting);
    }

    // ------------------------------------------------------------------ page

    static string PageXml(Diagram d)
    {
        var sb = new StringBuilder();
        sb.Append($"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<PageContents xmlns="{NsMain}" xmlns:r="{NsR}" xml:space="preserve">
<Shapes>
""");

        int id = 1;
        foreach (var shape in d.Shapes)
            sb.AppendLine(ShapeXml(id++, shape));

        sb.Append("</Shapes>\n</PageContents>");
        return sb.ToString();
    }

    static string ShapeXml(int id, DShape s)
    {
        bool isLine = s.Kind == "line";
        double x = s.X, y = s.Y, w = Math.Max(s.W, 0.02), h = Math.Max(s.H, 0.02);

        if (isLine && s.Points.Count > 1)
        {
            x = s.Points.Min(p => p.X);
            y = s.Points.Min(p => p.Y);
            w = Math.Max(s.Points.Max(p => p.X) - x, 0.02);
            h = Math.Max(s.Points.Max(p => p.Y) - y, 0.02);
        }

        var sb = new StringBuilder();
        sb.Append($"""<Shape ID="{id}" Type="Shape" LineStyle="0" FillStyle="0" TextStyle="0" NameU="Shape.{id}" Name="Shape.{id}">""");

        void Cell(string n, string v) => sb.Append($"""<Cell N="{n}" V="{v}"/>""");

        Cell("PinX", F(x));
        Cell("PinY", F(y));
        Cell("Width", F(w));
        Cell("Height", F(h));
        Cell("LocPinX", "0");
        Cell("LocPinY", "0");
        Cell("Angle", "0");
        Cell("FlipX", "0");
        Cell("FlipY", "0");
        Cell("ResizeMode", "0");
        Cell("ObjType", "1");

        bool hasFill = s.Fill is not null && !isLine;
        Cell("FillForegnd", s.Fill ?? "#FFFFFF");
        Cell("FillBkgnd", "#FFFFFF");
        Cell("FillPattern", hasFill ? "1" : "0");

        Cell("LineWeight", F(s.StrokeWeight / 72.0));
        Cell("LineColor", s.Stroke ?? "#000000");
        Cell("LinePattern", s.Stroke is null ? "0" : (s.Dashed ? "2" : "1"));
        Cell("LineCap", "0");
        Cell("Rounding", s.Round && s.Kind == "rect" ? "0.06" : "0");
        Cell("ShdwPattern", "0");

        double tw = Math.Max(w - 0.06, 0.02);
        double th = Math.Max(h - 0.02, 0.02);
        Cell("TxtPinX", F(w / 2));
        Cell("TxtPinY", F(h / 2));
        Cell("TxtWidth", F(tw));
        Cell("TxtHeight", F(th));
        Cell("TxtLocPinX", F(tw / 2));
        Cell("TxtLocPinY", F(th / 2));
        Cell("TxtAngle", "0");
        Cell("VerticalAlign", "1");
        Cell("LeftMargin", "0.02");
        Cell("RightMargin", "0.02");
        Cell("TopMargin", "0.01");
        Cell("BottomMargin", "0.01");

        if (isLine && s.Points.Count > 1)
        {
            sb.Append("""<Section N="Geometry" IX="0"><Cell N="NoFill" V="1"/><Cell N="NoLine" V="0"/><Cell N="NoShow" V="0"/><Cell N="NoSnap" V="0"/><Cell N="NoQuickDrag" V="0"/>""");
            for (int i = 0; i < s.Points.Count; i++)
            {
                var p = s.Points[i];
                double nx = Math.Clamp((p.X - x) / w, 0, 1);
                double ny = Math.Clamp((p.Y - y) / h, 0, 1);
                sb.Append($"""<Row T="{(i == 0 ? "RelMoveTo" : "RelLineTo")}" IX="{i + 1}"><Cell N="X" V="{F(nx)}"/><Cell N="Y" V="{F(ny)}"/></Row>""");
            }
            sb.Append("</Section>");
        }
        else if (s.Kind == "rect")
        {
            sb.Append("""<Section N="Geometry" IX="0"><Cell N="NoFill" V="0"/><Cell N="NoLine" V="0"/><Cell N="NoShow" V="0"/><Cell N="NoSnap" V="0"/><Cell N="NoQuickDrag" V="0"/>""");
            sb.Append("""<Row T="RelMoveTo" IX="1"><Cell N="X" V="0"/><Cell N="Y" V="0"/></Row><Row T="RelLineTo" IX="2"><Cell N="X" V="1"/><Cell N="Y" V="0"/></Row><Row T="RelLineTo" IX="3"><Cell N="X" V="1"/><Cell N="Y" V="1"/></Row><Row T="RelLineTo" IX="4"><Cell N="X" V="0"/><Cell N="Y" V="1"/></Row><Row T="RelLineTo" IX="5"><Cell N="X" V="0"/><Cell N="Y" V="0"/></Row>""");
            sb.Append("</Section>");
        }

        sb.Append($"""<Section N="Character"><Row IX="0"><Cell N="Font" V="Calibri"/><Cell N="Color" V="{s.TextColor}"/><Cell N="Size" V="{F(s.FontSize / 72.0)}"/><Cell N="Style" V="{(s.Bold ? 1 : 0)}"/></Row></Section>""");
        sb.Append($"""<Section N="Paragraph"><Row IX="0"><Cell N="HorzAlign" V="{s.Align}"/><Cell N="SpLine" V="0"/></Row></Section>""");

        if (!string.IsNullOrEmpty(s.Text))
            sb.Append("<Text>").Append(Esc(s.Text)).Append("</Text>");

        sb.Append("</Shape>");
        return sb.ToString();
    }
}
