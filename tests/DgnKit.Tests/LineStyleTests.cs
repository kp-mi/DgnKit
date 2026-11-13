using System.Globalization;
using System.Text;
using System.Text.Json;
using ACadSharp.IO;
using DgnKit.Export;
using DgnKit.Graphics;
using DgnKit.Model;
using Xunit;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// Custom line style definitions (V8 table level 8): pattern flattening, corpus decoding, and comparison with
/// MicroStation's own DgnPlatform line style API (testdata/oracle/*.linestyles.json, written by
/// <c>tools/oracle/MstnOracle --linestyles</c>). Corpus/oracle-dependent tests skip when the data is absent.
/// </summary>
public class LineStyleTests
{
    private readonly ITestOutputHelper _out;
    public LineStyleTests(ITestOutputHelper o) { _out = o; }

    private static string? Corpus(string rel)
    {
        var p = Path.Combine(TestPaths.Corpus, rel);
        return File.Exists(p) ? p : null;
    }

    private static LineStyleComponent Code(params (bool dash, double len)[] strokes)
    {
        var c = new LineStyleComponent { Kind = LineStyleComponentKind.StrokePattern };
        foreach (var (d, l) in strokes) c.Strokes.Add(new LineStyleStroke { IsDash = d, Length = l, StrokeMode = (byte)(d ? 1 : 0) });
        return c;
    }

    [Fact]
    public void Flatten_MergesRunsAndStartsWithDash()
    {
        // dash 10, gap 10, gap 10, gap 10 → [10, 30]
        Assert.Equal(new[] { 10.0, 30.0 }, LineStyleDefinition.FlattenDashes(Code((true, 10), (false, 10), (false, 10), (false, 10)), out var ph));
        Assert.Equal(0, ph, 9);
        // leading gap rotates to the end; the line starts inside the gap → phase = dash length... measured in the new pattern
        var p = LineStyleDefinition.FlattenDashes(Code((false, 5), (true, 10)), out ph);
        Assert.Equal(new[] { 10.0, 5.0 }, p);
        Assert.Equal(10, ph, 9);
        // dash at both ends wraps into one run: 25 + 25 = 50, the element starts 25 into it
        p = LineStyleDefinition.FlattenDashes(Code((true, 25), (false, 8), (true, 58), (false, 8), (true, 25)), out ph);
        Assert.Equal(new[] { 50.0, 8, 58, 8 }, p);
        Assert.Equal(25, ph, 9);
        // continuous (no gaps) and invisible (no dashes) patterns are not dashed
        Assert.Null(LineStyleDefinition.FlattenDashes(Code((true, 10)), out _));
        Assert.Null(LineStyleDefinition.FlattenDashes(Code((false, 10)), out _));
    }

    [Fact]
    public void Flatten_AppliesFixedPhase()
    {
        var c = Code((true, 10), (false, 5));
        c.Phase = 3;
        var p = LineStyleDefinition.FlattenDashes(c, out var ph);
        Assert.Equal(LineStylePhaseMode.Fixed, c.PhaseMode);
        Assert.Equal(new[] { 10.0, 5.0 }, p);
        Assert.Equal(3, ph, 9);
    }

    [Fact]
    public void ExpectedDashes_FollowPatternAndPhase()
    {
        // 10 on / 30 off along 100, starting 5 into the pattern: [0,5] [35,45] [75,85]
        var d = ExpectedDashes(new[] { 10.0, 30 }, 5, 100);
        Assert.Equal(new[] { (0.0, 5.0), (35.0, 45.0), (75.0, 85.0) }, d);
        // phase inside the gap: first dash at 40 - 20 = 20
        Assert.Equal((20.0, 30.0), ExpectedDashes(new[] { 10.0, 30 }, 20, 100)[0]);
    }

