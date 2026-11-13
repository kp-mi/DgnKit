using System.Globalization;
using DgnKit.Export.Pdf;
using DgnKit.Graphics;
using DgnKit.Model;
using DPoint3d = DgnKit.Geometry.DPoint3d;

namespace DgnKit.Export;

/// <summary>
/// Vector PDF plot of a model (top view) using the built-in <see cref="MiniPdf"/> writer (no third-party dependency).
/// Sheet models drawn at full size plot 1:1 when they fit the paper; otherwise the drawing is fitted to the paper.
/// Line weights → pen widths (mm), built-in line styles → dash patterns, text → real (searchable) PDF text.
/// </summary>
public sealed class PdfExporter : IExporter
{
    public string Format => "pdf";

    public ExportReport Export(DgnFile file, string outputPath, ExportOptions options)
    {
        var report = new ExportReport();
        var model = ExportHelpers.ResolveModel(file, options);
        var dl = options.ToDisplayListOptions();
        dl.ModelName = model.Name;
        dl.IncludeConstruction = false; // construction class is not plotted by default
        var prims = DisplayListBuilder.Build(file, model, dl);
        report.Primitives = prims.Count;

        var ext = options.FitAllGeometry ? FullExtents(prims) : RobustExtents(prims);
        if (ext.IsEmpty) { ext.Extend(new DPoint3d(0, 0)); ext.Extend(new DPoint3d(1, 1)); }
        // drop primitives entirely outside the plot extents (stray far-away elements)
        prims = prims.Where(p => { var r = p.Range(); return r.IsEmpty || (r.High.X >= ext.Low.X && r.Low.X <= ext.High.X && r.High.Y >= ext.Low.Y && r.Low.Y <= ext.High.Y); }).ToList();

        double mpu = ExportHelpers.MetersPerMaster(file, model);
        double extWmm = ext.XSize * (mpu > 0 ? mpu * 1000 : 1), extHmm = ext.YSize * (mpu > 0 ? mpu * 1000 : 1);
        (double pw, double ph) = PaperSize(options.Paper, extWmm, extHmm);
        double margin = 10; // mm
        double sx = (pw - 2 * margin) / Math.Max(ext.XSize, 1e-9), sy = (ph - 2 * margin) / Math.Max(ext.YSize, 1e-9);
        double scale = Math.Min(sx, sy); // mm per master unit
        if (options.Paper == null && mpu > 0 && extWmm <= pw && extHmm <= ph) scale = mpu * 1000; // true 1:1 sheet
        double offX = margin + ((pw - 2 * margin) - ext.XSize * scale) / 2, offY = margin + ((ph - 2 * margin) - ext.YSize * scale) / 2;
        const double mmToPt = 72.0 / 25.4;
        (double X, double Y) Map(DPoint3d q) => ((offX + (q.X - ext.Low.X) * scale) * mmToPt, (offY + (q.Y - ext.Low.Y) * scale) * mmToPt);

        var pdf = new MiniPdf { Title = Path.GetFileName(file.Path ?? "dgn") + " - " + model.Name };
        var page = pdf.AddPage(pw * mmToPt, ph * mmToPt);
        page.SetCaps(1, 1);
        // display priority (2D) then file order
        foreach (var p in prims.Select((p, i) => (p, i)).OrderBy(t => t.p.Symbology.Priority).ThenBy(t => t.i).Select(t => t.p))
        {
            try { Draw(page, p, Map, scale, options, report); }
            catch (Exception ex) { report.Skipped++; if (report.Warnings.Count < 50) report.Warnings.Add($"element {p.Leaf.Id}: {ex.Message}"); }
        }
        pdf.Save(outputPath);
        return report;
    }

    private static DgnKit.Geometry.Range3d FullExtents(List<Primitive> prims)
    {
        var ext = DgnKit.Geometry.Range3d.Empty;
        foreach (var p in prims) ext.Extend(p.Range());
        return ext;
    }

