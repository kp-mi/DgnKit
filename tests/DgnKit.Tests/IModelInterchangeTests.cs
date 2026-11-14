using System.Text.Json;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.Graphics;
using DgnKit.IModel.Dgn;
using DgnKit.IModel.Imx;
using DgnKit.Model;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// Contract tests for the iModel interchange (docs/IMODEL-INTERCHANGE.md) — the part of the DGN → iModel path that
/// runs without Node.js: record order and key references, determinism (stable checksums drive change detection),
/// unit conversion against the display list, and coverage of the source elements. Corpus-dependent; skipped without it.
/// </summary>
public class IModelInterchangeTests
{
    private readonly ITestOutputHelper _out;
    public IModelInterchangeTests(ITestOutputHelper o) { _out = o; }

    public static IEnumerable<object[]> SampleFiles() => new[]
    {
        new object[] { "txdot-tcp-1-4-26/tcp(1-4)-26.dgn" },          // MicroStation 2025 sheet
        new object[] { "txdot-mb96/MB96.DGN" },                       // V7
        new object[] { "njdot-traffic/Traffic Construction Details DGN file set__104_TCD-2ConstructionBarrier.dgn" }, // V8 2004
        new object[] { "wsdot-XE3736/07_XL7089_PS_RS2.dgn" },        // ORD sheet (US survey feet; no GCS)
        new object[] { "gdal-dgnv8/test_dgnv8.dgn" },                 // ODA-written V8, 3D
        new object[] { "fdot-102-600/102-600-5of11.dgn" },            // CONNECT, dimensions
    };

    private static string? Corpus(string rel)
    {
        var p = Path.Combine(TestPaths.Corpus, rel);
        return File.Exists(p) ? p : null;
    }

    private static (byte[] Bytes, DgnImxReport Report) Export(DgnFile f, DgnImxOptions? o = null)
    {
        using var ms = new MemoryStream();
        DgnImxReport rep;
        using (var w = new ImxWriter(ms)) rep = new DgnImxExporter(f, o).Write(w);
        return (ms.ToArray(), rep);
    }

    private static List<JsonElement> Records(byte[] bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Records_AreOrdered_AndKeysResolve(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var (bytes, rep) = Export(DgnFile.Open(src));
        _out.WriteLine(rep.ToString());
        var recs = Records(bytes);
        Assert.Equal("header", recs[0].GetProperty("kind").GetString());
        Assert.Equal("dgnkit-imx", recs[0].GetProperty("format").GetString());

        var order = new[] { "header", "font", "lineStyle", "category", "subCategory", "model", "part", "element" };
        int last = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in recs)
        {
            string kind = r.GetProperty("kind").GetString()!;
            int idx = Array.IndexOf(order, kind);
            Assert.True(idx >= last, $"record '{kind}' out of order");
            last = idx;
            if (r.TryGetProperty("key", out var k)) Assert.True(keys.Add(k.GetString()!), $"duplicate key {k}");
            if (r.TryGetProperty("source", out var s) && kind != "header")
                Assert.True(identities.Add(kind + "|" + s.GetProperty("kind").GetString() + "|" + s.GetProperty("id").GetString()), $"duplicate source identity in {r}");
            if (kind != "element") continue;

            Assert.Contains(r.GetProperty("model").GetString()!, keys);
            Assert.Contains(r.GetProperty("category").GetString()!, keys);
            if (r.TryGetProperty("parent", out var parent)) Assert.Contains(parent.GetString()!, keys);
            int prims = 0;
            foreach (var g in r.GetProperty("geometry").EnumerateArray())
            {
                var entry = g.EnumerateObject().Single();
                switch (entry.Name)
                {
                    case "appearance":
                        if (entry.Value.TryGetProperty("style", out var st) && st.GetString() != "ls:0") Assert.Contains(st.GetString()!, keys);
                        break;
                    case "textString":
                        Assert.Contains(entry.Value.GetProperty("font").GetString()!, keys);
                        Assert.True(entry.Value.GetProperty("height").GetDouble() > 0);
                        prims++;
                        break;
                    case "fill": case "styleMod": break;
                    default: prims++; break;
                }
            }
            Assert.True(prims > 0, $"element without geometry: {r.GetProperty("key")}");
        }
    }

    /// <summary>Same input → byte-identical output: element checksums only change when the source changes.</summary>
    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Export_IsDeterministic(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var a = Export(DgnFile.Open(src)).Bytes;
        var b = Export(DgnFile.Open(src)).Bytes;
        Assert.Equal(a, b);
    }

