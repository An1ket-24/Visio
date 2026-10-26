namespace VisioGenerator;

/// <summary>Renderer-neutral primitive. All coordinates are inches, origin bottom-left.</summary>
public sealed class DShape
{
    public string Kind = "rect"; // rect | line | text
    public double X, Y, W, H;
    public string? Text;
    public double FontSize = 10;
    public bool Bold;
    public string TextColor = "#1F2937";
    public int Align = 1; // 0 left, 1 center, 2 right
    public string? Fill;
    public string? Stroke;
    public double StrokeWeight = 1;
    public bool Dashed;
    public bool Round = true;
    public List<(double X, double Y)> Points = new();
}

public sealed class Diagram
{
    public double Width { get; set; }
    public double Height { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Project { get; set; } = "";
    public List<DShape> Shapes { get; } = new();
}

/// <summary>
/// Deterministic layout engine: floors become horizontal bands, every medium becomes a
/// vertical trunk in its own lane, equipment sits in the right-hand column and connects
/// to each trunk with a horizontal branch.
///
/// Pass 1 = equipment rectangles, pass 2 = branch heights, pass 3 = trunks, then emit.
/// The resulting <see cref="Diagram"/> is what the VSDX writer consumes.
/// </summary>
public static class LayoutBuilder
{
    const double Gutter = 1.15;
    const double Band = 1.45;
    const double Header = 1.3;
    const double LegendH = 0.95;
    const double BoxW = 1.35;
    const double BoxH = 0.52;
    const double LaneStep = 1.5;
    const double BranchGap = 0.11;

    static readonly Dictionary<string, string> KindFill = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chiller"] = "#DAE3F3",
        ["ahu"] = "#E2EFDA",
        ["pump"] = "#FFF2CC",
        ["boiler"] = "#FCE4D6",
        ["tower"] = "#DEEBF7",
        ["panel"] = "#EDEDED",
        ["equipment"] = "#F2F2F2",
    };

    public static Diagram Build(RiserSpec spec)
    {
        if (spec.Floors.Count == 0)
            throw new InvalidOperationException("Spec must contain at least one floor.");

        var d = new Diagram
        {
            Title = spec.Title,
            Subtitle = spec.Subtitle,
            Project = spec.Project
        };

        var systems = spec.Systems;
        int ns = systems.Count;
        int nf = spec.Floors.Count;

        double trunkStart = Gutter + 0.75;
        double trunkEnd = trunkStart + Math.Max(0, ns - 1) * LaneStep;
        double boxSpan = 2 * BoxW + 0.2;

        d.Width = Math.Max(11.0, trunkEnd + 1.35 + boxSpan + 0.55);
        d.Height = Math.Max(8.5, Header + nf * Band + LegendH);

        double eqRight = d.Width - 0.55;
        double eqLeft = eqRight - boxSpan;

        double Top(int floor) => d.Height - Header - floor * Band;
        double Center(int floor) => Top(floor) - Band / 2;

        int SystemIndex(string id) => systems.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        // ---- riser membership -------------------------------------------------
        var nodeRisers = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in spec.Nodes)
            if (!nodeRisers.TryAdd(node.Id, new SortedSet<int>()))
                throw new InvalidOperationException($"Duplicate node id '{node.Id}'.");

        foreach (var link in spec.Links)
        {
            int idx = SystemIndex(link.Medium);
            if (idx < 0) continue;
            if (nodeRisers.TryGetValue(link.From, out var a)) a.Add(idx);
            if (nodeRisers.TryGetValue(link.To, out var b)) b.Add(idx);
        }

