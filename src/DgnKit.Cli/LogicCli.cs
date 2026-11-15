using System.Diagnostics;
using System.Globalization;
using System.Text;
using DgnKit.Logic.Dgn;
using DgnKit.Logic.Engine;
using DgnKit.Logic.Interop;
using DgnKit.Logic.Model;
using DgnKit.Logic.Query;
using DgnKit.Logic.Rules;
using DgnKit.Model;

namespace DgnKit.Cli;

/// <summary><c>dgnkit logic …</c>: the engineering logic layer from the command line.</summary>
internal static class LogicCli
{
    public static int Run(string[] a)
    {
        if (a.Length == 0) { Console.Error.WriteLine("usage: dgnkit logic <file>... | query | rules | scan  (see dgnkit without arguments)"); return 1; }
        return a[0].ToLowerInvariant() switch
        {
            "rules" => Rules(a.Skip(1).ToArray()),
            "query" => Query(a.Skip(1).ToArray()),
            "scan" => Scan(a.Skip(1).ToArray()),
            _ => Analyse(a),
        };
    }

    private static string? Opt(string[] a, string name)
    {
        int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static List<string> Opts(string[] a, string name)
    {
        var r = new List<string>();
        for (int i = 0; i + 1 < a.Length; i++) if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) r.Add(a[i + 1]);
        return r;
    }

    private static bool Flag(string[] a, string name) => a.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    { "--rules", "--model", "-o", "--report", "--graphml", "--cypher", "--neo4j", "--from", "--query", "--csv", "--max" };

