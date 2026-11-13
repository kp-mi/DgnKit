using DgnKit.Export;
using DgnKit.Graphics;
using DgnKit.Model;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// Text size in DgnKit's PDF against an official MicroStation plot. MicroStation's PDF driver writes an invisible text
/// layer whose font size is the text size and whose letters sit at the real glyph positions (the visible glyphs are
/// vectors), so after text registration: (a) our Helvetica cap height (em × 0.718) must equal the official size — MicroStation
/// sizes TrueType text by cap height — and (b) words must span the same length (FDOT's TrueType font is ~1.24× wider
/// than Helvetica, see <see cref="FontMetrics.WidthFactor"/>).
/// </summary>
public class TextSizeTests
{
    private readonly ITestOutputHelper _out;
    public TextSizeTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void Pdf_FdotText_MatchesOfficialPlotSize()
    {
        var dgn = Path.Combine(TestPaths.Corpus, "fdot-110-100", "110-100-1of1.dgn");
        var official = Path.Combine(TestPaths.Corpus, "fdot-110-100", "pdf", "110-100.pdf");
        if (!File.Exists(dgn) || !File.Exists(official)) return;
        var f = DgnFile.Open(dgn, new DgnReadOptions { LoadReferences = true });
        var m = PdfComparer.PickModel(f);
        var ours = Path.Combine(Directory.CreateDirectory(Path.Combine(TestPaths.Reports, "exports")).FullName, "110-100-textsize.pdf");
        new PdfExporter().Export(f, ours, new ExportOptions { ModelName = m.Name, IncludeReferences = true });

        var fit = PdfComparer.FitFromText(official, 0, ours);
        Assert.True(fit.HasValue, "no text registration between our PDF and the official plot");
        double s = fit!.Value.S;

        using var a = PdfDocument.Open(official);
        using var b = PdfDocument.Open(ours);
        static Dictionary<string, UglyToad.PdfPig.Content.Word> Unique(UglyToad.PdfPig.Content.Page p) =>
            p.GetWords().Where(w => w.Text.Length >= 4 && w.Text.All(char.IsLetter))
                .GroupBy(w => w.Text.ToUpperInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        var ua = Unique(a.GetPage(1)); var ub = Unique(b.GetPage(1));
        var pairs = ua.Keys.Where(ub.ContainsKey).Select(k => (A: ua[k], B: ub[k])).ToList();
        Assert.True(pairs.Count >= 10, $"only {pairs.Count} common words");

        var size = pairs.Select(p => p.A.Letters[0].PointSize / (s * p.B.Letters[0].PointSize * FontMetrics.HelveticaCapHeight)).ToList();
        var span = pairs.Select(p => (p.A.Letters[^1].StartBaseLine.X - p.A.Letters[0].StartBaseLine.X) /
                                     (s * (p.B.Letters[^1].StartBaseLine.X - p.B.Letters[0].StartBaseLine.X))).ToList();
        double Med(List<double> v) { v.Sort(); return v[v.Count / 2]; }
        double ms = Med(size), mw = Med(span);
        _out.WriteLine($"{pairs.Count} words, scale {s:0.0000}: official size / our cap height = {ms:0.000}, official / our word span = {mw:0.000}");
        Assert.InRange(ms, 0.95, 1.05);
        Assert.InRange(mw, 0.92, 1.08);
    }
}