    /// <summary>
    /// Unit conversion and transforms: the IMX model extents (metres) must match the display list of the same model
    /// (master units × metres per master unit), which the PDF/DXF exporters are verified against.
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Extents_MatchDisplayList(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var f = DgnFile.Open(src);
        var model = f.PrimaryModel!;
        var recs = Records(Export(f).Bytes);
        var m = recs.First(r => r.GetProperty("kind").GetString() == "model" && r.GetProperty("source").GetProperty("id").GetString() == model.Id.ToString());
        var lo = m.GetProperty("extents").GetProperty("low"); var hi = m.GetProperty("extents").GetProperty("high");

        var prims = DisplayListBuilder.Build(f, model, new DisplayListOptions { IncludeReferences = false, RespectLevelDisplay = false });
        var r = Range3d.Empty;
        foreach (var p in prims.Where(p => p.Kind is PrimitiveKind.Polyline or PrimitiveKind.Arc)) r.Extend(p.Range());
        Assert.False(r.IsEmpty);
        double mpm = model.Units.MetersPerMaster;
        double size = Math.Max(r.XSize, r.YSize) * mpm;
        double tol = Math.Max(0.02 * size, 1e-3);
        _out.WriteLine($"display list (m): {r.Low.X * mpm:F3},{r.Low.Y * mpm:F3} – {r.High.X * mpm:F3},{r.High.Y * mpm:F3}");
        _out.WriteLine($"imx extents  (m): {lo[0].GetDouble():F3},{lo[1].GetDouble():F3} – {hi[0].GetDouble():F3},{hi[1].GetDouble():F3}");
        // IMX may be larger (text, line style symbols are not in the display list's polylines) but must contain it
        Assert.True(lo[0].GetDouble() <= r.Low.X * mpm + tol && lo[1].GetDouble() <= r.Low.Y * mpm + tol, "IMX extents miss low corner");
        Assert.True(hi[0].GetDouble() >= r.High.X * mpm - tol && hi[1].GetDouble() >= r.High.Y * mpm - tol, "IMX extents miss high corner");
        // and must not be wildly larger (wrong scale / global origin)
        double imxSize = Math.Max(hi[0].GetDouble() - lo[0].GetDouble(), hi[1].GetDouble() - lo[1].GetDouble());
        Assert.InRange(imxSize, size * 0.98, size * 3 + 1);
    }

    /// <summary>MicroStation DISCONNECT vertices (DBL_MAX in UORs) separate runs; they are never positions.</summary>
    [Fact]
    public void DisconnectVertices_SplitRuns()
    {
        var d = new DPoint3d(DPoint3d.Disconnect, DPoint3d.Disconnect, 0);
        var runs = DPoint3d.SplitAtDisconnects(new[] { new DPoint3d(0, 0), new DPoint3d(1, 0), d, new DPoint3d(2, 0), new DPoint3d(3, 0), d });
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { new DPoint3d(0, 0), new DPoint3d(1, 0) }, runs[0]);
        Assert.Equal(new[] { new DPoint3d(2, 0), new DPoint3d(3, 0) }, runs[1]);
        Assert.Single(DPoint3d.SplitAtDisconnects(new[] { new DPoint3d(0, 0), new DPoint3d(1, 1) }));
        Assert.Empty(DPoint3d.SplitAtDisconnects(new[] { d }));
        Assert.True(new DPoint3d(0, 0, DPoint3d.Disconnect).IsDisconnect);
        Assert.False(new DPoint3d(1e300, 0, 0).IsDisconnect);

        // the detailing-symbol leader in CD-RAC-20 (line string 39307: two runs around one DISCONNECT vertex)
        var path = Corpus("txdot-CD-RAC-20/CD-RAC-20.dgn");
        if (path == null) return;
        var f = DgnFile.Open(path);
        var leader = f.Models.SelectMany(m => m.AllElements()).OfType<LineStringElement>().Single(e => e.Id == 39307);
        Assert.Single(leader.Points, p => p.IsDisconnect);
        var model = f.Models.First(m => m.AllElements().Any(e => e.Id == 39307));
        var prims = DisplayListBuilder.Build(f, model, new DisplayListOptions { IncludeReferences = false, RespectLevelDisplay = false })
            .Where(p => p.Leaf.Id == 39307).ToList();
        Assert.Equal(2, prims.Count);
        Assert.All(prims, p => Assert.All(p.Points, q => Assert.True(Math.Abs(q.X) < 1e7 && Math.Abs(q.Y) < 1e7, $"{q}")));
    }

    /// <summary>Every displayable top-level element of every model becomes an iModel element (or is reported as unsupported).</summary>
    [Fact]
    public void Corpus_EveryGraphicElementIsAccountedFor()
    {
        if (!Directory.Exists(TestPaths.Corpus)) return;
        int files = 0, eligible = 0, converted = 0;
        foreach (var path in Directory.EnumerateFiles(TestPaths.Corpus, "*.dgn", SearchOption.AllDirectories).OrderBy(p => p))
        {
            var f = DgnFile.Open(path);
            var (bytes, rep) = Export(f);
            // Every model fits on the Earth (a DISCONNECT vertex read as a point once put CD-RAC-20 at 4.6e303 m), and
            // no text has zero height (iTwin.js drops an element's whole GeometryStream for one).
            foreach (var m in Records(bytes).Where(r => r.GetProperty("kind").GetString() == "model" && r.TryGetProperty("extents", out _)))
                foreach (var corner in new[] { "low", "high" })
                    foreach (var v in m.GetProperty("extents").GetProperty(corner).EnumerateArray())
                        Assert.True(Math.Abs(v.GetDouble()) < 1e8, $"{Path.GetFileName(path)}: model '{m.GetProperty("name").GetString()}' extent {v.GetDouble()} m");
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.False(json.Contains("\"height\":0,") || json.Contains("\"height\":0}"), $"{Path.GetFileName(path)}: zero-height text in IMX");
            int e = f.Models.Where(m => !m.IsDictionary).SelectMany(m => m.Elements)
                .Count(x => x.IsGraphic && !x.IsDeleted && !x.IsInvisible && x is not SharedCellDefinition && !(x is TagElement t && !t.Displayed));
            int accounted = rep.Elements + rep.SkippedEmpty + rep.Unsupported.Values.Sum();
            Assert.True(accounted >= e, $"{Path.GetFileName(path)}: {accounted} accounted for, {e} eligible");
            eligible += e; converted += rep.Elements; files++;
        }
        _out.WriteLine($"{files} files: {converted}/{eligible} top-level graphic elements converted");
        if (files > 0) Assert.True(converted >= 0.97 * eligible, $"only {converted}/{eligible} converted");
    }
}