    [Fact]
    public void Wsdot_DefinitionsDecode()
    {
        var src = Corpus("wsdot-XE3736/12_XL7089_PS_TC1.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        // 10' skip line, 30' gap, 8" wide (left half), UOR units at 10000 UOR/ft, physical
        var skip = f.LineStyles.FindByName("RD_MK_LineSkipCenterNew")!;
        Assert.True(skip.HasDefinition);
        Assert.Equal(LineStyleUnits.Uor, skip.Units);
        Assert.True(skip.UnitsArePhysical);
        Assert.Equal(LineStyleComponentKind.StrokePattern, skip.Root!.Kind);
        Assert.Equal(4, skip.Root.Strokes.Count);
        Assert.Equal(100000, skip.Root.Strokes[0].Length, 6);
        Assert.True(skip.Root.Strokes[0].IsDash);
        Assert.Equal(6666.67, skip.Root.Strokes[0].StartWidth, 6);
        Assert.Equal(new[] { 100000.0, 300000.0 }, skip.DashPattern);
        Assert.Equal(new[] { 10.0, 30.0 }, skip.DashPatternMaster!.Select(v => Math.Round(v, 9)));
        // compound: the same line code twice at ±1/3 ft
        var dbl = f.LineStyles.FindByName("RD_MK_LineDouble")!;
        Assert.Equal(LineStyleComponentKind.Compound, dbl.Root!.Kind);
        Assert.Equal(2, dbl.Root.Children.Count);
        Assert.Equal(3333.33, dbl.Root.ChildOffsets[0], 6);
        Assert.Equal(-3333.33, dbl.Root.ChildOffsets[1], 6);
        Assert.Same(dbl.Root.Children[0], dbl.Root.Children[1]);
        // compound with a line point whose symbol is a "Linestyle Definition Cell"
        var bar = f.LineStyles.FindByName("RD_BR_BarrierCenter")!;
        var lp = bar.Root!.Children.Single(c => c.Kind == LineStyleComponentKind.LinePoint);
        Assert.Equal(125, lp.LineCode!.PatternLength, 6);
        var sym = Assert.Single(lp.Symbols);
        Assert.Equal(LineStyleSymbolLocation.Stroke, sym.Location);
        Assert.Equal(LineStyleComponentKind.PointSymbol, sym.Symbol!.Kind);
        Assert.Equal(10000, sym.Symbol.SymbolScale, 6);
        Assert.NotEmpty(sym.Symbol.SymbolElements);
        // styles defined in an external dgnlib have no definition here
        Assert.False(f.LineStyles.FindByName("AA_TM_FullTerminator")!.HasDefinition);
    }

    [Fact]
    public void Wsdot_ElementStyleModifiersDecode()
    {
        var src = Corpus("wsdot-XE3736/12_XL7089_PS_TC1.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        var withScale = f.Models.SelectMany(m => m.AllElements()).Where(e => e.StyleParams != null).ToList();
        Assert.NotEmpty(withScale);
        // linkage 0x79F9 with modifiers 0x21 = scale + distance phase
        var phased = withScale.FirstOrDefault(e => e.StyleParams!.Modifiers == 0x21);
        if (phased != null)
        {
            Assert.Equal(1.0 / 1200, phased.StyleParams!.Scale, 9);
            Assert.True(phased.StyleParams.Phase > 0);
        }
        Assert.All(withScale, e => Assert.True(e.StyleParams!.Scale > 0));
    }

    [Fact]
    public void LinePointSymbols_ArePlacedAlongTheLine()
    {
        var src = Corpus("wsdot-XE3736/05_XL7089_PS_SP1.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        var m = f.Models.First(x => x.Name == "Untitled Sheet");
        var prims = DisplayListBuilder.Build(f, m, new DisplayListOptions { ModelName = m.Name });
        // RD_BR_FenceNew: compound of a dash pattern and a line point with an X tic at the centre of stroke 1 (the gap)
        var fences = prims.Where(p => p.Symbology.StyleName == "RD_BR_FenceNew" && p.Symbology.LineStyle != null && p.Kind == PrimitiveKind.Polyline).ToList();
        Assert.NotEmpty(fences);
        var line = fences[0];
        var symbols = prims.Where(p => p.Leaf == line.Leaf && p.Symbology.LineStyle == null).ToList();
        Assert.NotEmpty(symbols);
        double k = line.Symbology.LineStyleToMaster;
        var lc = LineStyleDefinition.MainStrokePattern(line.Symbology.LineStyle!.Root)!;
        double period = lc.PatternLength * k, len = 0;
        for (int i = 1; i < line.Points.Count; i++) len += line.Points[i].DistanceTo(line.Points[i - 1]);
        // one tic (two strokes of an X) per pattern repetition, drawn continuous in the line's colour
        Assert.InRange(symbols.Count / 2, (int)Math.Floor(len / period) - 1, (int)Math.Ceiling(len / period) + 1);
        Assert.All(symbols, s => { Assert.Equal(0, s.Symbology.StyleNumber); Assert.Null(s.Symbology.DashPattern); Assert.Equal(line.Symbology.Color, s.Symbology.Color); });
        // every tic is centred on the line: its midpoint lies within a tic size of the polyline
        double ticSize = 2 * 20.8334 * k;
        foreach (var s in symbols)
        {
            var mid = new DgnKit.Geometry.DPoint3d((s.Points[0].X + s.Points[^1].X) / 2, (s.Points[0].Y + s.Points[^1].Y) / 2);
            double dist = Enumerable.Range(1, line.Points.Count - 1).Min(i => DistToSegment(mid, line.Points[i - 1], line.Points[i]));
            Assert.True(dist < ticSize, $"symbol {dist} away from the line (tic {ticSize})");
        }
    }

    private static double DistToSegment(DgnKit.Geometry.DPoint3d p, DgnKit.Geometry.DPoint3d a, DgnKit.Geometry.DPoint3d b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2)) : 0;
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    [Fact]
    public void Dxf_CustomLineTypesHaveSegments()
    {
        var src = Corpus("wsdot-XE3691/XL7087_PS GR1.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        var outPath = Path.Combine(Directory.CreateDirectory(Path.Combine(TestPaths.Reports, "exports")).FullName, "linestyles-GR1.dxf");
        var model = f.Models.FirstOrDefault(m => DisplayListBuilder.Build(f, m, new DisplayListOptions { ModelName = m.Name })
            .Any(p => p.Symbology.DashPattern != null));
        if (model == null) return; // no element in the file uses a decoded custom style
        Exporters.Get("dxf").Export(f, outPath, new ExportOptions { ModelName = model.Name });
        using var reader = new DxfReader(outPath);
        var doc = reader.Read();
        var custom = doc.LineTypes.Where(lt => f.LineStyles.FindByName(lt.Name)?.DashPattern != null).ToList();
        Assert.NotEmpty(custom);
        foreach (var lt in custom)
        {
            Assert.True(lt.Segments.Count() >= 2, $"{lt.Name}: no segments");
            Assert.Contains(lt.Segments, s => s.Length < 0);
            _out.WriteLine($"{lt.Name}: " + string.Join(", ", lt.Segments.Select(s => s.Length.ToString("G6", CultureInfo.InvariantCulture))));
        }
    }

    // ---------------------------------------------------------------- oracle comparison

    public static IEnumerable<string> OracleDumps() =>
        Directory.Exists(TestPaths.Oracle) ? Directory.EnumerateFiles(TestPaths.Oracle, "*.linestyles.json").OrderBy(x => x) : Enumerable.Empty<string>();

    private static string? DgnForLineStyleOracle(string json)
    {
        string name = Path.GetFileName(json).Replace(".linestyles.json", "", StringComparison.OrdinalIgnoreCase);
        return TestPaths.DgnForOracle(name + ".oracle.json");
    }

    private sealed class Tally
    {
        public readonly Dictionary<string, (int ok, int n)> Fields = new();
        public readonly List<string> Mismatches = new();
        public void Add(string field, bool ok, string detail)
        {
            var (a, b) = Fields.TryGetValue(field, out var v) ? v : (0, 0);
            Fields[field] = (a + (ok ? 1 : 0), b + 1);
            if (!ok && Mismatches.Count < 60) Mismatches.Add($"{field}: {detail}");
        }
        public double Rate(string f) => Fields.TryGetValue(f, out var v) && v.n > 0 ? (double)v.ok / v.n : 1;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static void CompareComponent(Tally t, string path, JsonElement o, LineStyleComponent? c, int depth = 0)
    {
        if (depth > 8) return;
        int otype = o.GetProperty("type").GetInt32();
        var expectKind = otype switch
        {
            1 => LineStyleComponentKind.PointSymbol, 2 => LineStyleComponentKind.Compound, 3 => LineStyleComponentKind.StrokePattern,
            4 => LineStyleComponentKind.LinePoint, 6 => LineStyleComponentKind.Internal, _ => LineStyleComponentKind.Unknown,
        };
        t.Add("component.kind", c != null && c.Kind == expectKind, $"{path}: oracle {expectKind}, dgnkit {c?.Kind}");
        if (c == null) return;
        if (o.TryGetProperty("strokes", out var strokes))
        {
            int n = strokes.GetArrayLength();
            t.Add("linecode.strokeCount", n == c.Strokes.Count, $"{path}: oracle {n}, dgnkit {c.Strokes.Count}");
            for (int i = 0; i < Math.Min(n, c.Strokes.Count); i++)
            {
                var os = strokes[i]; var ds = c.Strokes[i];
                string p = $"{path}/stroke[{i}]";
                t.Add("stroke.length", Near(os.GetProperty("length").GetDouble(), ds.Length), $"{p}: oracle {os.GetProperty("length").GetDouble():R}, dgnkit {ds.Length:R}");
                t.Add("stroke.isDash", os.GetProperty("isDash").GetBoolean() == ds.IsDash, $"{p}");
                t.Add("stroke.startWidth", Near(os.GetProperty("startWidth").GetDouble(), ds.StartWidth), $"{p}: oracle {os.GetProperty("startWidth").GetDouble():R}, dgnkit {ds.StartWidth:R}");
                t.Add("stroke.endWidth", Near(os.GetProperty("endWidth").GetDouble(), ds.EndWidth), $"{p}: oracle {os.GetProperty("endWidth").GetDouble():R}, dgnkit {ds.EndWidth:R}");
                t.Add("stroke.stretchable", os.GetProperty("stretchable").GetBoolean() == ds.IsStretchable, p);
                t.Add("stroke.invert", os.GetProperty("invertStart").GetBoolean() == ds.InvertStart && os.GetProperty("invertEnd").GetBoolean() == ds.InvertEnd, p);
                t.Add("stroke.bypassCorner", os.GetProperty("bypassCorner").GetBoolean() == ds.BypassCorner, p);
                t.Add("stroke.widthMode", os.GetProperty("leftWidth").GetBoolean() == ds.HasLeftWidth && os.GetProperty("rightWidth").GetBoolean() == ds.HasRightWidth,
                    $"{p}: oracle L{os.GetProperty("leftWidth").GetBoolean()} R{os.GetProperty("rightWidth").GetBoolean()}, dgnkit mode {ds.WidthMode}");
                t.Add("stroke.capMode", os.GetProperty("capMode").GetInt32() == ds.CapMode, $"{p}: oracle {os.GetProperty("capMode").GetInt32()}, dgnkit {ds.CapMode}");
            }
            if (o.TryGetProperty("phaseMode", out var pm))
                t.Add("linecode.phaseMode", pm.GetInt32() == (int)c.PhaseMode, $"{path}: oracle {pm.GetInt32()}, dgnkit {c.PhaseMode}");
            if (o.TryGetProperty("phase", out var ph) && c.PhaseMode != LineStylePhaseMode.Center)
                t.Add("linecode.phase", Near(ph.GetDouble(), c.Phase), $"{path}: oracle {ph.GetDouble():R}, dgnkit {c.Phase:R}");
            if (o.TryGetProperty("iterationLimit", out var il))
                t.Add("linecode.iterationLimit", il.GetBoolean() == c.HasIterationLimit && (!il.GetBoolean() || o.GetProperty("iterations").GetInt32() == c.MaxIterate), path);
            if (o.TryGetProperty("singleSegment", out var ss))
                t.Add("linecode.singleSegment", ss.GetBoolean() == c.IsSingleSegment, path);
        }
        if (o.TryGetProperty("components", out var comps))
        {
            int n = comps.GetArrayLength();
            t.Add("compound.count", n == c.Children.Count, $"{path}: oracle {n}, dgnkit {c.Children.Count}");
            for (int i = 0; i < Math.Min(n, c.Children.Count); i++)
            {
                t.Add("compound.offset", Near(comps[i].GetProperty("offset").GetDouble(), c.ChildOffsets[i]), $"{path}/comp[{i}]");
                CompareComponent(t, $"{path}/comp[{i}]", comps[i], c.Children[i], depth + 1);
            }
        }
        if (o.TryGetProperty("symbols", out var syms))
        {
            if (o.TryGetProperty("lineCode", out var lc) && lc.ValueKind == JsonValueKind.Object) CompareComponent(t, path + "/lineCode", lc, c.LineCode, depth + 1);
            int n = syms.GetArrayLength();
            t.Add("linepoint.symbolCount", n == c.Symbols.Count, $"{path}: oracle {n}, dgnkit {c.Symbols.Count}");
            for (int i = 0; i < Math.Min(n, c.Symbols.Count); i++)
            {
                var os = syms[i]; var ds = c.Symbols[i];
                string p = $"{path}/sym[{i}]";
                t.Add("symbol.strokeNumber", os.GetProperty("strokeNumber").GetInt32() == ds.StrokeNumber, p);
                t.Add("symbol.location", os.GetProperty("location").GetInt32() == (int)ds.Location, $"{p}: oracle {os.GetProperty("location").GetInt32()}, dgnkit {ds.Location}");
                if (ds.Location == LineStyleSymbolLocation.Stroke)
                    t.Add("symbol.strokeLocation", os.GetProperty("strokeLocation").GetInt32() == ds.StrokeLocation, p);
                t.Add("symbol.offset", Near(os.GetProperty("xOffset").GetDouble(), ds.XOffset) && Near(os.GetProperty("yOffset").GetDouble(), ds.YOffset), p);
                double oa = os.GetProperty("angle").GetDouble();
                t.Add("symbol.angle", Near(oa, ds.Angle) || Near(oa, ds.Angle * 180 / Math.PI), $"{p}: oracle {oa:R}, dgnkit {ds.Angle:R} rad");
                if (os.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.Object) CompareComponent(t, p + "/symbol", s, ds.Symbol, depth + 1);
            }
        }
        if (o.TryGetProperty("unitScale", out var us))
            t.Add("symbol.scale", Near(us.GetDouble(), c.SymbolScale), $"{path}: oracle {us.GetDouble():R}, dgnkit {c.SymbolScale:R}");
    }

    [Fact]
    public void Definitions_MatchMicroStation()
    {
        var t = new Tally();
        int files = 0, styles = 0;
        var sb = new StringBuilder("# DgnKit line style definitions vs MicroStation (DgnPlatform line style API)\n\n");
        foreach (var json in OracleDumps())
        {
            var dgn = DgnForLineStyleOracle(json);
            if (dgn == null) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (!doc.RootElement.TryGetProperty("lineStyles", out var arr)) continue;
            var f = DgnFile.Open(dgn);
            files++;
            foreach (var o in arr.EnumerateArray())
            {
                if (!o.TryGetProperty("root", out var root) || root.ValueKind != JsonValueKind.Object) continue;
                int number = o.GetProperty("number").GetInt32();
                if (number >= 0 && number <= 7) continue; // built-in cosmetic styles (not stored in the file)
                string name = o.GetProperty("name").GetString() ?? "";
                var def = f.LineStyles.Find(number) ?? f.LineStyles.FindByName(name);
                t.Add("style.found", def != null, $"{Path.GetFileName(dgn)}: {number} '{name}'");
                if (def == null) continue;
                // styles MicroStation resolves from external libraries (no definition in this file) are not compared
                if (!def.HasDefinition) { t.Add("style.definitionInFile", false, $"{Path.GetFileName(dgn)}: '{name}' resolved by MicroStation from {o}"); continue; }
                styles++;
                t.Add("style.name", def.Name == name, $"'{def.Name}' vs '{name}'");
                if (o.TryGetProperty("unitsType", out var ut)) t.Add("style.units", ut.GetInt32() == (int)def.Units, $"{name}: oracle {ut.GetInt32()}, dgnkit {def.Units}");
                if (o.TryGetProperty("physical", out var ph)) t.Add("style.physical", ph.GetBoolean() == def.UnitsArePhysical, name);
                CompareComponent(t, $"{Path.GetFileName(dgn)}:{name}", root, def.Root);
            }
        }
        if (files == 0) return; // oracle not generated on this machine
        sb.Append($"{files} files, {styles} styles with definitions in the file.\n\n| field | match | n |\n|---|---|---|\n");
        foreach (var kv in t.Fields.OrderBy(k => k.Key)) sb.Append($"| {kv.Key} | {(double)kv.Value.ok / kv.Value.n:P1} | {kv.Value.n} |\n");
        if (t.Mismatches.Count > 0) sb.Append("\nMismatches (first 60):\n\n").Append(string.Join("\n", t.Mismatches.Select(m => "- " + m))).Append('\n');
        File.WriteAllText(Path.Combine(TestPaths.Reports, "linestyle-report.md"), sb.ToString());
        _out.WriteLine(sb.ToString());
        Assert.True(t.Rate("style.found") >= 0.99, "style.found");
        Assert.True(t.Rate("component.kind") >= 0.99, "component.kind");
        Assert.True(t.Rate("linecode.strokeCount") >= 0.99, "linecode.strokeCount");
        Assert.True(t.Rate("stroke.length") >= 0.99, "stroke.length");
        Assert.True(t.Rate("stroke.isDash") >= 0.99, "stroke.isDash");
        Assert.True(t.Rate("stroke.startWidth") >= 0.99, "stroke.startWidth");
        Assert.True(t.Rate("compound.offset") >= 0.99, "compound.offset");
    }

    [Fact]
    public void ModelLineStyleScale_MatchesMicroStation()
    {
        int n = 0;
        foreach (var json in OracleDumps())
        {
            var dgn = DgnForLineStyleOracle(json);
            if (dgn == null) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (!doc.RootElement.TryGetProperty("models", out var models)) continue;
            var f = DgnFile.Open(dgn);
            foreach (var m in models.EnumerateObject())
            {
                if (!m.Value.TryGetProperty("lineStyleScale", out var ls)) continue;
                var dm = f.Models.FirstOrDefault(x => x.Name == m.Name);
                Assert.True(dm != null, $"{Path.GetFileName(dgn)}: model '{m.Name}' not found");
                Assert.True(Math.Abs(ls.GetDouble() - dm!.LineStyleScale) < 1e-9 * Math.Max(1, ls.GetDouble()),
                    $"{Path.GetFileName(dgn)} '{m.Name}': MicroStation line style scale {ls.GetDouble()}, DgnKit {dm.LineStyleScale}");
                if (m.Value.TryGetProperty("annotationScale", out var an))
                    Assert.Equal(an.GetDouble(), dm.AnnotationScale, 9);
                n++;
            }
        }
        _out.WriteLine($"{n} models compared");
    }

    // ---------------------------------------------------------------- stroked geometry (scale / phase)

    private static List<double[]> OraclePoints(JsonElement curves)
    {
        var pts = new List<double[]>();
        foreach (var c in curves.EnumerateArray())
        {
            if (c.TryGetProperty("points", out var ps))
                foreach (var p in ps.EnumerateArray()) pts.Add(new[] { p[0].GetDouble(), p[1].GetDouble() });
            else if (c.TryGetProperty("start", out var a) && c.TryGetProperty("end", out var b))
            {
                pts.Add(new[] { a[0].GetDouble(), a[1].GetDouble() });
                pts.Add(new[] { b[0].GetDouble(), b[1].GetDouble() });
            }
        }
        return pts;
    }

    /// <summary>Arc-length position along a polyline of the closest point to <paramref name="p"/>, and the distance.</summary>
    private static (double s, double d) Project(List<double[]> line, double[] p)
    {
        double best = double.MaxValue, bs = 0, acc = 0;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1][0], ay = line[i - 1][1], dx = line[i][0] - ax, dy = line[i][1] - ay, l2 = dx * dx + dy * dy, l = Math.Sqrt(l2);
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((p[0] - ax) * dx + (p[1] - ay) * dy) / l2)) : 0;
            double ex = ax + t * dx - p[0], ey = ay + t * dy - p[1], d = Math.Sqrt(ex * ex + ey * ey);
            if (d < best - 1e-9) { best = d; bs = acc + t * l; }
            acc += l;
        }
        return (bs, best);
    }

