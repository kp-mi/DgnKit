using System.Text;
using System.Text.RegularExpressions;
using DgnKit.Elements;
using DgnKit.Export;
using DgnKit.Graphics;
using DgnKit.Model;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace DgnKit.Tests;

/// <summary>Result of comparing a DGN (via DgnKit) with the official DOT plot PDF of the same sheet.</summary>
public sealed class PdfComparison
{
    public string Item { get; init; } = "";
    public string Dgn { get; init; } = "";
    public string Pdf { get; init; } = "";
    public string Model { get; set; } = "";
    public int PdfPages { get; set; }
    public int BestPage { get; set; } = -1;
    /// <summary>Distinct normalised words in the official page's text layer.</summary>
    public int PdfWords { get; set; }
    public int DgnWords { get; set; }
    /// <summary>Fraction of PDF words also present in the DGN text (recall of our text extraction).</summary>
    public double TextRecall { get; set; }
    /// <summary>Fraction of DGN words present in the PDF text layer.</summary>
    public double TextPrecision { get; set; }
    /// <summary>Ink-pixel F1 (2-px tolerance) between official page raster and DgnKit's PDF raster after extent alignment.</summary>
    public double InkF1 { get; set; } = double.NaN;
    public double InkRecall { get; set; } = double.NaN;
    public double InkPrecision { get; set; } = double.NaN;
    public string? Note { get; set; }
    public string? OverlayPng { get; set; }
}

public static class PdfComparer
{
    private static readonly Regex Word = new("[A-Z0-9][A-Z0-9.\\-/']*[A-Z0-9]|[A-Z0-9]", RegexOptions.Compiled);

