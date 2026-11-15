using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using DgnKit.Logic.Json;
using DgnKit.Logic.Model;

namespace DgnKit.Logic.Rules
{
    public enum Severity { Info, Warning, Error }

    /// <summary>Common fields of every rule.</summary>
    public abstract class Rule
    {
        public string Id { get; internal set; } = "";
        /// <summary>Evaluation order within a phase (lower first, then load order). Generic detectors use a late stage.</summary>
        public int Stage { get; internal set; }
        public string? Description { get; internal set; }
        /// <summary>Precedent / code reference (e.g. "Ten States Standards §33.4", "IFC IfcRelFillsElement").</summary>
        public string? Reference { get; internal set; }
        /// <summary>Rule pack the rule came from.</summary>
        public string Pack { get; internal set; } = "";
        public override string ToString() => Id;
    }

    /// <summary>Assigns a class to features matching <see cref="When"/>.</summary>
    public sealed class ClassifyRule : Rule
    {
        public string ClassName { get; internal set; } = "";
        public Expr When { get; internal set; } = Expr.True;
        public double Confidence { get; internal set; } = 0.9;
        /// <summary>A veto rule removes the class from the candidates instead of proposing it.</summary>
        public bool Veto { get; internal set; }
    }

    /// <summary>Computes derived values on nodes of a class after classification (<c>set: {name: expr}</c>).</summary>
    public sealed class ComputeRule : Rule
    {
        public string ForClass { get; internal set; } = "";
        public List<(string Name, Expr Value)> Set { get; } = new();
    }

    public enum Test { Near, EndpointNear, EndpointsMeet, Intersects, Crosses, Within, Contains, Above, Below, Overlaps, Along, SameGroup, Attribute }

    /// <summary>
    /// Infers edges <c>subject -relation-> object</c>: spatial (or attribute) candidates → hard <see cref="Where"/> veto →
    /// log-odds score (prior + evidence + distance) → cardinality-constrained assignment → threshold.
    /// </summary>
    public sealed class RelateRule : Rule
    {
        public string Relation { get; internal set; } = "";
        public string Subject { get; internal set; } = "";
        public string Object { get; internal set; } = "";
        public Test Test { get; internal set; } = Test.Near;
        /// <summary>Test tolerance in metres: a constant or an expression over subject/object.</summary>
        public Expr? Tolerance { get; internal set; }
        public double? ToleranceConst { get; internal set; }
        /// <summary>Tolerance as a multiple of the subject's plan size (symbols drawn at sheet scale).</summary>
        public double? ToleranceFactor { get; internal set; }
        /// <summary>Candidate search radius (metres, expression over subject); defaults to the constant tolerance.</summary>
        public Expr? SearchRadius { get; internal set; }
        public bool Use3d { get; internal set; }
        /// <summary>For above/below: the largest vertical gap accepted (metres).</summary>
        public double MaxGap { get; internal set; } = 0.5;
        /// <summary>For above/below/overlaps: minimum XY overlap as a fraction of the smaller footprint.</summary>
        public double MinOverlap { get; internal set; } = 0.05;
        public Expr? Where { get; internal set; }
        public List<(Expr When, double Weight, string Label)> Evidence { get; } = new();
        public double Prior { get; internal set; } = 0.8;
        public double DistanceWeight { get; internal set; } = 1.0;
        public double Threshold { get; internal set; } = 0.5;
        /// <summary>Max edges per subject (1 = "one": the best candidate wins).</summary>
        public int MaxPerSubject { get; internal set; } = int.MaxValue;
        /// <summary>Max edges per object.</summary>
        public int MaxPerObject { get; internal set; } = int.MaxValue;
        /// <summary>Ambiguous when the runner-up is within this many logits (log-odds) of the winner (default 1).</summary>
        public double AmbiguityMargin { get; internal set; } = 1.0;
        /// <summary>When true the edge is stored object → subject (orientation, e.g. flow direction).</summary>
        public Expr? ReverseWhen { get; internal set; }
        /// <summary>Attribute join keys (test = attribute).</summary>
        public Expr? SubjectKey { get; internal set; }
        public Expr? ObjectKey { get; internal set; }
        /// <summary>Skip subjects that already have an edge of this relation (e.g. asserted by the source) to an object of the rule's object class.</summary>
        public bool SkipIfRelated { get; internal set; } = true;
        /// <summary>Wider skip: subjects already related (this relation) to any object of this class are skipped (fallback rules).</summary>
        public string? SkipIfRelatedTo { get; internal set; }
        /// <summary>
        /// Whether this rule's (subject, relation, object) counts as permitted under a default-deny relation. A generic
        /// "detector" rule (any pipe end touching any pipe) sets <c>grants: false</c> so it finds connections without
        /// legitimising them.
        /// </summary>
        public bool Grants { get; internal set; } = true;
    }

