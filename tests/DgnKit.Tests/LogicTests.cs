using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DgnKit.IModel.Dgn;
using DgnKit.IModel.Logic;
using DgnKit.Logic.Dgn;
using DgnKit.Logic.Engine;
using DgnKit.Logic.Geometry;
using DgnKit.Logic.Interop;
using DgnKit.Logic.Json;
using DgnKit.Logic.Model;
using DgnKit.Logic.Query;
using DgnKit.Logic.Rules;
using DgnKit.Logic.Spatial;
using DgnKit.Model;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// The engineering logic layer (docs/LOGIC.md): parser / index / predicate unit tests, the synthetic building and
/// utilities fixtures (testdata/logic — every planted defect must be found, and nothing else), traces, determinism,
/// writers, IMX enrichment, and the DGN corpus (sign → mount, milepost → route; report in testdata/reports/logic-report.md).
/// </summary>
public class LogicTests
{
    private readonly ITestOutputHelper _out;
    public LogicTests(ITestOutputHelper o) { _out = o; }

    private sealed class Acc { public int Applicable, Satisfied, Violated, Unresolved, Implicit; }

    private static readonly Lazy<RuleSet> Rules = new(() => RuleSet.Builtin());
    private static string Fixture(string name) => Path.Combine(TestPaths.TestData, "logic", name);

    private static LogicResult RunFixture(string name) => LogicEngine.Run(GeoJsonFeatures.ReadFile(Fixture(name)), Rules.Value);

    private static HashSet<string> Subjects(LogicResult r, string rule, FindingStatus? status = null) =>
        r.Findings.Where(f => f.Rule == rule && (status == null || f.Status == status) && f.Subject != null).Select(f => f.Subject!.Key).ToHashSet();

    private static bool HasEdge(LogicResult r, string from, string rel, string to) =>
        r.Graph.Edges.Any(e => e.Relation.Name == rel && ((e.From.Key == from && e.To.Key == to) || (e.Relation.Symmetric && e.From.Key == to && e.To.Key == from)));

    // ------------------------------------------------------------------ building blocks

    [Fact]
    public void JsonReader_AcceptsCommentsTrailingCommasAndEscapes()
    {
        var o = (JsonObject)JsonReader.Parse("""
            // header
            { "a": 1.5e2, /* block */ "b": [true, false, null,], "c": "x\"yé\\d", }
            """)!;
        Assert.Equal(150.0, o.Num("a"));
        Assert.Equal(3, o.Arr("b").Count);
        Assert.Equal("x\"yé\\d", o.Str("c"));
        Assert.Throws<FormatException>(() => JsonReader.Parse("{\"a\": }"));
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("-2 + 5 % 3", 0.0)]
    [InlineData("max(1, 4, 2) - min(3, 1)", 3.0)]
    [InlineData("pow(2, 10)", 1024.0)]
    [InlineData("if(1 > 2, 10, 20)", 20.0)]
    [InlineData("2 > 1 ? 5 : 6", 5.0)]
    [InlineData("number('300 mm')", 300.0)]
    [InlineData("len('abc') + len([1, 2])", 5.0)]
    public void Expr_Arithmetic(string src, double expected) => Assert.Equal(expected, Expr.Parse(src).Number(new DictScope()));

    [Fact]
    public void Expr_ComparisonsRegexNullsAndMembers()
    {
        var s = new DictScope().Set("layer", "TR_SN_ConstructionSigns").Set("d", 0.2).Set("obj", new Dictionary<string, object?> { ["Diameter"] = 0.3 });
        Assert.True(Expr.Parse("layer ~ '^tr_sn_'").Test(s));                          // case-insensitive regex
        Assert.True(Expr.Parse("layer !~ 'Mount'").Test(s));
        Assert.True(Expr.Parse("d < obj.diameter and obj.diameter == 0.3").Test(s));    // member names case-insensitive
        Assert.False(Expr.Parse("missing > 1").Test(s));                                // null compares false …
        Assert.True(Expr.Parse("missing == null and not exists(missing)").Test(s));     // … but == null works
        Assert.Null(Expr.Parse("missing + 1").Evaluate(s));                             // arithmetic with null is null
        Assert.True(Expr.Parse("'W20' in ['W20', 'R2']").Test(s));
        Assert.Equal("W20-7", Expr.Parse("capture('Signs_W20-7_48x48', '([WRGIEM]\\d{1,3}(-\\d+)?)')").Evaluate(s));
        Assert.True(Expr.Parse("anyMatch(['Level 1', 'TR_WZ_PortableSignMountBlk'], '^TR_WZ_Portable')").Test(s));
        Assert.Throws<FormatException>(() => Expr.Parse("1 +"));
    }