    /// <summary>
    /// Plot extents ignoring small clusters of stray geometry far from the drawing (common in DGNs: elements left at the
    /// design-plane origin, cell placement markers…). Core box = 1st–99th percentile of primitive centres, grown by 25%;
    /// result = union of all primitives whose centre lies in the core box.
    /// </summary>
    internal static DgnKit.Geometry.Range3d RobustExtents(List<Primitive> prims)
    {
        var ranges = prims.Select(p => p.Range()).Where(r => !r.IsEmpty).ToList();
        if (ranges.Count < 20) return FullExtents(prims);
        var xs = ranges.Select(r => r.Center.X).OrderBy(v => v).ToList();
        var ys = ranges.Select(r => r.Center.Y).OrderBy(v => v).ToList();
        double Q(List<double> v, double q) => v[(int)Math.Max(0, Math.Min(v.Count - 1, Math.Round(q * (v.Count - 1))))];
        double x0 = Q(xs, 0.01), x1 = Q(xs, 0.99), y0 = Q(ys, 0.01), y1 = Q(ys, 0.99);
        double gx = Math.Max((x1 - x0) * 0.25, 1e-9), gy = Math.Max((y1 - y0) * 0.25, 1e-9);
        x0 -= gx; x1 += gx; y0 -= gy; y1 += gy;
        var ext = DgnKit.Geometry.Range3d.Empty;
        foreach (var r in ranges)
            if (r.Center.X >= x0 && r.Center.X <= x1 && r.Center.Y >= y0 && r.Center.Y <= y1) ext.Extend(r);
        return ext.IsEmpty ? FullExtents(prims) : ext;
    }

    private static RgbColor Col(RgbColor c, ExportOptions o) =>
        o.PrintWhiteAsBlack && c.R > 245 && c.G > 245 && c.B > 245 ? new RgbColor(0, 0, 0) : c;

    private static void Pen(MiniPdfPage pg, Primitive p, ExportOptions o, double scale)
    {
        var c = Col(p.Symbology.Color, o);
        pg.SetStroke(c.R, c.G, c.B);
        // custom styles with wide dashes plot at the stroke width (capped so a mis-scaled style cannot flood the sheet)
        double styleMm = p.Symbology.LineStyleWidth * scale;
        double wMm = Math.Max(0.1, p.Symbology.WeightMm);
        if (styleMm > wMm && styleMm < 10) wMm = styleMm;
        pg.SetLineWidth(wMm * 72 / 25.4);
        pg.SetDash(DashPt(p.Symbology, scale, out double phase), phase);
    }

    /// <summary>
    /// Dash pattern in points: decoded custom line styles (root master units × <paramref name="scale"/> mm per master
    /// unit), else MicroStation's built-in device patterns. Patterns whose dashes would print shorter than ~0.05 mm or
    /// longer than the sheet degrade to solid lines rather than to dust or invisible gaps.
    /// </summary>
    internal static double[]? DashPt(ResolvedSymbology s, double scale, out double phase)
    {
        const double mmToPt = 72 / 25.4;
        phase = 0;
        if (s.DashPattern != null)
        {
            var mm = s.DashPattern.Select(v => v * scale).ToArray();
            double total = mm.Sum();
            if (total < 0.2 || total > 5000 || mm.Where((v, i) => i % 2 == 0).Max() < 0.05) return null;
            phase = s.DashPhase * scale * mmToPt;
            return mm.Select(v => v * mmToPt).ToArray();
        }
        return LineStyleDefinition.BuiltInPatternMm(s.StyleNumber)?.Select(v => v * mmToPt).ToArray();
    }