    /// <summary>Permits (subject, relation, object) under a default-deny relation (Esri Utility Network style).</summary>
    public sealed class AllowRule : Rule
    {
        public string Subject { get; internal set; } = "";
        public string Relation { get; internal set; } = "";
        public string Object { get; internal set; } = "";
    }

    /// <summary>What a <c>require</c> rule reports when the required object is missing.</summary>
    public enum AbsentPolicy
    {
        /// <summary>A violation (the object must be in this data).</summary>
        Error,
        /// <summary>Open world: the object may live elsewhere (another file, a reference) — reported as unresolved.</summary>
        Unresolved,
        /// <summary>Drafting convention omits it (sign symbol without a drawn post): reported as implied, not an error.</summary>
        Implicit,
    }

    /// <summary>One step of a derive path: relation, inverse (^) and repetition (* or +).</summary>
    public sealed class PathStep
    {
        public string Relation { get; internal set; } = "";
        public bool Inverse { get; internal set; }
        public bool Repeat { get; internal set; }
        public bool AllowZero { get; internal set; }
        public override string ToString() => (Inverse ? "^" : "") + Relation + (Repeat ? (AllowZero ? "*" : "+") : "");

        public static PathStep Parse(string s)
        {
            var st = new PathStep();
            s = s.Trim();
            if (s.StartsWith("^")) { st.Inverse = true; s = s.Substring(1); }
            if (s.EndsWith("*")) { st.Repeat = true; st.AllowZero = true; s = s.Substring(0, s.Length - 1); }
            else if (s.EndsWith("+")) { st.Repeat = true; s = s.Substring(0, s.Length - 1); }
            st.Relation = s;
            return st;
        }
    }

    /// <summary>Derives <c>subject -relation-> object</c> wherever a path of existing edges connects them (composition / closure).</summary>
    public sealed class DeriveRule : Rule
    {
        public string Relation { get; internal set; } = "";
        public string Subject { get; internal set; } = "";
        public string Object { get; internal set; } = "";
        public List<PathStep> Path { get; } = new();
        /// <summary>Intermediate nodes must satisfy this (e.g. stay in the gas network, stop at closed valves).</summary>
        public Expr? Through { get; internal set; }
        public int MaxDepth { get; internal set; } = 10000;
    }

    public enum CardinalityKind { Require, Expect }

    /// <summary>Closed-world cardinality constraint (<c>require</c>) or soft expectation with a prior (<c>expect</c>).</summary>
    public sealed class CardinalityRule : Rule
    {
        public CardinalityKind Kind { get; internal set; }
        public string Subject { get; internal set; } = "";
        public string Relation { get; internal set; } = "";
        public string? Object { get; internal set; }
        public Direction Direction { get; internal set; } = Direction.Out;
        public int Min { get; internal set; } = 1;
        public int Max { get; internal set; } = int.MaxValue;
        /// <summary>Only edges at or above this joint confidence count (default 0: every edge the relate rules accepted).</summary>
        public double MinConfidence { get; internal set; }
        public Expr? When { get; internal set; }
        public Severity Severity { get; internal set; } = Severity.Error;
        public AbsentPolicy Absent { get; internal set; } = AbsentPolicy.Error;
        /// <summary>Subjects within this distance (m) of the data extent are "unresolved" rather than violated (open world).</summary>
        public double BoundaryMargin { get; internal set; }
        /// <summary>Expect only: how often the expectation typically holds (0..1).</summary>
        public double Probability { get; internal set; } = 0.9;
        public string? Message { get; internal set; }
    }

    /// <summary>Forbids edges of a relation between two classes.</summary>
    public sealed class ForbidRule : Rule
    {
        public string Subject { get; internal set; } = "";
        public string Relation { get; internal set; } = "";
        public string Object { get; internal set; } = "";
        public Expr? When { get; internal set; }
        public Severity Severity { get; internal set; } = Severity.Error;
        public string? Message { get; internal set; }
    }

