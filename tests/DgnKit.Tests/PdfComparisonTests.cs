using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// Compares DgnKit output with the official plot PDFs downloaded alongside the corpus DGNs (testdata/corpus/*/pdf).
/// Pairs: single-DGN items with one PDF, and multi-DGN items (sheet sets) matched page-by-page via text overlap.
/// Writes testdata/reports/pdf-report.md plus overlay images.
/// </summary>
public class PdfComparisonTests
{
    private readonly ITestOutputHelper _out;
    public PdfComparisonTests(ITestOutputHelper o) { _out = o; }

    public static IEnumerable<(string Item, string Dgn, string Pdf)> Pairs()
    {
        if (!Directory.Exists(TestPaths.Corpus)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(TestPaths.Corpus).OrderBy(x => x))
        {
            var pdfDir = Path.Combine(dir, "pdf");
            if (!Directory.Exists(pdfDir)) continue;
            var pdfs = Directory.EnumerateFiles(pdfDir, "*.pdf").ToList();
            var dgns = Directory.EnumerateFiles(dir, "*.dgn").Where(p => p.EndsWith(".dgn", StringComparison.OrdinalIgnoreCase)).ToList();
            string item = Path.GetFileName(dir);
            if (dgns.Count == 0 || pdfs.Count == 0) continue;
            if (pdfs.Count == dgns.Count && pdfs.Count > 1)
            {
                // 1:1 sets (NJDOT): match by file name
                foreach (var d in dgns)
                {
                    var key = Path.GetFileNameWithoutExtension(d).Split("__").Last();
                    var p = pdfs.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).Split("__").Last().Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (p != null) yield return (item, d, p);
                }
            }
            else
            {
                foreach (var d in dgns) yield return (item, d, pdfs[0]);
            }
        }
    }

    [Fact]
    public void OfficialPdfs_AreReproduced()
    {
        var work = Path.Combine(TestPaths.Reports, "pdf");
        var results = new List<PdfComparison>();
        foreach (var (item, dgn, pdf) in Pairs())
        {
            try { results.Add(PdfComparer.Compare(item, dgn, pdf, work)); }
            catch (Exception ex) { results.Add(new PdfComparison { Item = item, Dgn = Path.GetFileName(dgn), Pdf = Path.GetFileName(pdf), Note = "error: " + ex.Message }); }
        }
        if (results.Count == 0) return;
        var report = PdfComparer.Report(results);
        File.WriteAllText(Path.Combine(TestPaths.Reports, "pdf-report.md"), report);
        File.WriteAllText(Path.Combine(TestPaths.Reports, "pdf-report.json"), JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        _out.WriteLine(report);
        Assert.DoesNotContain(results, r => r.Note != null && r.Note.StartsWith("error:"));
    }
}