    /// <summary>Dash intervals [start, end] along a line of length <paramref name="len"/> for a dash/gap pattern and phase.</summary>
    internal static List<(double a, double b)> ExpectedDashes(double[] pattern, double phase, double len, int max = 5000)
    {
        var res = new List<(double, double)>();
        double total = pattern.Sum();
        if (total <= 0) return res;
        double pos = -(((phase % total) + total) % total);
        for (int i = 0; pos < len && res.Count < max; i = (i + 1) % pattern.Length)
        {
            double a = pos, b = pos + pattern[i];
            if (i % 2 == 0 && b > 0) res.Add((Math.Max(0, a), Math.Min(len, b)));
            pos = b;
        }
        return res;
    }

    /// <summary>
    /// MicroStation's stroked ("dropped") line style geometry vs DgnKit's resolved dash pattern for sample elements: the
    /// dashes of the stroke pattern nearest the element's own path (DgnKit's main pattern) are projected onto the path and
    /// compared as intervals. Checks the definition → master scale (units, element scale) and the phase.
    /// </summary>
    [Fact]
    public void DroppedStrokes_MatchMicroStation()
    {
        int samples = 0, compared = 0, lenN = 0, lenOk = 0, posOk = 0, dashesSeen = 0, solidOk = 0;
        var sb = new StringBuilder("# DgnKit dash patterns vs MicroStation stroked line style geometry\n\n");
        sb.Append("| file | element | style | DgnKit pattern (UOR) | phase | MicroStation dashes | interior length match | position match |\n|---|---|---|---|---|---|---|---|\n");
        var notes = new List<string>();
        foreach (var json in OracleDumps())
        {
            var dgn = DgnForLineStyleOracle(json);
            if (dgn == null) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (!doc.RootElement.TryGetProperty("samples", out var arr)) continue;
            var f = DgnFile.Open(dgn);
            string? activeModel = doc.RootElement.TryGetProperty("model", out var mn) ? mn.GetString() : null;
            // custom-styled polylines of each sampled model, by element id
            var cache = new Dictionary<string, (double upm, Dictionary<ulong, Primitive> byLeaf)>();
            (double upm, Dictionary<ulong, Primitive> byLeaf) ModelPrims(string? name)
            {
                name ??= activeModel ?? "";
                if (cache.TryGetValue(name, out var c)) return c;
                var model = f.Models.FirstOrDefault(m => m.Name == name) ?? f.DefaultModel!;
                var prims = DisplayListBuilder.Build(f, model, new DisplayListOptions { ModelName = model.Name });
                return cache[name] = (model.Units.UorPerMaster, prims.Where(p => p.Kind == PrimitiveKind.Polyline && p.Symbology.LineStyle != null)
                    .GroupBy(p => p.Leaf.Id).ToDictionary(g => g.Key, g => g.First()));
            }
            foreach (var o in arr.EnumerateArray())
            {
                if (!o.TryGetProperty("dropped", out var dropped) || !o.TryGetProperty("source", out var source)) continue;
                samples++;
                ulong id = o.GetProperty("elementId").GetUInt64();
                string file = Path.GetFileName(dgn);
                var (upm, byLeaf) = ModelPrims(o.TryGetProperty("model", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : null);
                if (!byLeaf.TryGetValue(id, out var prim)) { notes.Add($"{file} {id}: no DgnKit primitive with a custom style"); continue; }
                var s = prim.Symbology;
                var path = OraclePoints(source);
                // de-duplicate consecutive vertices
                path = path.Where((p, i) => i == 0 || Math.Abs(p[0] - path[i - 1][0]) + Math.Abs(p[1] - path[i - 1][1]) > 1e-9).ToList();
                if (path.Count < 2) continue;
                double len = 0;
                for (int i = 1; i < path.Count; i++) len += Math.Sqrt(Math.Pow(path[i][0] - path[i - 1][0], 2) + Math.Pow(path[i][1] - path[i - 1][1], 2));
                // dropped pieces → (mean offset from the path, interval along it)
                var pieces = new List<(double off, double a, double b)>();
                foreach (var d in dropped.EnumerateArray())
                {
                    if (!d.TryGetProperty("curves", out var cv) || d.GetProperty("type").GetInt32() == 2) continue;
                    var pts = OraclePoints(cv);
                    if (pts.Count < 2) continue;
                    var pr = pts.Select(p => Project(path, p)).ToList();
                    pieces.Add((pr.Average(x => x.d), pr.Min(x => x.s), pr.Max(x => x.s)));
                }
                string style = s.StyleName ?? s.LineStyle?.Name ?? "?";
                // the pieces of the pattern nearest the path; touching pieces (split at vertices) merge into one dash
                List<(double a, double b)> Merge(double tolerance)
                {
                    double width = s.LineStyleWidth * upm;
                    double offMin = pieces.Count > 0 ? pieces.Min(p => p.off) : 0;
                    var near = pieces.Where(p => p.off <= offMin + Math.Max(tolerance, width) + 1e-6)
                        .Select(p => (p.a, p.b)).Where(p => p.b - p.a > 1e-6).OrderBy(p => p.a).ToList();
                    var m = new List<(double a, double b)>();
                    foreach (var dsh in near)
                        if (m.Count > 0 && dsh.a <= m[^1].b + tolerance * 0.1) m[^1] = (m[^1].a, Math.Max(m[^1].b, dsh.b));
                        else m.Add(dsh);
                    return m;
                }
                if (s.DashPattern == null)
                {
                    // MicroStation strokes wide continuous styles into abutting pieces: fine if they form one run
                    var run = Merge(1e-6 * len + 1e-3);
                    bool continuous = run.Count == 1 && run[0].a <= 1e-3 * len && run[0].b >= len * (1 - 1e-3);
                    sb.Append($"| {file} | {id} | {style} | solid | | {pieces.Count} pieces → {run.Count} run(s) | {(continuous ? "yes (continuous)" : "NO")} | |\n");
                    if (!continuous) notes.Add($"{file} {id} '{style}': DgnKit draws it solid; MicroStation's {pieces.Count} pieces form {run.Count} runs (gaps)");
                    else solidOk++;
                    continue;
                }
                var patUor = s.DashPattern.Select(v => v * upm).ToArray();
                double phaseUor = s.DashPhase * upm, total = patUor.Sum();
                double tol = Math.Max(1e-6 * len, 0.01 * patUor.Where((v, i) => i % 2 == 0).Min());
                var merged = Merge(tol);
                var expected = ExpectedDashes(patUor, phaseUor, len);
                compared++;
                dashesSeen += merged.Count;
                // interior (not clipped by the ends) dash lengths must be one of the pattern's dash lengths
                var dashLens = patUor.Where((v, i) => i % 2 == 0).ToArray();
                var interior = merged.Where(m => m.a > tol && m.b < len - tol).ToList();
                bool lengthsMatch = interior.Count > 0 && interior.All(m => dashLens.Any(l => Math.Abs(m.b - m.a - l) <= tol));
                // full interval sequence match (phase); a truncated drop list (oracle cap) only has to be a subset
                bool truncated = o.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True;
                bool positionsMatch = truncated
                    ? merged.Count > 0 && merged.All(m => expected.Any(e => Math.Abs(m.a - e.a) <= tol && Math.Abs(m.b - e.b) <= tol))
                    : merged.Count == expected.Count &&
                      merged.Zip(expected, (m, e) => Math.Abs(m.a - e.a) <= tol && Math.Abs(m.b - e.b) <= tol).All(x => x);
                if (interior.Count > 0) lenN++;
                if (lengthsMatch) lenOk++;
                if (positionsMatch) posOk++;
                string Fmt(IEnumerable<double> v) => string.Join(" ", v.Take(6).Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)));
                sb.Append($"| {file} | {id} | {style} | {Fmt(patUor)} | {phaseUor.ToString("0.###", CultureInfo.InvariantCulture)} | " +
                          $"{merged.Count}: {Fmt(merged.Select(m => m.b - m.a))} (first at {(merged.Count > 0 ? merged[0].a : 0).ToString("0.###", CultureInfo.InvariantCulture)}) | " +
                          $"{(interior.Count == 0 ? "n/a" : lengthsMatch ? "yes" : "NO")} | {(positionsMatch ? "yes" : $"NO (expected {expected.Count}, first at {(expected.Count > 0 ? expected[0].a : 0).ToString("0.###", CultureInfo.InvariantCulture)})")} |\n");
            }
        }
        if (samples == 0) return; // no oracle samples on this machine
        sb.Append($"\n{samples} samples: {compared} dashed ({lenN} with interior dashes) — interior dash lengths match in {lenOk}, " +
                  $"full dash positions (phase) in {posOk}; {solidOk} drawn solid where MicroStation's stroked pieces form one continuous run.\n");
        if (notes.Count > 0) sb.Append("\nNotes:\n\n").Append(string.Join("\n", notes.Take(80).Select(n => "- " + n))).Append('\n');
        File.WriteAllText(Path.Combine(TestPaths.Reports, "linestyle-drop-report.md"), sb.ToString());
        _out.WriteLine(sb.ToString());
        if (lenN == 0) return;
        // the scale (units × element scale × model line style scale) and the phase must be right for nearly every sample
        Assert.True(lenOk >= 0.95 * lenN, $"interior dash lengths match for {lenOk}/{lenN} samples (see linestyle-drop-report.md)");
        Assert.True(posOk >= 0.9 * compared, $"dash positions match for {posOk}/{compared} samples (see linestyle-drop-report.md)");
    }
}