    /// <summary>Attribute assertion on every node of a class, or on every edge of a relation.</summary>
    public sealed class CheckRule : Rule
    {
        public string Subject { get; internal set; } = "";
        public string? Relation { get; internal set; }
        public string? Object { get; internal set; }
        public Expr Assert { get; internal set; } = Expr.True;
        public Expr? When { get; internal set; }
        public Severity Severity { get; internal set; } = Severity.Warning;
        public string? Message { get; internal set; }
    }

    /// <summary>No directed cycles over a relation among nodes of a class (gravity networks).</summary>
    public sealed class AcyclicRule : Rule
    {
        public string Relation { get; internal set; } = "";
        public string Subject { get; internal set; } = "";
        public Severity Severity { get; internal set; } = Severity.Error;
        public string? Message { get; internal set; }
    }

    /// <summary>A named trace (downstream, feeds …) runnable from the CLI or API.</summary>
    public sealed class QueryDef
    {
        public string Name { get; internal set; } = "";
        public string? Description { get; internal set; }
        public List<string> Relations { get; } = new();
        public Direction Direction { get; internal set; } = Direction.Out;
        public Expr? Through { get; internal set; }
        public Expr? StopAt { get; internal set; }
        public string? Pack { get; internal set; }
    }

    /// <summary>
    /// A loaded set of rule packs: ontology + ordered rules. Files are JSON (comments and trailing commas allowed) with a
    /// <c>"dgnkit-rules": 1</c> header; several packs merge (classes/relations union, rule lists concatenated, ids unique).
    /// See docs/LOGIC.md for the format.
    /// </summary>
    public sealed class RuleSet
    {
        public const int FormatVersion = 1;

        public Ontology Ontology { get; } = new();
        public List<string> Packs { get; } = new();
        public List<ClassifyRule> Classify { get; } = new();
        public List<ComputeRule> Compute { get; } = new();
        public List<RelateRule> Relate { get; } = new();
        public List<DeriveRule> Derive { get; } = new();
        public List<CardinalityRule> Cardinality { get; } = new();
        public List<ForbidRule> Forbid { get; } = new();
        public List<CheckRule> Check { get; } = new();
        public List<AcyclicRule> Acyclic { get; } = new();
        public List<AllowRule> Allow { get; } = new();
        public List<QueryDef> Queries { get; } = new();
        public double MinClassConfidence { get; set; } = 0.5;

        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<Rule> AllRules =>
            Classify.Cast<Rule>().Concat(Compute).Concat(Relate).Concat(Derive).Concat(Cardinality).Concat(Forbid).Concat(Check).Concat(Acyclic).Concat(Allow);

        /// <summary>Names of the rule packs compiled into the assembly (repo /rules).</summary>
        public static IReadOnlyList<string> BuiltinPackNames =>
            typeof(RuleSet).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("DgnKit.Logic.Rules.", StringComparison.Ordinal) && n.EndsWith(".rules.json", StringComparison.Ordinal))
                .Select(n => n.Substring("DgnKit.Logic.Rules.".Length, n.Length - "DgnKit.Logic.Rules.".Length - ".rules.json".Length))
                .OrderBy(n => n == "core" ? "" : n, StringComparer.Ordinal).ToList();

