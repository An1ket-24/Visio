using System.IO.Compression;
using System.Xml.Linq;

namespace VisioGenerator;

/// <summary>
/// Post-write gate: reopens the produced file from disk and checks it is a sound OPC
/// package. Catches the things that make Visio say "the file is corrupt" — missing or
/// dangling parts, malformed XML, broken relationship targets, duplicate shape IDs,
/// NaN/blank geometry.
/// </summary>
public static class VsdxValidator
{
    static readonly XNamespace Main = "http://schemas.microsoft.com/office/visio/2012/main";
    static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>A plain number with a comma decimal separator, e.g. <c>1,5</c> or <c>-0,25</c>.</summary>
    static readonly System.Text.RegularExpressions.Regex CommaDecimal =
        new(@"^-?\d+,\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static readonly string[] RequiredParts =
    {
        "[Content_Types].xml",
        "_rels/.rels",
        "visio/document.xml",
        "visio/_rels/document.xml.rels",
        "visio/pages/pages.xml",
        "visio/pages/_rels/pages.xml.rels",
        "visio/pages/page1.xml",
        "visio/windows.xml",
    };

    public static List<string> Validate(string path)
    {
        var issues = new List<string>();

        if (!File.Exists(path))
        {
            issues.Add("file does not exist");
            return issues;
        }

        if (new FileInfo(path).Length == 0)
        {
            issues.Add("file is empty");
            return issues;
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (Exception ex)
        {
            issues.Add($"not a readable ZIP/OPC package: {ex.Message}");
            return issues;
        }

        using (zip)
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            foreach (var dup in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add($"duplicate part: {dup.Key}");

            foreach (var required in RequiredParts)
                if (!nameSet.Contains(required))
                    issues.Add($"missing part: {required}");

            if (issues.Count > 0)
                return issues;

            // ---- every XML part must parse ------------------------------------
            var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names.Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                               || n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = zip.GetEntry(name)!.Open();
                    docs[name] = XDocument.Load(stream, LoadOptions.None);
                }
                catch (Exception ex)
                {
                    issues.Add($"malformed XML in {name}: {ex.Message}");
                }
            }

            if (issues.Count > 0)
                return issues;

            // ---- content types cover every part, and point at real parts ------
            var types = docs["[Content_Types].xml"].Root!;
            var overrides = types.Elements(Ct + "Override")
                .Select(o => ((string?)o.Attribute("PartName") ?? "").TrimStart('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaults = types.Elements(Ct + "Default")
                .Select(d => ((string?)d.Attribute("Extension") ?? "").TrimStart('.').ToLowerInvariant())
                .ToHashSet();

            foreach (var name in names.Where(n => n != "[Content_Types].xml"))
            {
                string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                if (!overrides.Contains(name) && !defaults.Contains(ext))
                    issues.Add($"part has no content type: {name}");
            }

            foreach (var part in overrides)
                if (!nameSet.Contains(part))
                    issues.Add($"content-type override points at missing part: {part}");

            // ---- relationships must resolve -----------------------------------
            foreach (var relsName in names.Where(n => n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                string sourcePart = SourcePartOf(relsName);
                foreach (var rel in docs[relsName].Root!.Elements(Rel + "Relationship"))
                {
                    if ((string?)rel.Attribute("TargetMode") == "External") continue;

                    string target = (string?)rel.Attribute("Target") ?? "";
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        issues.Add($"relationship with empty target in {relsName}");
                        continue;
                    }

                    string resolved = Resolve(sourcePart, target);
                    if (!nameSet.Contains(resolved))
                        issues.Add($"dangling relationship in {relsName}: '{target}' -> '{resolved}'");
                }
            }

            // ---- reachability: every part must be reachable from the package root
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();

            foreach (var rel in docs["_rels/.rels"].Root!.Elements(Rel + "Relationship"))
            {
                if ((string?)rel.Attribute("TargetMode") == "External") continue;
                string resolved = Resolve("", (string?)rel.Attribute("Target") ?? "");
                if (nameSet.Contains(resolved) && reachable.Add(resolved)) queue.Enqueue(resolved);
            }

            while (queue.Count > 0)
            {
                string part = queue.Dequeue();
                if (!docs.TryGetValue(RelsNameFor(part), out var partRels)) continue;

                foreach (var rel in partRels.Root!.Elements(Rel + "Relationship"))
                {
                    if ((string?)rel.Attribute("TargetMode") == "External") continue;
                    string resolved = Resolve(part, (string?)rel.Attribute("Target") ?? "");
                    if (nameSet.Contains(resolved) && reachable.Add(resolved)) queue.Enqueue(resolved);
                }
            }

            foreach (var name in names.Where(n => n != "[Content_Types].xml"
                                               && !n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                if (!reachable.Contains(name))
                    issues.Add($"orphaned part (not reachable from _rels/.rels): {name}");

            // ---- page <Rel r:id> must exist in the page relationships ---------
            if (docs.TryGetValue("visio/pages/pages.xml", out var pages) &&
                docs.TryGetValue("visio/pages/_rels/pages.xml.rels", out var pagesRels))
            {
                var relIds = pagesRels.Root!.Elements(Rel + "Relationship")
                    .Select(r => (string?)r.Attribute("Id")).ToHashSet();

                foreach (var page in pages.Descendants(Main + "Page"))
                {
                    var rel = page.Element(Main + "Rel");
                    if (rel is null)
                    {
                        issues.Add("page has no <Rel> pointing at its content part");
                        continue;
                    }

                    string id = (string?)rel.Attribute(R + "id") ?? "";
                    if (!relIds.Contains(id))
                        issues.Add($"page references r:id='{id}' that is not declared in pages.xml.rels");
                }
            }

            // ---- shape integrity ----------------------------------------------
            if (docs.TryGetValue("visio/pages/page1.xml", out var pageXml) &&
                pageXml.Root!.Name == Main + "PageContents")
            {
                var shapes = pageXml.Descendants(Main + "Shape").ToList();
                if (shapes.Count == 0)
                    issues.Add("page contains no shapes");

                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var shape in shapes)
                {
                    string id = (string?)shape.Attribute("ID") ?? "";
                    if (!int.TryParse(id, out _)) issues.Add($"shape has a non-numeric ID: '{id}'");
                    else if (!ids.Add(id)) issues.Add($"duplicate shape ID: {id}");

                    foreach (var required in new[] { "PinX", "PinY", "Width", "Height" })
                    {
                        var cell = shape.Elements(Main + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == required);
                        if (cell is null)
                        {
                            issues.Add($"shape {id} is missing the {required} cell");
                            continue;
                        }

                        if (!double.TryParse((string?)cell.Attribute("V"),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double value)
                            || double.IsNaN(value) || double.IsInfinity(value))
                            issues.Add($"shape {id} has an invalid {required}");
                    }

                    // geometry rows must have numeric X/Y
                    foreach (var row in shape.Descendants(Main + "Row"))
                    {
                        foreach (var axis in new[] { "X", "Y" })
                        {
                            var cell = row.Elements(Main + "Cell").FirstOrDefault(c => (string?)c.Attribute("N") == axis);
                            if (cell is null) continue;

                            string v = (string?)cell.Attribute("V") ?? "";
                            if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double d)
                                || double.IsNaN(d) || double.IsInfinity(d))
                                issues.Add($"shape {id} has a non-numeric geometry {axis}: '{v}'");
                        }
                    }

                    // no locale leakage: a numeric cell written as "1,5" instead of "1.5".
                    // Commas inside formulas (NURBS(…), RGB(…), GUARD(…)) are legitimate.
                    foreach (var cell in shape.Descendants(Main + "Cell"))
                    {
                        string v = ((string?)cell.Attribute("V") ?? "").Trim();
                        if (CommaDecimal.IsMatch(v))
                            issues.Add($"shape {id} cell '{cell.Attribute("N")?.Value}' looks culture-formatted: '{v}'");
                    }
                }
            }
        }

        return issues;
    }

    static string RelsNameFor(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0
            ? "_rels/" + part + ".rels"
            : part[..slash] + "/_rels/" + part[(slash + 1)..] + ".rels";
    }

    static string SourcePartOf(string relsName)
    {
        int idx = relsName.LastIndexOf("/_rels/", StringComparison.Ordinal);
        if (idx < 0)
            return ""; // package-root _rels/.rels

        string directory = relsName[..idx];
        string file = relsName[(idx + "/_rels/".Length)..];
        if (file.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            file = file[..^".rels".Length];

        return directory + "/" + file;
    }

    static string Resolve(string sourcePart, string target)
    {
        string baseDir = "";
        int slash = sourcePart.LastIndexOf('/');
        if (slash > 0) baseDir = sourcePart[..slash];

        string combined = baseDir.Length == 0 ? target : baseDir + "/" + target;

        var stack = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }

        return string.Join("/", stack);
    }
}