    public static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Word.Matches(s.ToUpperInvariant()))
            if (m.Value.Length >= 2 && m.Value.Any(char.IsLetter)) set.Add(m.Value.Trim('.', '-', '/', '\''));
        set.Remove("");
        return set;
    }

    /// <summary>Text drawn by the DGN sheet: text elements, text nodes, tags (and reference texts when loaded).</summary>
    public static string DgnText(DgnFile f, DgnModel m)
    {
        var sb = new StringBuilder();
        foreach (var p in DisplayListBuilder.Build(f, m, new DisplayListOptions { ModelName = m.Name, IncludeReferences = true }))
            if (p.Kind == PrimitiveKind.Text && p.Text != null) sb.Append(p.Text).Append(' ');
        foreach (var t in m.AllElements().OfType<TagElement>()) sb.Append(t.Value).Append(' ');
        return sb.ToString();
    }

    /// <summary>
    /// Chooses the plotted sheet model without looking at the PDF: the model whose display list <i>with references</i>
    /// draws the most text, ties broken by the most own text elements, then file order. A sheet model that
    /// self-references its component models (FDOT: <c>Border Sheet</c> → <c>Text</c>, <c>Details</c>, default model…)
    /// thereby wins over the component holding the most own text, matching what the official PDF plots.
    /// </summary>
    public static DgnModel PickModel(DgnFile f) =>
        f.Models.Where(m => !m.IsDictionary)
            .Select((m, i) => (Model: m, Index: i,
                RefText: DisplayListBuilder.Build(f, m, new DisplayListOptions { ModelName = m.Name, IncludeReferences = true })
                    .Count(p => p.Kind == PrimitiveKind.Text && p.Text != null),
                OwnText: m.AllElements().Count(e => e is TextElement)))
            .OrderByDescending(t => t.RefText).ThenByDescending(t => t.OwnText).ThenBy(t => t.Index)
            .First().Model;

    public static PdfComparison Compare(string item, string dgnPath, string pdfPath, string workDir, bool raster = true)
    {
        var r = new PdfComparison { Item = item, Dgn = Path.GetFileName(dgnPath), Pdf = Path.GetFileName(pdfPath) };
        var f = DgnFile.Open(dgnPath, new DgnReadOptions { LoadReferences = true });
        var m = PickModel(f);
        r.Model = m.Name;
        var dgnTokens = Tokens(DgnText(f, m));
        r.DgnWords = dgnTokens.Count;

        // best page by token overlap
        using (var pdf = PdfDocument.Open(pdfPath))
        {
            r.PdfPages = pdf.NumberOfPages;
            double best = -1;
            HashSet<string>? bestTokens = null;
            for (int i = 1; i <= pdf.NumberOfPages; i++)
            {
                var page = pdf.GetPage(i);
                var toks = Tokens(string.Join(" ", page.GetWords().Select(w => w.Text)));
                if (toks.Count == 0) continue;
                double overlap = toks.Count(dgnTokens.Contains) / (double)Math.Max(1, Math.Min(toks.Count, dgnTokens.Count));
                if (overlap > best) { best = overlap; r.BestPage = i - 1; bestTokens = toks; }
            }
            if (bestTokens != null)
            {
                r.PdfWords = bestTokens.Count;
                r.TextRecall = bestTokens.Count == 0 ? double.NaN : bestTokens.Count(dgnTokens.Contains) / (double)bestTokens.Count;
                r.TextPrecision = dgnTokens.Count == 0 ? double.NaN : dgnTokens.Count(bestTokens.Contains) / (double)dgnTokens.Count;
            }
            else
            {
                r.Note = "official PDF has no text layer (text plotted as vectors)";
                r.BestPage = pdf.NumberOfPages == 1 ? 0 : -1;
            }
        }

        if (raster && r.BestPage >= 0)
        {
            try
            {
                Directory.CreateDirectory(workDir);
                string ours = Path.Combine(workDir, Path.GetFileNameWithoutExtension(dgnPath) + ".dgnkit.pdf");
                new PdfExporter().Export(f, ours, new ExportOptions { ModelName = m.Name, IncludeReferences = true });
                using var a = Render(pdfPath, r.BestPage);
                using var b = Render(ours, 0);
                var fit = FitFromText(pdfPath, r.BestPage, ours);
                if (fit != null) r.Note = (r.Note == null ? "" : r.Note + "; ") + $"text-registered ({fit.Value.Pairs} anchors)";
                var (p, rc, f1, overlay) = InkScore(a, b, fit);
                if (fit == null)
                {
                    // no text anchors: also try full-extent alignment and keep the better registration
                    var alt = InkScore(a, b, null, robustBox: false);
                    if (alt.F1 > f1) { overlay.Dispose(); (p, rc, f1, overlay) = alt; } else alt.Overlay.Dispose();
                }
                r.InkPrecision = p; r.InkRecall = rc; r.InkF1 = f1;
                string png = Path.Combine(workDir, Path.GetFileNameWithoutExtension(dgnPath) + ".overlay.png");
                using (var fs = File.Create(png)) overlay.Encode(fs, SKEncodedImageFormat.Png, 90);
                overlay.Dispose();
                // repo-relative: the report is committed, and an absolute path would carry the local user profile
                r.OverlayPng = Path.GetRelativePath(TestPaths.RepoRoot, png).Replace('\\', '/');
            }
            catch (Exception ex) { r.Note = (r.Note == null ? "" : r.Note + "; ") + "raster compare failed: " + ex.Message; }
        }
        return r;
    }

    private const int Dpi = 40;

    private static SKBitmap Render(string pdf, int pageIndex)
    {
        using var fs = File.OpenRead(pdf);
        return Conversion.ToImage(fs, page: pageIndex, options: new RenderOptions(Dpi: Dpi, BackgroundColor: SKColors.White));
    }

    /// <summary>Similarity fit (uniform scale + translation, PDF points) mapping our page onto the official page.</summary>
    public readonly record struct PageFit(double S, double Tx, double Ty, int Pairs, double OfficialHeightPt, double OursHeightPt);

    /// <summary>
    /// Registers our PDF to the official page using words that occur exactly once on both pages: a robust median fit,
    /// or — when that leaves too few inliers (within 6 pt) — a consensus fit over 2-word hypotheses refined by least
    /// squares on its inliers. The fallback matters when our glyphs are narrower than the plot's (TrueType em vs cap
    /// height): words later in a line drift left and bias the medians, while line-start words still agree exactly.
    /// (Preferring the median fit when it passes keeps sheets whose text widths match on their accurate registration.)
    /// </summary>
    public static PageFit? FitFromText(string officialPdf, int officialPage, string ourPdf)
    {
        static Dictionary<string, (double X, double Y)> Unique(UglyToad.PdfPig.Content.Page page)
        {
            var groups = page.GetWords().Where(w => w.Text.Length >= 3 && w.Text.Any(char.IsLetterOrDigit))
                .GroupBy(w => w.Text.ToUpperInvariant());
            return groups.Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => (g.First().BoundingBox.Left, g.First().BoundingBox.Bottom));
        }
        using var a = PdfDocument.Open(officialPdf);
        using var b = PdfDocument.Open(ourPdf);
        var pa = a.GetPage(officialPage + 1); var pb = b.GetPage(1);
        var ua = Unique(pa); var ub = Unique(pb);
        var pairs = ua.Keys.Where(ub.ContainsKey).Select(k => (A: ua[k], B: ub[k])).ToList();
        if (pairs.Count < 6) return null;
        // candidate scales from pair distances (robust median), then translation median
        var scales = new List<double>();
        var rnd = new Random(1);
        for (int i = 0; i < Math.Min(4000, pairs.Count * pairs.Count); i++)
        {
            var p = pairs[rnd.Next(pairs.Count)]; var q = pairs[rnd.Next(pairs.Count)];
            double db = Math.Sqrt(Math.Pow(p.B.X - q.B.X, 2) + Math.Pow(p.B.Y - q.B.Y, 2));
            double da = Math.Sqrt(Math.Pow(p.A.X - q.A.X, 2) + Math.Pow(p.A.Y - q.A.Y, 2));
            if (db > 20) scales.Add(da / db);
        }
        if (scales.Count < 5) return null;
        bool In((double X, double Y) A, (double X, double Y) B, double s, double tx, double ty) =>
            Math.Abs(A.X - (s * B.X + tx)) < 6 && Math.Abs(A.Y - (s * B.Y + ty)) < 6;
        int Count(double s, double tx, double ty) => pairs.Count(p => In(p.A, p.B, s, tx, ty));
        // median fit
        double s = Median(scales);
        double tx = Median(pairs.Select(p => p.A.X - s * p.B.X).ToList());
        double ty = Median(pairs.Select(p => p.A.Y - s * p.B.Y).ToList());
        int inliers = Count(s, tx, ty), need = Math.Max(4, pairs.Count / 4);
        if (inliers >= need) return new PageFit(s, tx, ty, inliers, pa.Height, pb.Height);
        // fallback consensus fit: exact similarity through two pairs, keep the hypothesis with most inliers
        for (int i = 0; i < 4000; i++)
        {
            var p = pairs[rnd.Next(pairs.Count)]; var q = pairs[rnd.Next(pairs.Count)];
            double db = Math.Sqrt(Math.Pow(p.B.X - q.B.X, 2) + Math.Pow(p.B.Y - q.B.Y, 2));
            if (db < 50) continue;
            double hs = Math.Sqrt(Math.Pow(p.A.X - q.A.X, 2) + Math.Pow(p.A.Y - q.A.Y, 2)) / db;
            double htx = p.A.X - hs * p.B.X, hty = p.A.Y - hs * p.B.Y;
            int n = Count(hs, htx, hty);
            if (n > inliers) { inliers = n; s = hs; tx = htx; ty = hty; }
        }
        // least-squares refinement on the inliers (kept only if it does not lose inliers)
        var inl = pairs.Where(p => In(p.A, p.B, s, tx, ty)).ToList();
        if (inl.Count >= 3)
        {
            double ax = inl.Average(p => p.A.X), ay = inl.Average(p => p.A.Y), bx = inl.Average(p => p.B.X), by = inl.Average(p => p.B.Y);
            double num = inl.Sum(p => (p.B.X - bx) * (p.A.X - ax) + (p.B.Y - by) * (p.A.Y - ay));
            double den = inl.Sum(p => (p.B.X - bx) * (p.B.X - bx) + (p.B.Y - by) * (p.B.Y - by));
            if (den > 0)
            {
                double rs = num / den, rtx = ax - rs * bx, rty = ay - rs * by;
                int n = Count(rs, rtx, rty);
                if (n >= inliers) { inliers = n; s = rs; tx = rtx; ty = rty; }
            }
        }
        if (inliers < need) return null;
        return new PageFit(s, tx, ty, inliers, pa.Height, pb.Height);
    }

    private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

    /// <summary>Ink mask plus a robust (0.5%..99.5% quantile) ink bounding box, so stray far-away marks don't skew alignment.</summary>
    private static bool[,] Ink(SKBitmap bmp, out SKRectI box, bool robust = true)
    {
        int w = bmp.Width, h = bmp.Height;
        var ink = new bool[w, h];
        var colCount = new int[w]; var rowCount = new int[h];
        long total = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red + c.Green + c.Blue < 600) { ink[x, y] = true; colCount[x]++; rowCount[y]++; total++; }
            }
        if (total == 0) { box = SKRectI.Empty; return ink; }
        double qlo = robust ? 0.005 : 0.0, qhi = robust ? 0.995 : 1.0;
        (int, int) Q(int[] counts, long total)
        {
            long lo = (long)(total * qlo), hi = (long)Math.Ceiling(total * qhi), acc = 0; int a = 0, b = counts.Length - 1;
            for (int i = 0; i < counts.Length; i++) { acc += counts[i]; if (acc > lo) { a = i; break; } }
            acc = 0;
            for (int i = 0; i < counts.Length; i++) { acc += counts[i]; if (acc >= hi) { b = i; break; } }
            return (a, b);
        }
        var (x0, x1) = Q(colCount, total); var (y0, y1) = Q(rowCount, total);
        box = new SKRectI(x0, y0, x1 + 1, y1 + 1);
        return ink;
    }

    /// <summary>
    /// Aligns the two ink bounding boxes (scale + translate, aspect preserved via average scale), then measures
    /// precision/recall of ink pixels within a 2-pixel tolerance. Returns an overlay (official = blue, ours = red).
    /// </summary>
    public static (double P, double R, double F1, SKBitmap Overlay) InkScore(SKBitmap official, SKBitmap ours, PageFit? fit = null, bool robustBox = true)
    {
        var a = Ink(official, out var ba, robustBox);
        var b = Ink(ours, out var bb, robustBox);
        if (ba.IsEmpty || bb.IsEmpty) return (0, 0, 0, official.Copy());
        int W = official.Width, H = official.Height;
        // pixel mapping ours→official: from the text registration (PDF points, y up) or the robust ink boxes
        Func<int, int, (int, int)> map;
        if (fit is { } f)
        {
            double k = Dpi / 72.0;
            map = (x, y) =>
            {
                double bx = x / k, by = f.OursHeightPt - y / k;            // our pixel → our PDF points
                double ax = f.S * bx + f.Tx, ay = f.S * by + f.Ty;        // → official PDF points
                return ((int)Math.Round(ax * k), (int)Math.Round((f.OfficialHeightPt - ay) * k));
            };
        }
        else
        {
            double sx = (double)ba.Width / bb.Width, sy = (double)ba.Height / bb.Height;
            double s = Math.Abs(sx - sy) / Math.Max(sx, sy) < 0.05 ? (sx + sy) / 2 : Math.Min(sx, sy);
            map = (x, y) => ((int)Math.Round(ba.Left + (x - bb.Left) * s), (int)Math.Round(ba.Top + (y - bb.Top) * s));
        }
        var mapped = new bool[W, H];
        for (int y = 0; y < ours.Height; y++)
            for (int x = 0; x < ours.Width; x++)
            {
                if (!b[x, y]) continue;
                var (X, Y) = map(x, y);
                if (X >= 0 && Y >= 0 && X < W && Y < H) mapped[X, Y] = true;
            }
        static bool Near(bool[,] m, int x, int y, int W, int H, int t)
        {
            for (int dy = -t; dy <= t; dy++)
                for (int dx = -t; dx <= t; dx++)
                {
                    int X = x + dx, Y = y + dy;
                    if (X >= 0 && Y >= 0 && X < W && Y < H && m[X, Y]) return true;
                }
            return false;
        }
        long ta = 0, tb = 0, hitA = 0, hitB = 0;
        var overlay = new SKBitmap(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool ia = a[x, y], ib = mapped[x, y];
                if (ia) { ta++; if (Near(mapped, x, y, W, H, 2)) hitA++; }
                if (ib) { tb++; if (Near(a, x, y, W, H, 2)) hitB++; }
                overlay.SetPixel(x, y, ia && ib ? new SKColor(40, 40, 40) : ia ? new SKColor(60, 110, 255) : ib ? new SKColor(255, 60, 60) : SKColors.White);
            }
        double recall = ta == 0 ? 0 : (double)hitA / ta;
        double precision = tb == 0 ? 0 : (double)hitB / tb;
        double f1 = recall + precision == 0 ? 0 : 2 * recall * precision / (recall + precision);
        return (precision, recall, f1, overlay);
    }

    public static string Report(IEnumerable<PdfComparison> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DgnKit vs official DOT plot PDFs");
        sb.AppendLine();
        sb.AppendLine("- **Text recall**: share of words on the official PDF page (text layer) that DgnKit extracted from the DGN (text, text nodes, tags, references).");
        sb.AppendLine("- **Ink F1**: raster agreement between the official page and DgnKit's own PDF export at 40 dpi after aligning drawing extents (2 px tolerance). Overlay PNGs: blue = official only, red = DgnKit only, dark = both.");
        sb.AppendLine();
        sb.AppendLine("| item | dgn | model | pdf page | pdf words | text recall | text precision | ink recall | ink precision | ink F1 | note |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Item} | {r.Dgn} | {r.Model} | {r.BestPage + 1}/{r.PdfPages} | {r.PdfWords} | {Pct(r.TextRecall)} | {Pct(r.TextPrecision)} | {Pct(r.InkRecall)} | {Pct(r.InkPrecision)} | {Pct(r.InkF1)} | {r.Note} |");
        return sb.ToString();
    }

    private static string Pct(double v) => double.IsNaN(v) ? "–" : $"{100 * v:0.0}%";
}