        foreach (var node in spec.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.System)) continue;
            int idx = SystemIndex(node.System);
            if (idx >= 0) nodeRisers[node.Id].Add(idx);
        }

        // ---- pass 1: equipment rectangles --------------------------------------
        var boxes = new Dictionary<string, (double X, double Y, double W, double H)>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in spec.Nodes
                     .Where(n => n.Floor >= 0 && n.Floor < nf)
                     .GroupBy(n => n.Floor))
        {
            var nodes = group.ToList();
            int count = nodes.Count;
            double c = Center(group.Key);

            for (int i = 0; i < count; i++)
            {
                double bx, by, bw, bh;
                if (count <= 2)
                {
                    bw = BoxW;
                    bh = BoxH;
                    bx = eqLeft + i * (BoxW + 0.2);
                    by = c - bh / 2;
                }
                else
                {
                    bh = Math.Min(BoxH, (Band - 0.2) / count);
                    bw = BoxW;
                    bx = eqLeft + (boxSpan - bw) / 2;
                    by = c - (count * (bh + 0.06) - 0.06) / 2 + i * (bh + 0.06);
                }

                boxes[nodes[i].Id] = (bx, by, bw, bh);
            }
        }

        // ---- pass 2: where every branch lands on each trunk -------------------
        var branchFor = new List<(string NodeId, int System, double Y, double X)>();
        var branchYs = new Dictionary<int, List<double>>();

        foreach (var node in spec.Nodes)
        {
            if (!boxes.TryGetValue(node.Id, out var box)) continue;

            var risers = nodeRisers[node.Id].ToList();
            for (int k = 0; k < risers.Count; k++)
            {
                int s = risers[k];
                double y = box.Y + box.H / 2 + (k - (risers.Count - 1) / 2.0) * BranchGap;
                branchFor.Add((node.Id, s, y, box.X));

                if (!branchYs.TryGetValue(s, out var list)) branchYs[s] = list = new List<double>();
                list.Add(y);
            }
        }

        // ---- floor grid --------------------------------------------------------
        for (int f = 0; f < nf; f++)
        {
            double y = Top(f);
            d.Shapes.Add(new DShape
            {
                Kind = "line",
                Points = { (Gutter - 0.3, y), (eqRight + 0.15, y) },
                Stroke = "#D0D5DD",
                StrokeWeight = 0.75,
                Dashed = true,
            });

            d.Shapes.Add(new DShape
            {
                Kind = "text",
                X = 0.12, Y = Center(f) - 0.13, W = Gutter - 0.2, H = 0.26,
                Text = spec.Floors[f].Name,
                FontSize = 9, Bold = true, TextColor = "#475467", Align = 0,
            });
        }

        // ---- pass 3: vertical trunks, extent driven by the branches -----------
        for (int s = 0; s < ns; s++)
        {
            if (!branchYs.TryGetValue(s, out var ys) || ys.Count == 0) continue;

            double x = trunkStart + s * LaneStep;
            double yLow = ys.Min() - 0.06;
            double yHigh = ys.Max() + 0.06;
            if (yHigh - yLow < 0.02) yHigh = yLow + 0.02;

            var sys = systems[s];
            d.Shapes.Add(new DShape
            {
                Kind = "line",
                Points = { (x, yLow), (x, yHigh) },
                Stroke = sys.Color,
                StrokeWeight = 2.2,
                Dashed = sys.Dashed,
            });

            d.Shapes.Add(new DShape
            {
                Kind = "text",
                X = x - 0.45, Y = yHigh + 0.08, W = 0.9, H = 0.22,
                Text = string.IsNullOrWhiteSpace(sys.Short) ? sys.Id : sys.Short,
                FontSize = 8, Bold = true, TextColor = sys.Color,
            });
        }

        // ---- branches ----------------------------------------------------------
        foreach (var branch in branchFor)
        {
            var sys = systems[branch.System];
            d.Shapes.Add(new DShape
            {
                Kind = "line",
                Points = { (branch.X, branch.Y), (trunkStart + branch.System * LaneStep, branch.Y) },
                Stroke = sys.Color,
                StrokeWeight = 1,
                Dashed = sys.Dashed,
            });
        }

        // ---- equipment boxes (drawn last so branches sit behind them) ---------
        foreach (var node in spec.Nodes)
        {
            if (!boxes.TryGetValue(node.Id, out var box)) continue;

            d.Shapes.Add(new DShape
            {
                Kind = "rect",
                X = box.X, Y = box.Y, W = box.W, H = box.H,
                Text = node.Label,
                FontSize = 7.5,
                Fill = KindFill.TryGetValue(node.Kind, out var fill) ? fill : KindFill["equipment"],
                Stroke = "#344054",
                StrokeWeight = 1,
            });
        }

        // ---- header ------------------------------------------------------------
        d.Shapes.Add(new DShape
        {
            Kind = "text",
            X = Gutter, Y = d.Height - 0.62, W = d.Width - Gutter - 0.6, H = 0.4,
            Text = spec.Title, FontSize = 16, Bold = true, Align = 0,
        });

        if (!string.IsNullOrWhiteSpace(spec.Subtitle))
        {
            d.Shapes.Add(new DShape
            {
                Kind = "text",
                X = Gutter, Y = d.Height - 0.95, W = d.Width - Gutter - 0.6, H = 0.26,
                Text = spec.Subtitle, FontSize = 9.5, TextColor = "#667085", Align = 0,
            });
        }

        // ---- legend ------------------------------------------------------------
        double lx = Gutter;
        const double ly = 0.45;

        foreach (var sys in systems)
        {
            d.Shapes.Add(new DShape
            {
                Kind = "line",
                Points = { (lx, ly + 0.1), (lx + 0.45, ly + 0.1) },
                Stroke = sys.Color, StrokeWeight = 2.2, Dashed = sys.Dashed,
            });

            string label = $"{sys.Short} – {sys.Name}";
            d.Shapes.Add(new DShape
            {
                Kind = "text",
                X = lx + 0.52, Y = ly - 0.02, W = label.Length * 0.062 + 0.2, H = 0.24,
                Text = label, FontSize = 8, Align = 0, TextColor = "#344054",
            });

            lx += 0.45 + 0.12 + label.Length * 0.062 + 0.35;
        }

        if (!string.IsNullOrWhiteSpace(spec.Project))
        {
            d.Shapes.Add(new DShape
            {
                Kind = "text",
                X = d.Width - 3.4, Y = 0.14, W = 3.0, H = 0.22,
                Text = spec.Project, FontSize = 8, Align = 2, TextColor = "#98A2B3",
            });
        }

        return d;
    }
}