    /// <summary>Positional arguments (inputs): everything that is not an option or an option value.</summary>
    private static List<string> Inputs(string[] a)
    {
        var r = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (ValueOptions.Contains(a[i])) { i++; continue; }
            if (a[i].StartsWith("--", StringComparison.Ordinal) || a[i] == "-o") continue;
            r.Add(a[i]);
        }
        return r;
    }

    private static RuleSet LoadRules(string[] a)
    {
        var paths = Opts(a, "--rules");
        return paths.Count == 0 ? RuleSet.Builtin() : RuleSet.FromPaths(paths);
    }

    /// <summary>Reads DGN and GeoJSON inputs into one feature set.</summary>
    internal static FeatureSet LoadInputs(IEnumerable<string> inputs, string? model, bool refs)
    {
        var all = new FeatureSet();
        var sources = new List<string>();
        foreach (var path in inputs)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            FeatureSet fs;
            if (ext is ".geojson" or ".json")
                fs = GeoJsonFeatures.ReadFile(path);
            else
            {
                var f = DgnFile.Open(path, new DgnReadOptions { LoadReferences = refs });
                fs = DgnFeatureExtractor.Extract(f, new DgnFeatureOptions { Models = model == null ? null : new[] { model }, IncludeReferences = refs });
            }
            sources.Add(Path.GetFileName(path));
            all.AddRange(fs);
        }
        all.Info["source"] = string.Join(", ", sources);
        all.Info["features"] = all.Features.Count;
        return all;
    }

    private static int Analyse(string[] a)
    {
        var inputs = Inputs(a);
        if (inputs.Count == 0) { Console.Error.WriteLine("error: no input files"); return 1; }
        var sw = Stopwatch.StartNew();
        var rules = LoadRules(a);
        var fs = LoadInputs(inputs, Opt(a, "--model"), !Flag(a, "--no-refs"));
        var r = LogicEngine.Run(fs, rules);
        PrintSummary(r, sw.ElapsedMilliseconds, int.TryParse(Opt(a, "--max"), out var mx) ? mx : 25);

        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputs[0])) ?? ".", Path.GetFileNameWithoutExtension(inputs[0]));
        var json = Opt(a, "-o") ?? stem + ".logic.json";
        using (var w = new StreamWriter(json, false, new UTF8Encoding(false))) LogicWriters.WriteJson(r, w, Flag(a, "--all"), Flag(a, "--props"));
        Console.WriteLine($"wrote {json}");
        if (Opt(a, "--report") is string md)
        {
            using var w = new StreamWriter(md, false, new UTF8Encoding(false));
            LogicWriters.WriteMarkdown(r, w, "Engineering logic report — " + string.Join(", ", inputs.Select(Path.GetFileName)));
            Console.WriteLine($"wrote {md}");
        }
        if (Opt(a, "--graphml") is string gm)
        {
            using var w = new StreamWriter(gm, false, new UTF8Encoding(false));
            LogicWriters.WriteGraphMl(r, w);
            Console.WriteLine($"wrote {gm}");
        }
        if (Opt(a, "--cypher") is string cy)
        {
            using var w = new StreamWriter(cy, false, new UTF8Encoding(false));
            LogicWriters.WriteCypher(r, w);
            Console.WriteLine($"wrote {cy}");
        }
        if (Opt(a, "--neo4j") is string dir)
        {
            LogicWriters.WriteNeo4jCsv(r, dir);
            Console.WriteLine($"wrote {Path.Combine(dir, "nodes.csv")}, {Path.Combine(dir, "relationships.csv")}");
        }
        return r.Count(Severity.Error) > 0 ? 4 : 0;
    }

    internal static void PrintSummary(LogicResult r, long ms, int maxFindings)
    {
        Console.WriteLine($"Inputs    : {r.Info.GetValueOrDefault("source")}  ({r.Graph.Nodes.Count} objects, {ms} ms)");
        Console.WriteLine($"Rules     : {r.Info.GetValueOrDefault("packs")}");
        var cc = r.ClassCounts().Where(k => true).ToList();
        Console.WriteLine($"Classified: {r.Classified.Count()} objects in {cc.Count} classes");
        foreach (var kv in cc) Console.WriteLine($"  {kv.Key,-34} {kv.Value,7}");
        Console.WriteLine($"Relations : {r.Graph.Edges.Count} edges");
        foreach (var g in r.Graph.Edges.GroupBy(e => (e.Relation.Name, F: e.From.Class?.Name ?? "?", T: e.To.Class?.Name ?? "?")).OrderBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.F, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key.F} -{g.Key.Name}-> {g.Key.T}: {g.Count()} (mean confidence {g.Average(e => e.Confidence).ToString("0.00", CultureInfo.InvariantCulture)}{(g.Any(e => e.Ambiguous) ? $", {g.Count(e => e.Ambiguous)} ambiguous" : "")})");
        Console.WriteLine($"Findings  : {r.Count(Severity.Error)} errors, {r.Count(Severity.Warning)} warnings, {r.Count(Severity.Info)} info");
        foreach (var g in r.Findings.GroupBy(f => (f.Rule, f.Status)))
            Console.WriteLine($"  {g.Key.Rule,-34} {g.Key.Status.ToString().ToLowerInvariant(),-10} {g.Count(),6}");
        foreach (var f in r.Findings.Where(f => f.Severity != Severity.Info).Take(maxFindings))
            Console.WriteLine($"  [{f.Severity.ToString().ToLowerInvariant()}] {f.Message}");
        foreach (var x in r.Warnings.Distinct().Take(10)) Console.WriteLine($"  warning: {x}");
    }

    private static int Query(string[] a)
    {
        var inputs = Inputs(a);
        var from = Opt(a, "--from");
        if (inputs.Count == 0 || from == null) { Console.Error.WriteLine("usage: dgnkit logic query <input>... --from <key> [--query name]"); return 1; }
        var rules = LoadRules(a);
        var r = LogicEngine.Run(LoadInputs(inputs, Opt(a, "--model"), !Flag(a, "--no-refs")), rules);
        var start = r.Graph.Find(from);
        if (start == null) { Console.Error.WriteLine($"error: no object with key '{from}'"); return 2; }
        var name = Opt(a, "--query") ?? "downstream";
        var steps = GraphQuery.Run(r, name, start);
        Console.WriteLine($"{name} from {start}: {steps.Count - 1} objects");
        // print as a tree (each node under the node it was reached from)
        var children = steps.Where(s => s.From != null).GroupBy(s => s.From!).ToDictionary(g => g.Key, g => g.ToList());
        void Print(TraceStep s, int depth)
        {
            Console.WriteLine($"{new string(' ', 2 * Math.Min(depth, 30))}{s.Node.Key} [{s.Node.Class?.Name ?? "?"}]" +
                (s.Via != null ? $"  via {(ReferenceEquals(s.Via.From, s.Node) ? "^" : "")}{s.Via.Relation.Name} ({s.Via.Status.ToString().ToLowerInvariant()}, {s.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})" : "") +
                (s.Stopped ? "  (stop)" : ""));
            if (children.TryGetValue(s.Node, out var kids)) foreach (var k in kids) Print(k, depth + 1);
        }
        Print(steps[0], 0);
        return 0;
    }

    private static int Rules(string[] a)
    {
        var rs = LoadRules(a);
        Console.WriteLine($"packs: {string.Join(", ", rs.Packs)}");
        Console.WriteLine($"classes ({rs.Ontology.Classes.Count()}):");
        foreach (var c in rs.Ontology.Classes.OrderBy(c => c.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {c.Name}{(c.Abstract ? " (abstract)" : "")}{(c.Traits.Count > 0 ? " : " + string.Join(", ", c.Traits.Select(t => t.Name)) : "")}{(c.Bis != null ? "  → " + c.Bis : "")}");
        Console.WriteLine($"relations ({rs.Ontology.Relations.Count()}):");
        foreach (var r in rs.Ontology.Relations.OrderBy(r => r.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {r.Name}{(r.Inverse != null ? " / " + r.Inverse : "")}{(r.Symmetric ? " (symmetric)" : "")}{(r.Transitive ? " (transitive)" : "")}{(r.DefaultDeny ? " (default deny)" : "")}{(!r.Persist ? " (not persisted)" : "")}");
        Console.WriteLine("rules:");
        foreach (var g in rs.AllRules.GroupBy(r => r.Pack))
            Console.WriteLine($"  {g.Key}: {string.Join(", ", g.GroupBy(r => r.GetType().Name.Replace("Rule", "").ToLowerInvariant()).Select(x => $"{x.Count()} {x.Key}"))}");
        Console.WriteLine($"queries: {string.Join(", ", rs.Queries.Select(q => q.Name))}");
        return 0;
    }

    private static int Scan(string[] a)
    {
        var dir = Inputs(a).FirstOrDefault();
        if (dir == null || !Directory.Exists(dir)) { Console.Error.WriteLine("usage: dgnkit logic scan <dir> [--csv out.csv] [--report out.md]"); return 1; }
        var rules = LoadRules(a);
        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".dgn", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var csv = new StringBuilder("file,objects,classified,edges,errors,warnings,infos,ms,error\n");
        var classTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var relTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        var ruleTotals = new Dictionary<string, (int Applicable, int Satisfied, int Violated)>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var sw = Stopwatch.StartNew();
            string rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
            try
            {
                var r = LogicEngine.Run(LoadInputs(new[] { path }, null, false), rules);
                foreach (var kv in r.ClassCounts()) classTotals[kv.Key] = classTotals.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (var e in r.Graph.Edges)
                {
                    var k = $"{e.From.Class?.Name ?? "?"} -{e.Relation.Name}-> {e.To.Class?.Name ?? "?"}";
                    relTotals[k] = relTotals.GetValueOrDefault(k) + 1;
                }
                foreach (var s in r.Stats.Values.Where(s => s.Kind is "require" or "expect" or "check" or "forbid"))
                {
                    var t = ruleTotals.GetValueOrDefault(s.Rule);
                    ruleTotals[s.Rule] = (t.Applicable + s.Applicable, t.Satisfied + s.Satisfied, t.Violated + s.Violated);
                }
                csv.Append($"\"{rel}\",{r.Graph.Nodes.Count},{r.Classified.Count()},{r.Graph.Edges.Count},{r.Count(Severity.Error)},{r.Count(Severity.Warning)},{r.Count(Severity.Info)},{sw.ElapsedMilliseconds},\n");
                Console.WriteLine($"{rel}: {r.Classified.Count()}/{r.Graph.Nodes.Count} classified, {r.Graph.Edges.Count} edges, {r.Count(Severity.Error)} E / {r.Count(Severity.Warning)} W ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                csv.Append($"\"{rel}\",,,,,,,{sw.ElapsedMilliseconds},\"{ex.Message.Replace("\"", "'")}\"\n");
                Console.WriteLine($"{rel}: ERROR {ex.Message}");
            }
        }
        if (Opt(a, "--csv") is string c) File.WriteAllText(c, csv.ToString());
        if (Opt(a, "--report") is string md)
        {
            var w = new StringBuilder();
            w.AppendLine($"# Logic scan — {files.Count} files").AppendLine();
            w.AppendLine("| class | objects |").AppendLine("|---|---:|");
            foreach (var kv in classTotals.OrderBy(k => k.Key, StringComparer.Ordinal)) w.AppendLine($"| {kv.Key} | {kv.Value} |");
            w.AppendLine().AppendLine("| relationship | edges |").AppendLine("|---|---:|");
            foreach (var kv in relTotals.OrderBy(k => k.Key, StringComparer.Ordinal)) w.AppendLine($"| {kv.Key} | {kv.Value} |");
            w.AppendLine().AppendLine("| rule | applicable | satisfied | violated |").AppendLine("|---|---:|---:|---:|");
            foreach (var kv in ruleTotals.Where(k => k.Value.Applicable > 0).OrderBy(k => k.Key, StringComparer.Ordinal)) w.AppendLine($"| {kv.Key} | {kv.Value.Applicable} | {kv.Value.Satisfied} | {kv.Value.Violated} |");
            File.WriteAllText(md, w.ToString());
        }
        return 0;
    }
}
