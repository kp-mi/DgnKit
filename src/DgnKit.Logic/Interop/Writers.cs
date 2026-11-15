using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DgnKit.Logic.Engine;
using DgnKit.Logic.Json;
using DgnKit.Logic.Model;
using DgnKit.Logic.Rules;

namespace DgnKit.Logic.Interop
{
    /// <summary>Serialises a <see cref="LogicResult"/>: JSON (full), Markdown (report), GraphML, Neo4j CSV, Cypher.</summary>
    public static class LogicWriters
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string N(double d) => JsonWriter.FormatNumber(d);

        // ---------------------------------------------------------------- JSON

        /// <summary>
        /// The full result: classified nodes (key, class, confidence, rule, candidates, derived values), edges (with
        /// evidence), findings, per-rule statistics. Unclassified objects are omitted unless <paramref name="includeUnclassified"/>.
        /// </summary>
        public static void WriteJson(LogicResult r, TextWriter tw, bool includeUnclassified = false, bool includeProperties = false)
        {
            var w = new JsonWriter(tw);
            w.StartObject();
            w.Prop("format", "dgnkit-logic").Prop("version", 1);
            w.Name("info").StartObject();
            foreach (var kv in r.Info.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Prop(kv.Key, kv.Value);
            w.EndObject();
            w.Name("summary").StartObject();
            w.Prop("objects", r.Graph.Nodes.Count).Prop("classified", r.Classified.Count()).Prop("edges", r.Graph.Edges.Count);
            w.Prop("errors", r.Count(Severity.Error)).Prop("warnings", r.Count(Severity.Warning)).Prop("infos", r.Count(Severity.Info));
            w.Name("classes").Value(r.ClassCounts().ToDictionary(k => k.Key, k => (object?)k.Value));
            w.Name("relations").Value(r.RelationCounts().ToDictionary(k => k.Key, k => (object?)k.Value));
            w.EndObject();

            w.Name("nodes").StartArray();
            foreach (var n in r.Graph.Nodes)
            {
                if (n.Class == null && !includeUnclassified) continue;
                w.StartObject();
                w.Prop("key", n.Key).Prop("class", n.Class?.Name).Prop("confidence", n.ClassConfidence).Prop("rule", n.ClassRule);
                if (n.Feature.Source != null) w.Prop("source", n.Feature.Source);
                if (n.Feature.Container != null) w.Prop("container", n.Feature.Container);
                if (n.Feature.Layer != null) w.Prop("layer", n.Feature.Layer);
                if (n.Feature.Cell != null) w.Prop("cell", n.Feature.Cell);
                var b = n.Feature.Geometry.Box;
                if (!b.IsEmpty) w.Name("box").Value(new object?[] { Math.Round(b.Min.X, 3), Math.Round(b.Min.Y, 3), Math.Round(b.Min.Z, 3), Math.Round(b.Max.X, 3), Math.Round(b.Max.Y, 3), Math.Round(b.Max.Z, 3) });
                if (n.Candidates.Count > 1)
                    w.Name("candidates").Value(n.Candidates.Take(5).Select(c => (object?)$"{c.Class.Name} {c.Confidence.ToString("0.###", Inv)} {c.Rule}").ToList());
                if (n.Derived.Count > 0) w.Name("derived").Value(n.Derived.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
                if (includeProperties && n.Feature.Properties.Count > 0)
                    w.Name("properties").Value(n.Feature.Properties.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
                w.EndObject();
            }
            w.EndArray();

            w.Name("edges").StartArray();
            foreach (var e in r.Graph.Edges)
            {
                w.StartObject();
                w.Prop("from", e.From.Key).Prop("relation", e.Relation.Name).Prop("to", e.To.Key);
                w.Prop("status", e.Status.ToString().ToLowerInvariant()).Prop("confidence", e.Confidence).Prop("rule", e.Rule);
                if (e.Ambiguous) w.Prop("ambiguous", true);
                if (e.Evidence.Count > 0) w.Name("evidence").Value(e.Evidence.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
                w.EndObject();
            }
            w.EndArray();

            w.Name("findings").StartArray();
            foreach (var f in r.Findings)
            {
                w.StartObject();
                w.Prop("rule", f.Rule).Prop("kind", f.Kind).Prop("severity", f.Severity.ToString().ToLowerInvariant()).Prop("status", f.Status.ToString().ToLowerInvariant());
                if (f.Subject != null) w.Prop("subject", f.Subject.Key);
                if (f.Object != null) w.Prop("object", f.Object.Key);
                if (f.Scope != null && f.Subject == null) w.Prop("scope", f.Scope);
                w.Prop("message", f.Message);
                if (f.Details.Count > 0) w.Name("details").Value(f.Details.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
                w.EndObject();
            }
            w.EndArray();

            w.Name("rules").StartArray();
            foreach (var s in r.Stats.Values.OrderBy(s => s.Rule, StringComparer.Ordinal))
            {
                w.StartObject();
                w.Prop("rule", s.Rule).Prop("kind", s.Kind).Prop("pack", s.Pack);
                void P(string k, int v) { if (v != 0) w.Prop(k, v); }
                P("applicable", s.Applicable); P("candidates", s.Candidates); P("vetoed", s.Vetoed); P("accepted", s.Accepted); P("rejected", s.Rejected);
                P("ambiguous", s.Ambiguous); P("satisfied", s.Satisfied); P("violated", s.Violated); P("unresolved", s.Unresolved); P("implicit", s.Implicit); P("unknown", s.Unknown);
                if (s.Prior is double pr) w.Prop("prior", pr);
                if (s.ObservedRate is double or) w.Prop("observedRate", Math.Round(or, 4));
                w.EndObject();
            }
            w.EndArray();
            if (r.Warnings.Count > 0) w.Name("warnings").Value(r.Warnings.Distinct().ToList());
            w.EndObject();
            tw.WriteLine();
        }

        // ---------------------------------------------------------------- Markdown report

        public static void WriteMarkdown(LogicResult r, TextWriter w, string title = "Engineering logic report", int maxFindingsPerRule = 15)
        {
            w.WriteLine($"# {title}");
            w.WriteLine();
            foreach (var kv in r.Info.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteLine($"* **{kv.Key}**: {kv.Value}");
            w.WriteLine($"* **objects**: {r.Graph.Nodes.Count}, classified {r.Classified.Count()}, edges {r.Graph.Edges.Count}");
            w.WriteLine($"* **findings**: {r.Count(Severity.Error)} errors, {r.Count(Severity.Warning)} warnings, {r.Count(Severity.Info)} info");
            w.WriteLine();

            w.WriteLine("## Classes");
            w.WriteLine();
            w.WriteLine("| class | objects | mean confidence |");
            w.WriteLine("|---|---:|---:|");
            foreach (var g in r.Classified.GroupBy(n => n.Class!.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
                w.WriteLine($"| {g.Key} | {g.Count()} | {g.Average(n => n.ClassConfidence).ToString("0.00", Inv)} |");
            w.WriteLine();

            w.WriteLine("## Relationships");
            w.WriteLine();
            w.WriteLine("| relation | from class | to class | edges | inferred | derived | asserted | ambiguous | mean confidence |");
            w.WriteLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (var g in r.Graph.Edges.GroupBy(e => (e.Relation.Name, F: e.From.Class?.Name ?? "?", T: e.To.Class?.Name ?? "?")).OrderBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.F, StringComparer.Ordinal).ThenBy(g => g.Key.T, StringComparer.Ordinal))
                w.WriteLine($"| {g.Key.Name} | {g.Key.F} | {g.Key.T} | {g.Count()} | {g.Count(e => e.Status == EdgeStatus.Inferred)} | {g.Count(e => e.Status == EdgeStatus.Derived)} | {g.Count(e => e.Status == EdgeStatus.Asserted)} | {g.Count(e => e.Ambiguous)} | {g.Average(e => e.Confidence).ToString("0.00", Inv)} |");
            w.WriteLine();

            w.WriteLine("## Rules");
            w.WriteLine();
            w.WriteLine("| rule | kind | applicable | candidates | accepted | satisfied | violated | unresolved | implicit | unknown | prior | observed |");
            w.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var s in r.Stats.Values.Where(s => s.Kind != "classify" || s.Accepted > 0).OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Rule, StringComparer.Ordinal))
                w.WriteLine($"| {s.Rule} | {s.Kind} | {s.Applicable} | {s.Candidates} | {s.Accepted} | {s.Satisfied} | {s.Violated} | {s.Unresolved} | {s.Implicit} | {s.Unknown} | {(s.Prior is double p ? p.ToString("0.##", Inv) : "")} | {(s.ObservedRate is double o ? o.ToString("P0", Inv) : "")} |");
            w.WriteLine();

            w.WriteLine("## Findings");
            w.WriteLine();
            if (r.Findings.Count == 0) w.WriteLine("None.");
            foreach (var g in r.Findings.GroupBy(f => f.Rule))
            {
                var list = g.ToList();
                w.WriteLine($"### {g.Key} — {list.Count} ({string.Join(", ", list.GroupBy(f => f.Status).Select(x => $"{x.Count()} {x.Key.ToString().ToLowerInvariant()}"))})");
                w.WriteLine();
                foreach (var f in list.Take(maxFindingsPerRule)) w.WriteLine($"* `{f.Severity.ToString().ToLowerInvariant()}` {Escape(f.Message)}");
                if (list.Count > maxFindingsPerRule) w.WriteLine($"* … {list.Count - maxFindingsPerRule} more");
                w.WriteLine();
            }
            if (r.Warnings.Count > 0)
            {
                w.WriteLine("## Warnings");
                w.WriteLine();
                foreach (var x in r.Warnings.Distinct().Take(50)) w.WriteLine($"* {Escape(x)}");
                w.WriteLine();
            }
        }

        private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");

        // ---------------------------------------------------------------- GraphML

        /// <summary>GraphML (yEd, Gephi, NetworkX, igraph): classified nodes and all edges with their attributes.</summary>
        public static void WriteGraphMl(LogicResult r, TextWriter w)
        {
            string X(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";
            w.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            w.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
            foreach (var (id, forWhat, name, type) in new[]
            {
                ("class", "node", "class", "string"), ("confidence", "node", "confidence", "double"), ("layer", "node", "layer", "string"),
                ("cell", "node", "cell", "string"), ("source", "node", "source", "string"), ("x", "node", "x", "double"), ("y", "node", "y", "double"),
                ("relation", "edge", "relation", "string"), ("econf", "edge", "confidence", "double"), ("status", "edge", "status", "string"), ("rule", "edge", "rule", "string"),
            })
                w.WriteLine($"  <key id=\"{id}\" for=\"{forWhat}\" attr.name=\"{name}\" attr.type=\"{type}\"/>");
            w.WriteLine("  <graph id=\"G\" edgedefault=\"directed\">");
            var included = new HashSet<Node>(r.Classified);
            foreach (var e in r.Graph.Edges) { included.Add(e.From); included.Add(e.To); }
            foreach (var n in r.Graph.Nodes.Where(included.Contains))
            {
                var a = n.Feature.Geometry.Anchor;
                w.WriteLine($"    <node id=\"{X(n.Key)}\"><data key=\"class\">{X(n.Class?.Name)}</data><data key=\"confidence\">{N(n.ClassConfidence)}</data><data key=\"layer\">{X(n.Feature.Layer)}</data><data key=\"cell\">{X(n.Feature.Cell)}</data><data key=\"source\">{X(n.Feature.Source)}</data><data key=\"x\">{N(a.X)}</data><data key=\"y\">{N(a.Y)}</data></node>");
            }
            foreach (var e in r.Graph.Edges)
                w.WriteLine($"    <edge id=\"e{e.Index}\" source=\"{X(e.From.Key)}\" target=\"{X(e.To.Key)}\"><data key=\"relation\">{X(e.Relation.Name)}</data><data key=\"econf\">{N(e.Confidence)}</data><data key=\"status\">{e.Status.ToString().ToLowerInvariant()}</data><data key=\"rule\">{X(e.Rule)}</data></edge>");
            w.WriteLine("  </graph>");
            w.WriteLine("</graphml>");
        }

        // ---------------------------------------------------------------- Neo4j / Cypher

        private static string Csv(string? s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>Neo4j label from a class name: <c>Sewer.Lateral</c> → <c>Sewer_Lateral</c>.</summary>
        public static string Label(string cls) => new string(cls.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        /// <summary>
        /// <c>neo4j-admin database import</c> CSVs: <c>nodes.csv</c> (<c>key:ID,class,confidence:double,…,:LABEL</c>,
        /// labels = class and every ancestor) and <c>relationships.csv</c> (<c>:START_ID,:END_ID,:TYPE,…</c>).
        /// </summary>
        public static void WriteNeo4jCsv(LogicResult r, string directory)
        {
            Directory.CreateDirectory(directory);
            var included = new HashSet<Node>(r.Classified);
            foreach (var e in r.Graph.Edges) { included.Add(e.From); included.Add(e.To); }
            using (var w = new StreamWriter(Path.Combine(directory, "nodes.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine("key:ID,class,confidence:double,layer,cell,source,container,x:double,y:double,:LABEL");
                foreach (var n in r.Graph.Nodes.Where(included.Contains))
                {
                    var a = n.Feature.Geometry.Anchor;
                    var labels = n.Class == null ? "Object" : string.Join(";", n.Class.SelfAndAncestors().Select(c => Label(c.Name)).Append("Object").Distinct());
                    w.WriteLine(string.Join(",", Csv(n.Key), Csv(n.Class?.Name), N(n.ClassConfidence), Csv(n.Feature.Layer), Csv(n.Feature.Cell), Csv(n.Feature.Source), Csv(n.Feature.Container), N(a.X), N(a.Y), labels));
                }
            }
            using (var w = new StreamWriter(Path.Combine(directory, "relationships.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine(":START_ID,:END_ID,:TYPE,confidence:double,status,rule,ambiguous:boolean");
                foreach (var e in r.Graph.Edges)
                    w.WriteLine(string.Join(",", Csv(e.From.Key), Csv(e.To.Key), RelType(e.Relation.Name), N(e.Confidence), e.Status.ToString().ToLowerInvariant(), Csv(e.Rule), e.Ambiguous ? "true" : "false"));
            }
        }

        /// <summary>Neo4j relationship type convention: <c>flowsInto</c> → <c>FLOWS_INTO</c>.</summary>
        public static string RelType(string rel)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < rel.Length; i++)
            {
                char c = rel[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
            }
            return sb.ToString();
        }

        private static string CypherStr(string? s) => s == null ? "null" : "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        /// <summary>
        /// A Cypher script (Neo4j 5, Memgraph, Apache AGE, LadybugDB-compatible subset): a uniqueness constraint, then
        /// batched <c>UNWIND … MERGE</c> of nodes and relationships.
        /// </summary>
        public static void WriteCypher(LogicResult r, TextWriter w, int batch = 500)
        {
            w.WriteLine("// generated by DgnKit.Logic — load with cypher-shell -f, or paste into Neo4j Browser");
            w.WriteLine("CREATE CONSTRAINT dgnkit_object_key IF NOT EXISTS FOR (n:Object) REQUIRE n.key IS UNIQUE;");
            var included = new HashSet<Node>(r.Classified);
            foreach (var e in r.Graph.Edges) { included.Add(e.From); included.Add(e.To); }
            foreach (var group in r.Graph.Nodes.Where(included.Contains).GroupBy(n => n.Class?.Name ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var labels = group.Key.Length == 0 ? "" : ":" + string.Join(":", r.Rules.Ontology.FindClass(group.Key)!.SelfAndAncestors().Select(c => Label(c.Name)));
                foreach (var chunk in Chunks(group.ToList(), batch))
                {
                    w.WriteLine("UNWIND [");
                    w.WriteLine(string.Join(",\n", chunk.Select(n =>
                    {
                        var a = n.Feature.Geometry.Anchor;
                        return $"  {{key:{CypherStr(n.Key)}, class:{CypherStr(n.Class?.Name)}, confidence:{N(n.ClassConfidence)}, layer:{CypherStr(n.Feature.Layer)}, cell:{CypherStr(n.Feature.Cell)}, source:{CypherStr(n.Feature.Source)}, x:{N(a.X)}, y:{N(a.Y)}}}";
                    })));
                    w.WriteLine($"] AS row MERGE (n:Object {{key: row.key}}) SET n += row{(labels.Length > 0 ? ", n" + labels : "")};");
                }
            }
            foreach (var group in r.Graph.Edges.GroupBy(e => e.Relation.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
                foreach (var chunk in Chunks(group.ToList(), batch))
                {
                    w.WriteLine("UNWIND [");
                    w.WriteLine(string.Join(",\n", chunk.Select(e =>
                        $"  {{from:{CypherStr(e.From.Key)}, to:{CypherStr(e.To.Key)}, confidence:{N(e.Confidence)}, status:{CypherStr(e.Status.ToString().ToLowerInvariant())}, rule:{CypherStr(e.Rule)}}}")));
                    w.WriteLine($"] AS row MATCH (a:Object {{key: row.from}}), (b:Object {{key: row.to}}) MERGE (a)-[x:{RelType(group.Key)}]->(b) SET x.confidence = row.confidence, x.status = row.status, x.rule = row.rule;");
                }
        }

        private static IEnumerable<List<T>> Chunks<T>(List<T> list, int size)
        {
            for (int i = 0; i < list.Count; i += size) yield return list.GetRange(i, Math.Min(size, list.Count - i));
        }
    }
}