    private static void Draw(MiniPdfPage pg, Primitive p, Func<DPoint3d, (double X, double Y)> map, double scale, ExportOptions o, ExportReport report)
    {
        switch (p.Kind)
        {
            case PrimitiveKind.Polyline:
            case PrimitiveKind.Arc:
            {
                var pts = (p.Kind == PrimitiveKind.Arc ? p.ArcPoints(24, 0.05 / Math.Max(scale, 1e-12)) : p.Points).Select(map).ToList();
                if (pts.Count < 2) return;
                Pen(pg, p, o, scale);
                bool fill = p.Closed && p.Symbology.Filled && pts.Count >= 3;
                if (fill)
                {
                    var fc = Col(p.Symbology.FillColor ?? p.Symbology.Color, o);
                    pg.SetFill(fc.R, fc.G, fc.B);
                }
                pg.Path(pts, p.Closed && pts.Count >= 3, stroke: true, fill: fill);
                report.Count(p.Kind.ToString());
                break;
            }
            case PrimitiveKind.Point:
            {
                var c = map(p.Points[0]);
                var col = Col(p.Symbology.Color, o);
                pg.SetFill(col.R, col.G, col.B);
                pg.Dot(c.X, c.Y, Math.Max(0.25, p.Symbology.WeightMm) * 72 / 25.4 / 2);
                report.Count("Point");
                break;
            }
            case PrimitiveKind.Text:
            {
                if (string.IsNullOrWhiteSpace(p.Text)) return;
                double hPt = p.TextHeight * scale * 72 / 25.4;
                if (hPt < 0.3) return;
                // MicroStation text size = cap height (TrueType and RSC fonts alike) → Helvetica em size
                double em = hPt / FontMetrics.HelveticaCapHeight;
                var origin = map(p.Points[0]);
                var dir = p.TextRotation.Column(0);
                double angle = Math.Atan2(dir.Y, dir.X);
                double wf = p.TextHeight > 0 && p.TextWidth > 0 ? p.TextWidth / p.TextHeight : 1;
                if (wf < 0.05 || wf > 20) wf = 1;
                wf *= FontMetrics.WidthFactor(p.FontName); // wide CAD fonts (FDOT, Engineering) vs Helvetica advances
                var col = Col(p.Symbology.Color, o);
                pg.SetFill(col.R, col.G, col.B);
                foreach (var (line, i) in p.Text!.Replace("\r", "").Split('\n').Select((l, i) => (l, i)))
                {
                    double dy = -i * hPt * 1.5; // successive lines go "down" the text's local y axis
                    pg.Text(line, origin.X - Math.Sin(angle) * dy, origin.Y + Math.Cos(angle) * dy, em, angle, wf);
                }
                report.Count("Text");
                break;
            }
            case PrimitiveKind.Mesh when p.Faces != null:
            {
                Pen(pg, p, o, scale);
                foreach (var f in p.Faces) pg.Path(f.Select(i => map(p.Points[i])).ToList(), true, true, false);
                report.Count("Mesh");
                break;
            }
        }
    }

    /// <summary>Paper size in mm (width, height, landscape by default).</summary>
    internal static (double W, double H) PaperSize(string? paper, double extWmm, double extHmm)
    {
        if (string.IsNullOrWhiteSpace(paper))
        {
            // full-size sheets (≤ ~1.3 m) print at their own size; larger drawings fit ANSI D landscape
            if (extWmm > 50 && extHmm > 50 && extWmm <= 1300 && extHmm <= 1300) return (extWmm + 20, extHmm + 20);
            return extWmm >= extHmm ? (863.6, 558.8) : (558.8, 863.6);
        }
        string s = paper!.Trim().ToUpperInvariant();
        (double, double)? named = s switch
        {
            "A0" => (1189, 841), "A1" => (841, 594), "A2" => (594, 420), "A3" => (420, 297), "A4" => (297, 210),
            "ANSI-A" or "LETTER" => (279.4, 215.9), "ANSI-B" or "TABLOID" => (431.8, 279.4), "ANSI-C" => (558.8, 431.8),
            "ANSI-D" => (863.6, 558.8), "ANSI-E" => (1117.6, 863.6), "11X17" => (431.8, 279.4), "22X34" => (863.6, 558.8),
            _ => null,
        };
        if (named.HasValue) return named.Value;
        bool inch = s.EndsWith("IN");
        var core = s.Replace("MM", "").Replace("IN", "");
        var parts = core.Split('X');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            return inch ? (w * 25.4, h * 25.4) : (w, h);
        throw new ArgumentException($"Unrecognised paper '{paper}'");
    }
}