        public static string ReadBuiltinPack(string name)
        {
            using var s = typeof(RuleSet).Assembly.GetManifestResourceStream("DgnKit.Logic.Rules." + name + ".rules.json")
                ?? throw new ArgumentException("no built-in rule pack " + name);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>All built-in packs (core first).</summary>
        public static RuleSet Builtin(params string[]? only)
        {
            var rs = new RuleSet();
            foreach (var n in BuiltinPackNames)
                if (only == null || only.Length == 0 || n == "core" || only.Contains(n, StringComparer.OrdinalIgnoreCase))
                    rs.Load(ReadBuiltinPack(n), "builtin:" + n);
            rs.Seal();
            return rs;
        }

        /// <summary>Loads rule files and/or directories of <c>*.rules.json</c>; "builtin" / "builtin:name" select embedded packs.</summary>
        public static RuleSet FromPaths(IEnumerable<string> paths)
        {
            var rs = new RuleSet();
            var list = paths.ToList();
            // the core vocabulary (relations, Util.* roles, Site.Premise) underlies every pack: load it unless given
            bool hasCore = list.Any(p => p.Equals("builtin", StringComparison.OrdinalIgnoreCase) || p.Equals("builtin:core", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).StartsWith("core.", StringComparison.OrdinalIgnoreCase)
                || (Directory.Exists(p) && File.Exists(Path.Combine(p, "core.rules.json"))));
            if (!hasCore) rs.Load(ReadBuiltinPack("core"), "builtin:core");
            foreach (var p in list)
            {
                if (p.Equals("builtin", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var n in BuiltinPackNames) rs.Load(ReadBuiltinPack(n), "builtin:" + n);
                }
                else if (p.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
                {
                    rs.Load(ReadBuiltinPack(p.Substring(8)), p);
                }
                else if (Directory.Exists(p))
                {
                    var files = Directory.GetFiles(p, "*.rules.json").OrderBy(f => Path.GetFileName(f).StartsWith("core.", StringComparison.OrdinalIgnoreCase) ? "" : Path.GetFileName(f), StringComparer.Ordinal);
                    foreach (var f in files) rs.Load(File.ReadAllText(f), f);
                }
                else rs.Load(File.ReadAllText(p), p);
            }
            rs.Seal();
            return rs;
        }

        public List<string> Errors { get; } = new();

        /// <summary>Resolves the taxonomy and checks rule references. Throws when anything is wrong.</summary>
        public void Seal()
        {
            Ontology.Link(Errors);
            void CheckClass(Rule r, string? cls, string field)
            {
                if (cls != null && Ontology.FindClass(cls) == null) Errors.Add($"{r.Pack}: rule {r.Id}: unknown {field} class '{cls}'");
            }
            void CheckRel(Rule r, string rel)
            {
                if (Ontology.FindRelation(rel) == null) Errors.Add($"{r.Pack}: rule {r.Id}: unknown relation '{rel}'");
            }
            foreach (var r in Classify) CheckClass(r, r.ClassName, "target");
            foreach (var r in Compute) CheckClass(r, r.ForClass, "for");
            foreach (var r in Relate) { CheckClass(r, r.Subject, "subject"); CheckClass(r, r.Object, "object"); CheckClass(r, r.SkipIfRelatedTo, "skipIfRelated"); CheckRel(r, r.Relation); }
            foreach (var r in Derive)
            {
                CheckClass(r, r.Subject, "subject"); CheckClass(r, r.Object, "object"); CheckRel(r, r.Relation);
                foreach (var s in r.Path) CheckRel(r, s.Relation);
            }
            foreach (var r in Cardinality) { CheckClass(r, r.Subject, "subject"); CheckClass(r, r.Object, "object"); CheckRel(r, r.Relation); }
            foreach (var r in Forbid) { CheckClass(r, r.Subject, "subject"); CheckClass(r, r.Object, "object"); CheckRel(r, r.Relation); }
            foreach (var r in Check) { CheckClass(r, r.Subject, "subject"); CheckClass(r, r.Object, "object"); if (r.Relation != null) CheckRel(r, r.Relation); }
            foreach (var r in Acyclic) { CheckClass(r, r.Subject, "subject"); CheckRel(r, r.Relation); }
            foreach (var r in Allow) { CheckClass(r, r.Subject, "subject"); CheckClass(r, r.Object, "object"); CheckRel(r, r.Relation); }
            foreach (var q in Queries)
                foreach (var rel in q.Relations)
                    if (Ontology.FindRelation(rel.TrimStart('^', '~')) == null) Errors.Add($"query {q.Name}: unknown relation '{rel}'");
            if (Errors.Count > 0) throw new FormatException("rule set errors:\n  " + string.Join("\n  ", Errors));
        }

        // ------------------------------------------------------------------ loading

        public void Load(string json, string origin)
        {
            if (JsonReader.Parse(json) is not JsonObject root) throw new FormatException($"{origin}: rule file must be a JSON object");
            var ver = root.Num("dgnkit-rules");
            if (ver == null) throw new FormatException($"{origin}: missing \"dgnkit-rules\" header");
            if (ver > FormatVersion) throw new FormatException($"{origin}: rule format {ver} is newer than supported ({FormatVersion})");
            string pack = root.Str("name") ?? Path.GetFileNameWithoutExtension(origin);
            Packs.Add(pack + (root.Str("version") is string v ? "@" + v : ""));
            if (root.Num("minClassConfidence") is double mc) MinClassConfidence = mc;

            if (root.Obj("classes") is JsonObject classes)
                foreach (var name in classes.OrderedKeys)
                {
                    var c = Ontology.Class(name);
                    var o = classes.Obj(name) ?? new JsonObject();
                    if (o.Str("parent") is string p) c.ParentName = p;
                    foreach (var t in o.Strs("traits")) if (!c.TraitNames.Contains(t)) c.TraitNames.Add(t);
                    if (o.Bool("abstract") is bool ab) c.Abstract = ab;
                    c.Description = o.Str("description") ?? c.Description;
                    c.Bis = o.Str("bis") ?? c.Bis;
                    c.Ifc = o.Str("ifc") ?? c.Ifc;
                    c.Shape = o.Str("shape") ?? c.Shape;
                }

            if (root.Obj("relations") is JsonObject rels)
                foreach (var name in rels.OrderedKeys)
                {
                    var r = Ontology.Relation(name);
                    var o = rels.Obj(name) ?? new JsonObject();
                    r.Inverse = o.Str("inverse") ?? r.Inverse;
                    if (o.Bool("symmetric") is bool sy) r.Symmetric = sy;
                    if (o.Bool("transitive") is bool tr) r.Transitive = tr;
                    r.Description = o.Str("description") ?? r.Description;
                    r.Bis = o.Str("bis") ?? r.Bis;
                    if (o.Bool("defaultDeny") is bool dd) r.DefaultDeny = dd;
                    if (o.Bool("persist") is bool ps) r.Persist = ps;
                }

            foreach (var o in Objects(root, "classify", origin))
            {
                var r = Common(new ClassifyRule(), o, pack, origin);
                r.ClassName = Req(o, "class", r, origin);
                r.When = Ex(o, "when", r, origin) ?? Expr.True;
                r.Confidence = o.Num("confidence") ?? 0.9;
                r.Veto = o.Bool("veto") ?? false;
                Classify.Add(r);
            }

            foreach (var o in Objects(root, "compute", origin))
            {
                var r = Common(new ComputeRule(), o, pack, origin);
                r.ForClass = Req(o, "for", r, origin);
                if (o.Obj("set") is JsonObject set)
                    foreach (var k in set.OrderedKeys) r.Set.Add((k, Parse(set.Str(k) ?? "null", r, origin)));
                Compute.Add(r);
            }

            foreach (var o in Objects(root, "relate", origin))
            {
                var r = Common(new RelateRule(), o, pack, origin);
                r.Relation = Req(o, "relation", r, origin);
                r.Subject = Req(o, "subject", r, origin);
                r.Object = Req(o, "object", r, origin);
                string test = o.Str("test") ?? "near";
                if (!Enum.TryParse(test, true, out Test t)) throw new FormatException($"{origin}: rule {r.Id}: unknown test '{test}'");
                r.Test = t;
                switch (o["tolerance"])
                {
                    case double d: r.ToleranceConst = d; break;
                    case string s: r.Tolerance = Parse(s, r, origin); break;
                }
                r.ToleranceFactor = o.Num("toleranceFactor");
                switch (o["searchRadius"])
                {
                    case double d: r.SearchRadius = Expr.Parse(d.ToString("R", CultureInfo.InvariantCulture)); break;
                    case string s: r.SearchRadius = Parse(s, r, origin); break;
                }
                if (r.Test is not (Test.SameGroup or Test.Attribute or Test.Crosses) && r.ToleranceConst == null && r.ToleranceFactor == null && r.Tolerance == null)
                    throw new FormatException($"{origin}: rule {r.Id}: a spatial test needs tolerance or toleranceFactor");
                if (r.Tolerance != null && r.SearchRadius == null)
                    throw new FormatException($"{origin}: rule {r.Id}: an expression tolerance needs searchRadius");
                r.Use3d = o.Bool("use3d") ?? false;
                r.MaxGap = o.Num("maxGap") ?? r.MaxGap;
                r.MinOverlap = o.Num("minOverlap") ?? r.MinOverlap;
                r.Where = Ex(o, "where", r, origin);
                foreach (var ev in o.Arr("evidence"))
                {
                    if (ev is not JsonObject eo) continue;
                    r.Evidence.Add((Parse(eo.Str("when") ?? "true", r, origin), eo.Num("weight") ?? 1.0, eo.Str("label") ?? eo.Str("when") ?? ""));
                }
                r.Prior = o.Num("prior") ?? r.Prior;
                r.DistanceWeight = o.Num("distanceWeight") ?? r.DistanceWeight;
                r.Threshold = o.Num("threshold") ?? r.Threshold;
                r.MaxPerSubject = Card(o["cardinality"]);
                r.MaxPerObject = Card(o["objectCardinality"]);
                r.AmbiguityMargin = o.Num("ambiguityMargin") ?? r.AmbiguityMargin;
                r.ReverseWhen = Ex(o, "reverseWhen", r, origin);
                r.SubjectKey = Ex(o, "subjectKey", r, origin);
                r.ObjectKey = Ex(o, "objectKey", r, origin);
                if (r.Test == Test.Attribute && (r.SubjectKey == null || r.ObjectKey == null))
                    throw new FormatException($"{origin}: rule {r.Id}: test 'attribute' needs subjectKey and objectKey");
                switch (o["skipIfRelated"])
                {
                    case bool b: r.SkipIfRelated = b; break;
                    case string cls: r.SkipIfRelated = true; r.SkipIfRelatedTo = cls; break;
                }
                r.Grants = o.Bool("grants") ?? true;
                Relate.Add(r);
            }

            foreach (var o in Objects(root, "derive", origin))
            {
                var r = Common(new DeriveRule(), o, pack, origin);
                r.Relation = Req(o, "relation", r, origin);
                r.Subject = Req(o, "subject", r, origin);
                r.Object = Req(o, "object", r, origin);
                foreach (var s in o.Strs("path")) r.Path.Add(PathStep.Parse(s));
                if (r.Path.Count == 0) throw new FormatException($"{origin}: rule {r.Id}: derive needs a path");
                r.Through = Ex(o, "through", r, origin);
                r.MaxDepth = (int)(o.Num("maxDepth") ?? r.MaxDepth);
                Derive.Add(r);
            }

            foreach (var kind in new[] { CardinalityKind.Require, CardinalityKind.Expect })
                foreach (var o in Objects(root, kind == CardinalityKind.Require ? "require" : "expect", origin))
                {
                    var r = Common(new CardinalityRule { Kind = kind }, o, pack, origin);
                    r.Subject = Req(o, "subject", r, origin);
                    r.Relation = Req(o, "relation", r, origin);
                    r.Object = o.Str("object");
                    r.Direction = Dir(o.Str("direction"));
                    if (o.Num("exactly") is double ex) { r.Min = (int)ex; r.Max = (int)ex; }
                    else
                    {
                        r.Min = (int)(o.Num("min") ?? 1);
                        r.Max = o.Num("max") is double mx ? (int)mx : int.MaxValue;
                    }
                    r.MinConfidence = o.Num("minConfidence") ?? r.MinConfidence;
                    r.When = Ex(o, "when", r, origin);
                    r.Severity = Sev(o.Str("severity"), kind == CardinalityKind.Require ? Severity.Error : Severity.Info);
                    r.Probability = o.Num("probability") ?? r.Probability;
                    r.Message = o.Str("message");
                    r.Absent = (o.Str("absent") ?? "error").ToLowerInvariant() switch
                    {
                        "unresolved" => AbsentPolicy.Unresolved,
                        "implicit" => AbsentPolicy.Implicit,
                        _ => AbsentPolicy.Error,
                    };
                    r.BoundaryMargin = o.Num("boundaryMargin") ?? 0;
                    Cardinality.Add(r);
                }

            foreach (var o in Objects(root, "forbid", origin))
            {
                var r = Common(new ForbidRule(), o, pack, origin);
                r.Subject = Req(o, "subject", r, origin);
                r.Relation = Req(o, "relation", r, origin);
                r.Object = Req(o, "object", r, origin);
                r.When = Ex(o, "when", r, origin);
                r.Severity = Sev(o.Str("severity"), Severity.Error);
                r.Message = o.Str("message");
                Forbid.Add(r);
            }

            foreach (var o in Objects(root, "check", origin))
            {
                var r = Common(new CheckRule(), o, pack, origin);
                r.Subject = Req(o, "subject", r, origin);
                r.Relation = o.Str("relation");
                r.Object = o.Str("object");
                r.Assert = Ex(o, "assert", r, origin) ?? throw new FormatException($"{origin}: rule {r.Id}: check needs assert");
                r.When = Ex(o, "when", r, origin);
                r.Severity = Sev(o.Str("severity"), Severity.Warning);
                r.Message = o.Str("message");
                Check.Add(r);
            }

            foreach (var o in Objects(root, "acyclic", origin))
            {
                var r = Common(new AcyclicRule(), o, pack, origin);
                r.Relation = Req(o, "relation", r, origin);
                r.Subject = Req(o, "subject", r, origin);
                r.Severity = Sev(o.Str("severity"), Severity.Error);
                r.Message = o.Str("message");
                Acyclic.Add(r);
            }

            foreach (var o in Objects(root, "allow", origin))
            {
                var r = Common(new AllowRule(), o, pack, origin);
                r.Subject = Req(o, "subject", r, origin);
                r.Relation = Req(o, "relation", r, origin);
                r.Object = Req(o, "object", r, origin);
                Allow.Add(r);
            }

            foreach (var o in Objects(root, "queries", origin))
            {
                var q = new QueryDef
                {
                    Name = o.Str("name") ?? throw new FormatException($"{origin}: query without name"),
                    Description = o.Str("description"),
                    Direction = Dir(o.Str("direction")),
                    Pack = pack,
                };
                q.Relations.AddRange(o.Strs("relations"));
                if (o.Str("through") is string th) q.Through = Expr.Parse(th);
                if (o.Str("stopAt") is string sa) q.StopAt = Expr.Parse(sa);
                Queries.RemoveAll(x => string.Equals(x.Name, q.Name, StringComparison.OrdinalIgnoreCase));
                Queries.Add(q);
            }
        }

        private static IEnumerable<JsonObject> Objects(JsonObject root, string key, string origin)
        {
            foreach (var x in root.Arr(key))
            {
                if (x is JsonObject o) yield return o;
                else throw new FormatException($"{origin}: every entry of \"{key}\" must be an object");
            }
        }

        private T Common<T>(T r, JsonObject o, string pack, string origin) where T : Rule
        {
            r.Id = o.Str("id") ?? throw new FormatException($"{origin}: rule without id ({string.Join(",", o.OrderedKeys)})");
            if (!_ids.Add(r.Id)) throw new FormatException($"{origin}: duplicate rule id {r.Id}");
            r.Description = o.Str("description");
            r.Reference = o.Str("reference");
            r.Pack = pack;
            r.Stage = (int)(o.Num("stage") ?? 0);
            return r;
        }

        private static string Req(JsonObject o, string key, Rule r, string origin) =>
            o.Str(key) ?? throw new FormatException($"{origin}: rule {r.Id}: missing \"{key}\"");

        private static Expr? Ex(JsonObject o, string key, Rule r, string origin) => o.Str(key) is string s ? Parse(s, r, origin) : null;

        private static Expr Parse(string s, Rule r, string origin)
        {
            try { return Expr.Parse(s); }
            catch (FormatException ex) { throw new FormatException($"{origin}: rule {r.Id}: {ex.Message}"); }
        }

        private static int Card(object? v) => v switch
        {
            null => int.MaxValue,
            double d => (int)d,
            string s when s.Equals("one", StringComparison.OrdinalIgnoreCase) => 1,
            string s when s.Equals("many", StringComparison.OrdinalIgnoreCase) => int.MaxValue,
            string s when int.TryParse(s, out var n) => n,
            _ => int.MaxValue,
        };

        private static Direction Dir(string? s) => s?.ToLowerInvariant() switch
        {
            "in" or "incoming" => Direction.In,
            "both" or "any" => Direction.Both,
            _ => Direction.Out,
        };

        private static Severity Sev(string? s, Severity dflt) => s?.ToLowerInvariant() switch
        {
            "error" => Severity.Error,
            "warning" or "warn" => Severity.Warning,
            "info" => Severity.Info,
            _ => dflt,
        };
    }
}