    [Fact]
    public void StrTree_MatchesBruteForce()
    {
        var rnd = new Random(42);
        var entries = new List<(int, Box3)>();
        for (int i = 0; i < 5000; i++)
        {
            double x = rnd.NextDouble() * 1000, y = rnd.NextDouble() * 1000, w = rnd.NextDouble() * 5, h = rnd.NextDouble() * 5;
            entries.Add((i, new Box3(new Vec3(x, y, 0), new Vec3(x + w, y + h, 1))));
        }
        var tree = new StrTree(entries);
        for (int q = 0; q < 200; q++)
        {
            double x = rnd.NextDouble() * 1000, y = rnd.NextDouble() * 1000;
            var win = new Box3(new Vec3(x, y, 0), new Vec3(x + 20, y + 20, 0));
            var expected = entries.Where(e => e.Item2.Intersects(win, false)).Select(e => e.Item1).OrderBy(i => i).ToList();
            Assert.Equal(expected, tree.Query(win).OrderBy(i => i).ToList());
        }
    }

    [Fact]
    public void GeoOps_DistancesCrossingsAndRings()
    {
        var a = new FeatureGeometry(); a.Parts.Add(GeometryPart.Line(new[] { new Vec3(0, 0), new Vec3(10, 0) }));
        var b = new FeatureGeometry(); b.Parts.Add(GeometryPart.Line(new[] { new Vec3(5, -5), new Vec3(5, 5) }));
        var c = new FeatureGeometry(); c.Parts.Add(GeometryPart.Line(new[] { new Vec3(0, 3), new Vec3(10, 3) }));
        var ring = new FeatureGeometry(); ring.Parts.Add(GeometryPart.Ring(new[] { new Vec3(-1, -1), new Vec3(1, -1), new Vec3(1, 1), new Vec3(-1, 1) }));
        Assert.Equal(0, GeoOps.Distance(a, b, false));
        Assert.True(GeoOps.Crosses2D(a, b));
        Assert.Equal(3, GeoOps.Distance(a, c, false), 9);
        Assert.False(GeoOps.Crosses2D(a, c));
        Assert.Equal(0, GeoOps.PointToGeometry(new Vec3(0.2, 0.3), ring, false, out _));     // inside the ring
        Assert.Equal(0, GeoOps.AngleBetween(a, c), 6);
        Assert.Equal(90, GeoOps.AngleBetween(a, b), 6);
        Assert.Equal(Math.Sqrt(2), GeoOps.SegmentSegment(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 1), new Vec3(1, 1, 1), true), 9); // parallel, offset (0,1,1)
        Assert.Equal(1.0, GeoOps.SegmentSegment(new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(1, -1, 1), new Vec3(1, 1, 1), true), 9);       // skew, 1 apart
    }

    [Fact]
    public void BuiltinPacks_LoadAndReferenceOnlyKnownNames()
    {
        var rs = Rules.Value;
        Assert.Contains("core", rs.Packs.First());
        foreach (var p in new[] { "buildings", "storm", "sewer", "water", "gas", "electric", "roadway", "wsdot-workzone", "lrs-gis" })
            Assert.Contains(rs.Packs, x => x.StartsWith(p + "@", StringComparison.Ordinal));
        Assert.Empty(rs.Errors);
        Assert.True(rs.AllRules.Count() > 120, $"{rs.AllRules.Count()} rules");
        Assert.True(rs.Ontology.FindClass("Gas.Pipe.Main")!.IsA(rs.Ontology.FindClass("Util.Pipe")!)); // trait
        // a pack loaded on its own pulls in the core vocabulary
        var only = RuleSet.FromPaths(new[] { Path.Combine(TestPaths.RepoRoot, "rules", "gas.rules.json") });
        Assert.NotNull(only.Ontology.FindRelation("tapsInto"));
    }

    [Fact]
    public void Classification_DescendsTheTaxonomy()
    {
        // a sign cell whose components sit on a mount level: the sign evidence wins over the deeper mount class
        var rs = new RuleSet();
        rs.Load("""
            { "dgnkit-rules": 1, "name": "t",
              "classes": { "Road.Sign": {}, "Road.SignSupport.PortableMount": {} },
              "classify": [
                { "id": "s", "class": "Road.Sign", "when": "cell ~ '^Signs_'", "confidence": 0.9 },
                { "id": "m", "class": "Road.SignSupport.PortableMount", "when": "layer ~ 'Mount'", "confidence": 0.75 } ] }
            """, "t");
        rs.Seal();
        var fs = new FeatureSet();
        var f = new Feature { Key = "k", Cell = "Signs_W20-4_36x36", Layer = "TR_WZ_PortableSignMountBlk-Org" };
        f.Geometry.Parts.Add(GeometryPart.Point(new Vec3(0, 0)));
        fs.Features.Add(f);
        var r = LogicEngine.Run(fs, rs);
        Assert.Equal("Road.Sign", r.Graph.Nodes[0].Class?.Name);
        Assert.Equal(2, r.Graph.Nodes[0].Candidates.Count);
    }

    // ------------------------------------------------------------------ fixtures

    [Fact]
    public void Building_WindowsNeedHostsRoofsUsuallyCoverWalls()
    {
        var r = RunFixture("building.geojson");
        Assert.Equal(17, r.Classified.Count());
        // hosting: exactly the two planted orphans fail (one off the walls, one above the wall top)
        Assert.Equal(new[] { "WIN-FLOAT", "WIN-HIGH" }, Subjects(r, "bld.window-host").OrderBy(x => x));
        Assert.True(HasEdge(r, "WIN-1", "hostedBy", "WALL-S"));
        Assert.True(HasEdge(r, "WIN-2", "hostedBy", "WALL-E"));
        Assert.True(HasEdge(r, "WIN-3", "hostedBy", "WALL-N"));           // classified from the A-GLAZ layer
        Assert.True(HasEdge(r, "SKY-1", "hostedBy", "ROOF-1"));            // skylight in the roof
        Assert.True(HasEdge(r, "DOOR-1", "hostedBy", "WALL-W"));           // classified from ifcClass
        // load path
        Assert.Equal(new[] { "CANOPY-1" }, Subjects(r, "bld.cover-supported"));
        Assert.Equal(new[] { "COL-2" }, Subjects(r, "bld.column-bears"));
        Assert.True(HasEdge(r, "COL-1", "bearsOn", "FTG-1"));
        foreach (var w in new[] { "WALL-S", "WALL-E", "WALL-N", "WALL-W" })
        {
            Assert.True(HasEdge(r, w, "coveredBy", "ROOF-1"), w);
            Assert.True(HasEdge(r, "ROOF-1", "supportedBy", w), w);
        }
        // "usually a roof on the walls, but not always": the screen wall is unusual, never an error
        var screen = r.Findings.Where(f => f.Subject?.Key == "WALL-SCREEN").ToList();
        Assert.NotEmpty(screen);
        Assert.All(screen, f => { Assert.Equal(Severity.Info, f.Severity); Assert.Equal(FindingStatus.Unusual, f.Status); });
        Assert.Equal(4, r.Count(Severity.Error));
        Assert.Equal(0, r.Count(Severity.Warning));
    }

    [Fact]
    public void Utilities_EveryPlantedDefectAndNothingElse()
    {
        var r = RunFixture("utilities.geojson");
        _out.WriteLine(string.Join("\n", r.Findings.Select(f => f.ToString())));
        var errors = r.Findings.Where(f => f.Severity == Severity.Error).Select(f => $"{f.Rule}:{f.Subject?.Key}").OrderBy(x => x).ToList();
        Assert.Equal(new[]
        {
            "core.no-cross-system-connection:GS-8", "core.no-cross-system-connection:ST-6",
            "elec.span-two-structures:OH-2", "gas.efv:GS-1",
            "sewer.adverse-slope:SAN-6", "sewer.lateral-one-outlet:LAT-5", "sewer.main-upstream-manhole:SAN-4",
            "sewer.min-diameter:SAN-4", "sewer.no-flow-cycles:MH-6",
            "storm.outlet-below-inlets:ST-5", "storm.pipe-inlet-end:ST-4", "storm.pipe-outlet:ST-4", "storm.pipe-outlet:ST-6",
            "water.fire-main-size:WM-2",
        }.OrderBy(x => x), errors);
        var warnings = r.Findings.Where(f => f.Severity == Severity.Warning).Select(f => $"{f.Rule}:{f.Subject?.Key}").OrderBy(x => x).ToList();
        Assert.Equal(new[] { "default-deny:connectsTo:ST-4", "sewer.manhole-diameter:MH-3", "storm.min-velocity-slope:ST-3", "water.sewer-separation:WM-3" }, warnings);

        // the user's examples: feeders flow into the main; the gas main feeds the addresses through its laterals
        foreach (var (lat, main) in new[] { ("LAT-1", "SAN-1"), ("LAT-2", "SAN-1"), ("LAT-3", "SAN-2"), ("LAT-4", "SAN-2") })
            Assert.True(HasEdge(r, lat, "flowsInto", main), lat);
        Assert.True(HasEdge(r, "GS-5", "tapsInto", "GS-4"));               // branched service
        Assert.True(HasEdge(r, "WM-1", "connectsTo", "WM-2"));            // allowed main joint
        var fed = r.Graph.Edges.Where(e => e.Relation.Name == "feeds" && e.From.Key == "GAS-M2").Select(e => e.To.Key).OrderBy(x => x);
        Assert.Equal(new[] { "P-3", "P-4", "P-6", "P-7" }, fed);
        Assert.True(HasEdge(r, "HY-3", "suppliedBy", "WM-1"));            // symbol-only hydrant → nearest main
        Assert.True(HasEdge(r, "HY-2", "suppliedBy", "WM-2"));            // derived through the lead

        // open world: drains that leave the data are unresolved, not errors
        Assert.Contains("ST-4", Subjects(r, "storm.reaches-outfall", FindingStatus.Unresolved));
        Assert.Contains("WS-3", Subjects(r, "water.service-metered", FindingStatus.Implicit));
    }

    [Fact]
    public void Queries_TraceDownstreamFeedsAndSupply()
    {
        var r = RunFixture("utilities.geojson");
        var down = GraphQuery.Run(r, "downstream", r.Graph.Find("LAT-1")!).Select(s => s.Node.Key).ToList();
        Assert.Equal(new[] { "LAT-1", "SAN-1", "MH-2", "SAN-2", "MH-3", "SAN-3", "LS-1" }, down);
        var up = GraphQuery.Run(r, "upstream", r.Graph.Find("MH-2")!).Select(s => s.Node.Key).ToHashSet();
        Assert.Superset(new HashSet<string> { "SAN-1", "SAN-4", "LAT-1", "LAT-2", "MH-1", "SAN-6", "MH-5" }, up);
        var supply = GraphQuery.Run(r, "supply", r.Graph.Find("P-6")!).Select(s => s.Node.Key).ToList();
        Assert.Equal(new[] { "P-6", "GM-6", "GS-5", "GS-4", "GAS-M2" }, supply);
        // the gas network trace stops at the cross-connection into the water system
        var net = GraphQuery.Run(r, "network", r.Graph.Find("GAS-M1")!);
        Assert.Contains(net, s => s.Node.Key == "WM-1" && s.Stopped);
        Assert.DoesNotContain(net, s => s.Node.Key == "WS-1");
        Assert.Equal(new[] { "WALL-SCREEN" }.ToList(), GraphQuery.Without(RunFixture("building.geojson").Graph, "Bldg.Wall", "coveredBy").Select(n => n.Key).ToList());
    }

    [Fact]
    public void Output_IsDeterministicAndWritersAreWellFormed()
    {
        string Json()
        {
            var sw = new StringWriter();
            var fs = GeoJsonFeatures.ReadFile(Fixture("utilities.geojson"));
            fs.AddRange(GeoJsonFeatures.ReadFile(Fixture("building.geojson")));
            LogicWriters.WriteJson(LogicEngine.Run(fs, Rules.Value), sw);
            return sw.ToString();
        }
        var a = Json();
        Assert.Equal(a, Json());
        using (var doc = JsonDocument.Parse(a)) Assert.True(doc.RootElement.GetProperty("edges").GetArrayLength() > 100);

        var r = RunFixture("utilities.geojson");
        var gml = new StringWriter();
        LogicWriters.WriteGraphMl(r, gml);
        var x = XDocument.Parse(gml.ToString());
        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
        Assert.Equal(r.Graph.Edges.Count, x.Descendants(ns + "edge").Count());
        var cy = new StringWriter();
        LogicWriters.WriteCypher(r, cy);
        Assert.Contains("MERGE (a)-[x:FLOWS_INTO]->(b)", cy.ToString());
        var dir = Path.Combine(Path.GetTempPath(), "dgnkit-logic-neo4j-" + Guid.NewGuid().ToString("N"));
        try
        {
            LogicWriters.WriteNeo4jCsv(r, dir);
            var rels = File.ReadAllLines(Path.Combine(dir, "relationships.csv"));
            Assert.Equal(":START_ID,:END_ID,:TYPE,confidence:double,status,rule,ambiguous:boolean", rels[0]);
            Assert.Equal(r.Graph.Edges.Count + 1, rels.Length);
            var gas = File.ReadAllLines(Path.Combine(dir, "nodes.csv")).Single(l => l.StartsWith("GAS-M1,Gas.Pipe.Main,"));
            Assert.Equal(new[] { "Gas", "Gas_Pipe", "Gas_Pipe_Main", "Object", "Util", "Util_Link", "Util_Pipe" }, gas.Split(',').Last().Split(';').OrderBy(x => x, StringComparer.Ordinal));
            var md = new StringWriter();
            LogicWriters.WriteMarkdown(r, md);
            Assert.Contains("| flowsInto | Sewer.Pipe.Lateral | Sewer.Pipe.GravityMain |", md.ToString());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ImxEnrichment_ClassificationsAndRelationshipsTravelWithElements()
    {
        var r = RunFixture("utilities.geojson");
        var en = LogicImx.ToEnrichment(r);
        Assert.Equal(r.Classified.Count(), en.ElementProperties.Count);
        Assert.Contains(en.RelationshipClasses, c => c.Name == "FlowsInto" && c.Properties["Confidence"] == "double");
        Assert.DoesNotContain(en.RelationshipClasses, c => c.Name is "Crosses" or "NearParallel" or "Duplicates"); // spatial facts stay out
        Assert.All(en.Relationships, rel => Assert.True(r.Graph.Find(rel.SourceKey) != null && r.Graph.Find(rel.TargetKey) != null));
        Assert.Equal("ServesPremise", LogicImx.ClassName("servesPremise"));
    }

    // ------------------------------------------------------------------ corpus

    private static string? Corpus(string rel)
    {
        var p = Path.Combine(TestPaths.Corpus, rel);
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public void Dgn_ImxCarriesLogicRelationships()
    {
        var path = Corpus("wsdot-XE3649/SR24 & SR240 PAVER_PS_TC.dgn");
        if (path == null) { _out.WriteLine("corpus not present — skipped"); return; }
        var f = DgnFile.Open(path);
        var lr = LogicEngine.Run(DgnFeatureExtractor.Extract(f, new DgnFeatureOptions { IncludeReferences = false }), Rules.Value);
        var en = LogicImx.ToEnrichment(lr);
        Assert.Contains(en.Relationships, x => x.Class == "SupportedBy");
        using var ms = new MemoryStream();
        DgnImxReport rep;
        using (var w = new DgnKit.IModel.Imx.ImxWriter(ms)) rep = new DgnImxExporter(f, new DgnImxOptions { Enrichment = en }).Write(w);
        var lines = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var keys = new HashSet<string>();
        int rels = 0, classes = 0, classified = 0;
        bool seenRelationship = false;
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("kind").GetString())
            {
                case "element":
                    Assert.False(seenRelationship, "elements must precede relationships");
                    keys.Add(root.GetProperty("key").GetString()!);
                    if (root.TryGetProperty("properties", out var props) && props.EnumerateArray().Any(p => p.GetProperty("source").GetString() == "Logic")) classified++;
                    break;
                case "relationshipClass": classes++; break;
                case "relationship":
                    seenRelationship = true;
                    rels++;
                    Assert.Contains(root.GetProperty("source").GetString()!, keys);
                    Assert.Contains(root.GetProperty("target").GetString()!, keys);
                    Assert.True(root.TryGetProperty("checksum", out _));
                    break;
            }
        }
        _out.WriteLine($"{classified} classified elements, {classes} relationship classes, {rels} relationships, {rep.DroppedRelationships} dropped");
        Assert.True(rels >= 30, $"{rels} relationships");
        Assert.True(classified >= 200, $"{classified} classified");
        Assert.Equal(en.Relationships.Count, rels + rep.DroppedRelationships);
    }

    [Fact]
    public void Corpus_LogicReport()
    {
        if (!Directory.Exists(TestPaths.Corpus)) { _out.WriteLine("corpus not present — skipped"); return; }
        var files = Directory.EnumerateFiles(TestPaths.Corpus, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".dgn", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var relations = new SortedDictionary<string, (int N, int Ambiguous, double Conf)>(StringComparer.Ordinal);
        var rules = new SortedDictionary<string, Acc>(StringComparer.Ordinal);
        var findings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int objects = 0, classified = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var path in files)
        {
            var r = LogicEngine.Run(DgnFeatureExtractor.Extract(DgnFile.Open(path), new DgnFeatureOptions { IncludeReferences = false }), Rules.Value);
            objects += r.Graph.Nodes.Count;
            classified += r.Classified.Count();
            foreach (var kv in r.ClassCounts()) classes[kv.Key] = classes.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var e in r.Graph.Edges)
            {
                var k = $"{e.From.Class?.Name} -{e.Relation.Name}-> {e.To.Class?.Name}";
                var t = relations.GetValueOrDefault(k);
                relations[k] = (t.N + 1, t.Ambiguous + (e.Ambiguous ? 1 : 0), t.Conf + e.Confidence);
            }
            foreach (var s in r.Stats.Values.Where(s => s.Kind is "require" or "expect" or "check" or "forbid" or "default-deny" or "acyclic"))
            {
                if (!rules.TryGetValue(s.Rule, out var acc)) rules[s.Rule] = acc = new Acc();
                acc.Applicable += s.Applicable; acc.Satisfied += s.Satisfied; acc.Violated += s.Violated; acc.Unresolved += s.Unresolved; acc.Implicit += s.Implicit;
            }
            foreach (var g in r.Findings.GroupBy(x => $"{x.Rule} ({x.Status.ToString().ToLowerInvariant()})")) findings[g.Key] = findings.GetValueOrDefault(g.Key) + g.Count();
        }
        long ms = sw.ElapsedMilliseconds;

        var md = new StringBuilder();
        string P(int k, int n) => n == 0 ? "—" : ((double)k / n).ToString("P1", CultureInfo.InvariantCulture);
        md.AppendLine("# Engineering logic layer — corpus report").AppendLine();
        md.AppendLine($"Generated by `LogicTests.Corpus_LogicReport` with the built-in rule packs ({string.Join(", ", Rules.Value.Packs)}).").AppendLine();
        md.AppendLine($"* {files.Count} DGN files, {objects} top-level objects, {classified} classified ({P(classified, objects)}), {ms} ms in total");
        md.AppendLine("* references not loaded (each file on its own); the corpus has no drainage, sewer, water, gas or building data — those packs are verified on `testdata/logic/*.geojson`").AppendLine();
        md.AppendLine("## Classes").AppendLine().AppendLine("| class | objects |").AppendLine("|---|---:|");
        foreach (var kv in classes) md.AppendLine($"| {kv.Key} | {kv.Value} |");
        md.AppendLine().AppendLine("## Relationships").AppendLine().AppendLine("| relationship | edges | ambiguous | mean confidence |").AppendLine("|---|---:|---:|---:|");
        foreach (var kv in relations) md.AppendLine($"| {kv.Key} | {kv.Value.N} | {kv.Value.Ambiguous} | {(kv.Value.Conf / kv.Value.N).ToString("0.00", CultureInfo.InvariantCulture)} |");
        md.AppendLine().AppendLine("## Rules").AppendLine().AppendLine("| rule | applicable | satisfied | violated | unresolved | implicit | satisfied rate |").AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var kv in rules.Where(k => k.Value.Applicable > 0)) md.AppendLine($"| {kv.Key} | {kv.Value.Applicable} | {kv.Value.Satisfied} | {kv.Value.Violated} | {kv.Value.Unresolved} | {kv.Value.Implicit} | {P(kv.Value.Satisfied, kv.Value.Applicable)} |");
        md.AppendLine().AppendLine("## Findings").AppendLine().AppendLine("| rule (status) | findings |").AppendLine("|---|---:|");
        foreach (var kv in findings) md.AppendLine($"| {kv.Key} | {kv.Value} |");
        File.WriteAllText(Path.Combine(TestPaths.Reports, "logic-report.md"), md.ToString());
        _out.WriteLine(md.ToString());

        // the measurable corpus rules
        var sign = rules["road.sign-supported"];
        Assert.True((double)sign.Satisfied / sign.Applicable >= 0.85, $"sign → support {sign.Satisfied}/{sign.Applicable}");
        var mp = rules["lrs.point-located"];
        Assert.True(mp.Applicable > 3000 && mp.Satisfied == mp.Applicable, $"milepost → route {mp.Satisfied}/{mp.Applicable}");
        Assert.Equal(rules["lrs.arm-in-segment"].Applicable, rules["lrs.arm-in-segment"].Satisfied);
        Assert.True(classes.GetValueOrDefault("Road.Sign") > 300);
    }
}
