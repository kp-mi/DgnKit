using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DgnKit.Logic.Geometry;
using DgnKit.Logic.Model;
using DgnKit.Logic.Rules;
using DgnKit.Logic.Spatial;

namespace DgnKit.Logic.Engine
{
    /// <summary>
    /// Runs a <see cref="RuleSet"/> over a <see cref="FeatureSet"/>, in strata:
    /// <list type="number">
    /// <item>graph build (nodes; source-asserted edges)</item>
    /// <item>classify (noisy-OR per class, propagated to ancestors; the most specific class above the threshold wins)</item>
    /// <item>compute (derived values)</item>
    /// <item>relate, in file order (spatial candidates via R-tree → where-veto → log-odds score → cardinality-constrained
    /// greedy assignment → ambiguity check → orientation)</item>
    /// <item>derive, in file order (best-product path search; positive recursion only)</item>
    /// <item>validate (require/expect with open-world statuses, forbid, check, acyclic, default-deny)</item>
    /// </list>
    /// Deterministic: same input and rules give the same graph, findings and output bytes.
    /// </summary>
    public static class LogicEngine
    {
        public static LogicResult Run(FeatureSet input, RuleSet rules, LogicOptions? options = null)
        {
            var st = new RunState(input, rules, options ?? new LogicOptions());
            st.Execute();
            return st.Result;
        }

        internal sealed class RunState
        {
            private readonly FeatureSet _input;
            private readonly RuleSet _rules;
            private readonly LogicOptions _opt;
            public readonly LogicResult Result = new();
            private readonly LogicGraph _g;
            private readonly EngineFunctions _fn;
            private readonly NodeScope _ns;
            private readonly PairScope _ps;
            private readonly Dictionary<ClassDef, List<Node>> _members = new();
            private readonly Dictionary<ClassDef, StrTree> _trees = new();
            private readonly Dictionary<(string Container, ClassDef Cls), int> _peerCounts = new();
            private readonly Dictionary<string, Box3> _extents = new(StringComparer.Ordinal);
            private Dictionary<long, List<Node>>? _groups;

            public RunState(FeatureSet input, RuleSet rules, LogicOptions opt)
            {
                _input = input;
                _rules = rules;
                _opt = opt;
                _g = new LogicGraph(rules.Ontology);
                Result.Graph = _g;
                Result.Rules = rules;
                _fn = new EngineFunctions(this);
                _ns = new NodeScope(_fn);
                _ps = new PairScope(_fn);
            }

            private RuleStat Stat(Rule r, string kind)
            {
                if (!Result.Stats.TryGetValue(r.Id, out var s))
                {
                    s = new RuleStat { Rule = r.Id, Kind = kind, Pack = r.Pack };
                    Result.Stats[r.Id] = s;
                }
                return s;
            }

            private void Time(string phase, Action a)
            {
                var sw = Stopwatch.StartNew();
                a();
                Result.TimingsMs[phase] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            }

            public void Execute()
            {
                Result.Warnings.AddRange(_input.Warnings);
                foreach (var kv in _input.Info) Result.Info[kv.Key] = kv.Value;
                Result.Info["packs"] = string.Join(", ", _rules.Packs);
                Time("build", Build);
                Time("classify", Classify);
                Time("compute", Compute);
                // stable order: stage, then load order
                Time("relate", () => { foreach (var r in _rules.Relate.Select((r, i) => (r, i)).OrderBy(x => x.r.Stage).ThenBy(x => x.i).Select(x => x.r)) Relate(r); });
                Time("derive", () => { foreach (var r in _rules.Derive.Select((r, i) => (r, i)).OrderBy(x => x.r.Stage).ThenBy(x => x.i).Select(x => x.r)) Derive(r); });
                Time("validate", Validate);
            }

            // ============================================================ build & classify

            private void Build()
            {
                foreach (var f in _input.Features)
                {
                    if (string.IsNullOrEmpty(f.Key)) { Result.Warnings.Add("feature without key skipped"); continue; }
                    if (_g.Find(f.Key) != null) { Result.Warnings.Add($"duplicate feature key {f.Key} skipped"); continue; }
                    _g.AddNode(f);
                    var src = ContainerOf(f);
                    _extents[src] = (_extents.TryGetValue(src, out var b) ? b : Box3.Empty).Extend(f.Geometry.Box);
                }
                foreach (var a in _input.Relations)
                {
                    var from = _g.Find(a.FromKey); var to = _g.Find(a.ToKey);
                    var rel = _rules.Ontology.FindRelation(a.Relation);
                    if (from == null || to == null) { Result.Warnings.Add($"asserted {a.Relation} {a.FromKey}→{a.ToKey}: unknown end"); continue; }
                    if (rel == null) { Result.Warnings.Add($"asserted relation '{a.Relation}' is not in the vocabulary"); continue; }
                    var e = _g.AddEdge(from, rel, to, EdgeStatus.Asserted, 1.0, "source");
                    foreach (var kv in a.Properties) e.Evidence[kv.Key] = kv.Value;
                }
            }

            private static string ContainerOf(Feature f) => (f.Source ?? "") + "|" + (f.Container ?? "");

            private void Classify()
            {
                var unknownClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var support = new Dictionary<ClassDef, (double NotP, string Rules)>();
                var vetoed = new List<ClassDef>();
                foreach (var n in _g.Nodes)
                {
                    support.Clear();
                    vetoed.Clear();
                    void Propose(ClassDef c, double p, string rule)
                    {
                        p = Math.Max(0, Math.Min(1, p));
                        n.Candidates.Add(new Classification(c, p, rule));
                        support[c] = support.TryGetValue(c, out var s) ? (s.NotP * (1 - p), s.Rules + "+" + rule) : (1 - p, rule);
                    }
                    if (!string.IsNullOrEmpty(n.Feature.Class))
                    {
                        var c = _rules.Ontology.FindClass(n.Feature.Class);
                        if (c != null) Propose(c, 1.0, "source");
                        else unknownClasses.Add(n.Feature.Class!);
                    }
                    _ns.With(n);
                    foreach (var r in _rules.Classify)
                    {
                        if (!r.When.Test(_ns)) continue;
                        var c = _rules.Ontology.FindClass(r.ClassName)!;
                        Stat(r, "classify").Accepted++;
                        if (r.Veto) vetoed.Add(c);
                        else Propose(c, r.Confidence, r.Id);
                    }
                    if (support.Count == 0) continue;
                    foreach (var v in vetoed) foreach (var c in support.Keys.ToList()) if (c.IsA(v)) support.Remove(c);
                    if (support.Count == 0) continue;

                    // Evidence for a subclass is evidence for its (primary) ancestors: P(ancestor) = noisy-OR over the
                    // descendants' evidence. Then descend the taxonomy: at each level take the child branch with the most
                    // support, while it stays above the threshold; the answer is the deepest concrete class on that path.
                    // (Traits are roles, not branches: they do not compete here.)
                    var total = new Dictionary<ClassDef, double>();
                    foreach (var kv in support)
                        for (var anc = kv.Key; anc != null; anc = anc.Parent)
                            total[anc] = total.TryGetValue(anc, out var np) ? np * kv.Value.NotP : kv.Value.NotP;
                    ClassDef? best = null;
                    double bestP = 0;
                    ClassDef? cur = null;
                    while (true)
                    {
                        ClassDef? next = null;
                        double nextP = -1;
                        foreach (var kv in total)
                        {
                            if (!ReferenceEquals(kv.Key.Parent, cur)) continue;
                            double p = 1 - kv.Value;
                            if (p > nextP + 1e-12 || (Math.Abs(p - nextP) <= 1e-12 && next != null && string.CompareOrdinal(kv.Key.Name, next.Name) < 0))
                            {
                                next = kv.Key; nextP = p;
                            }
                        }
                        if (next == null || nextP < _rules.MinClassConfidence) break;
                        cur = next;
                        if (!cur.Abstract) { best = cur; bestP = nextP; }
                    }
                    n.Candidates.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
                    if (best == null) continue;
                    n.Class = best;
                    n.ClassConfidence = Math.Round(bestP, 6);
                    n.ClassRule = support.TryGetValue(best, out var own) ? own.Rules : string.Join("+", support.Where(s => s.Key.IsA(best)).Select(s => s.Value.Rules));
                }
                foreach (var u in unknownClasses) Result.Warnings.Add($"source class '{u}' is not in the vocabulary (objects left unclassified)");

                foreach (var n in _g.Nodes)
                {
                    if (n.Class == null) continue;
                    var cont = ContainerOf(n.Feature);
                    foreach (var c in n.Class.SelfAndAncestors())
                    {
                        if (!_members.TryGetValue(c, out var list)) { list = new List<Node>(); _members[c] = list; }
                        list.Add(n);
                        _peerCounts[(cont, c)] = _peerCounts.TryGetValue((cont, c), out var k) ? k + 1 : 1;
                    }
                }
            }

            private List<Node> Members(string? cls)
            {
                var c = _rules.Ontology.FindClass(cls);
                return c != null && _members.TryGetValue(c, out var l) ? l : new List<Node>();
            }

            public int PeerCount(Node n, string cls)
            {
                var c = _rules.Ontology.FindClass(cls);
                return c != null && _peerCounts.TryGetValue((ContainerOf(n.Feature), c), out var k) ? k : 0;
            }

            private readonly Dictionary<(string Container, ClassDef Cls), List<Node>> _peerLists = new();
            private readonly Dictionary<string, Expr> _conditions = new(StringComparer.Ordinal);

            /// <summary>Nodes of class <paramref name="cls"/> in the container of <paramref name="n"/>.</summary>
            public List<Node> PeersOf(Node n, string cls)
            {
                var c = _rules.Ontology.FindClass(cls);
                if (c == null) return new List<Node>();
                var cont = ContainerOf(n.Feature);
                if (_peerLists.TryGetValue((cont, c), out var l)) return l;
                l = Members(cls).Where(m => ContainerOf(m.Feature) == cont).ToList();
                _peerLists[(cont, c)] = l;
                return l;
            }

            public Expr CompiledCondition(string source)
            {
                if (!_conditions.TryGetValue(source, out var e)) { e = Expr.Parse(source); _conditions[source] = e; }
                return e;
            }

            public int Degree(Node n, string rel, string? cls)
            {
                var r = _rules.Ontology.FindRelation(rel);
                if (r == null) return 0;
                var c = cls == null ? null : _rules.Ontology.FindClass(cls);
                int k = 0;
                foreach (var (_, other) in _g.Neighbours(n, r, Direction.Both))
                    if (c == null || other.IsA(c)) k++;
                return k;
            }

            private void Compute()
            {
                foreach (var r in _rules.Compute)
                {
                    var stat = Stat(r, "compute");
                    foreach (var n in Members(r.ForClass))
                    {
                        _ns.With(n);
                        stat.Applicable++;
                        foreach (var (name, expr) in r.Set)
                        {
                            var v = expr.Evaluate(_ns);
                            if (v != null) n.Derived[name] = v;
                        }
                    }
                }
            }

            // ============================================================ relate

            private sealed class Candidate
            {
                public Node S = null!, O = null!;
                public double P, Distance, Tolerance;
                public Dictionary<string, object?> Vars = null!;
                public List<string> Terms = new();
                public bool Reverse;
            }

            private StrTree Tree(ClassDef c)
            {
                if (_trees.TryGetValue(c, out var t)) return t;
                var list = _members.TryGetValue(c, out var m) ? m : new List<Node>();
                t = new StrTree(list.Select(n => (n.Index, n.Feature.Geometry.Box)).ToList());
                _trees[c] = t;
                return t;
            }

            private static double Logit(double p)
            {
                p = Math.Max(1e-9, Math.Min(1 - 1e-9, p));
                return Math.Log(p / (1 - p));
            }

            private static double Sigmoid(double l) => 1 / (1 + Math.Exp(-l));

            private void Relate(RelateRule r)
            {
                var stat = Stat(r, "relate");
                stat.Prior = r.Prior;
                var rel = _rules.Ontology.FindRelation(r.Relation)!;
                var objCls = _rules.Ontology.FindClass(r.Object)!;
                var subjects = Members(r.Subject);
                var objects = Members(r.Object);
                if (subjects.Count == 0 || objects.Count == 0) return;

                Dictionary<string, List<Node>>? keyIndex = null;
                if (r.Test == Test.Attribute)
                {
                    keyIndex = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var o in objects)
                    {
                        if (Expr.ToStr(r.ObjectKey!.Evaluate(_ps.With(o, o))) is not string k || k.Length == 0) continue;
                        if (!keyIndex.TryGetValue(k, out var l)) { l = new List<Node>(); keyIndex[k] = l; }
                        l.Add(o);
                    }
                }
                if (r.Test == Test.SameGroup && _groups == null)
                {
                    _groups = new Dictionary<long, List<Node>>();
                    foreach (var n in _g.Nodes)
                        if (n.Feature.Group != 0)
                        {
                            if (!_groups.TryGetValue(n.Feature.Group, out var l)) { l = new List<Node>(); _groups[n.Feature.Group] = l; }
                            l.Add(n);
                        }
                }

                var all = new List<Candidate>();
                var seenPairs = new HashSet<(int, int)>();
                var bySubject = new Dictionary<Node, List<Candidate>>();
                var skipCls = r.SkipIfRelatedTo != null ? _rules.Ontology.FindClass(r.SkipIfRelatedTo)! : objCls;
                foreach (var s in subjects)
                {
                    if (r.SkipIfRelated && _g.Neighbours(s, rel, Direction.Both).Any(x => x.Node.IsA(skipCls))) continue;
                    stat.Applicable++;
                    var sg = s.Feature.Geometry;
                    double size = sg.Box.Diagonal2D;
                    double? tolConst = r.ToleranceConst ?? (r.ToleranceFactor is double tf ? tf * size : null);
                    IEnumerable<Node> cands;
                    if (r.Test == Test.Attribute)
                    {
                        var k = Expr.ToStr(r.SubjectKey!.Evaluate(_ps.With(s, s)));
                        cands = k != null && keyIndex!.TryGetValue(k, out var l) ? l : Enumerable.Empty<Node>();
                    }
                    else if (r.Test == Test.SameGroup)
                    {
                        cands = s.Feature.Group != 0 && _groups!.TryGetValue(s.Feature.Group, out var l) ? l.Where(o => o.IsA(objCls)) : Enumerable.Empty<Node>();
                    }
                    else
                    {
                        double radius = r.SearchRadius?.Number(_ps.With(s, s)) ?? tolConst ?? 0;
                        if (r.Test is Test.Above or Test.Below) radius = Math.Max(radius, 0) ;
                        var window = sg.Box.Inflate(radius);
                        if (r.Test is Test.Above or Test.Below)
                            window = new Box3(new Vec3(window.Min.X, window.Min.Y, double.MinValue / 4), new Vec3(window.Max.X, window.Max.Y, double.MaxValue / 4));
                        var hits = Tree(objCls).Query(window, r.Use3d && r.Test is not (Test.Above or Test.Below));
                        if (hits.Count > _opt.MaxCandidatesPerSubject)
                            hits = hits.OrderBy(i => sg.Box.Distance2D(_g.Nodes[i].Feature.Geometry.Box)).ThenBy(i => i).Take(_opt.MaxCandidatesPerSubject).ToList();
                        else hits.Sort();
                        cands = hits.Select(i => _g.Nodes[i]);
                    }

                    foreach (var o in cands)
                    {
                        if (ReferenceEquals(o, s)) continue;
                        if (!string.Equals(s.Feature.Frame, o.Feature.Frame, StringComparison.Ordinal)) continue; // different coordinate frames never relate
                        // symmetric relation: a pair already accepted as a candidate from the other side is not scored twice
                        // (but a pair whose test failed one way is still tried the other way — tests like endpointNear are asymmetric)
                        var pair = (Math.Min(s.Index, o.Index), Math.Max(s.Index, o.Index));
                        if (rel.Symmetric && seenPairs.Contains(pair)) continue;
                        stat.Candidates++;
                        _ps.With(s, o);
                        double tol = tolConst ?? r.Tolerance?.Number(_ps) ?? 0;
                        _ps.Vars["tolerance"] = tol;
                        if (!RunTest(r, s, o, tol, out double dist)) continue;
                        _ps.Vars["distance"] = dist;
                        if (r.Where != null && !r.Where.Test(_ps)) { stat.Vetoed++; continue; }

                        double L = Logit(r.Prior);
                        var c = new Candidate { S = s, O = o, Distance = dist, Tolerance = tol };
                        foreach (var (when, w, label) in r.Evidence)
                            if (when.Test(_ps)) { L += w; c.Terms.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1:+0.##;-0.##})", label, w)); }
                        if (tol > 0 && r.Test is not (Test.Attribute or Test.SameGroup))
                        {
                            double rd = Math.Min(1, dist / tol);
                            L -= r.DistanceWeight * rd * rd;
                        }
                        c.P = Sigmoid(L);
                        c.Reverse = r.ReverseWhen != null && r.ReverseWhen.Test(_ps);
                        c.Vars = new Dictionary<string, object?>(_ps.Vars, StringComparer.Ordinal);
                        if (rel.Symmetric) seenPairs.Add(pair);
                        all.Add(c);
                        if (!bySubject.TryGetValue(s, out var bl)) { bl = new List<Candidate>(); bySubject[s] = bl; }
                        bl.Add(c);
                    }
                }

                // cardinality-constrained greedy assignment, strongest first
                all.Sort((a, b) =>
                {
                    int k = b.P.CompareTo(a.P);
                    if (k != 0) return k;
                    k = a.Distance.CompareTo(b.Distance);
                    if (k != 0) return k;
                    k = a.S.Index.CompareTo(b.S.Index);
                    return k != 0 ? k : a.O.Index.CompareTo(b.O.Index);
                });
                var perS = new Dictionary<Node, int>();
                var perO = new Dictionary<Node, int>();
                foreach (var c in all)
                {
                    if (c.P < r.Threshold) { stat.Rejected++; continue; }
                    int ks = perS.TryGetValue(c.S, out var x) ? x : 0;
                    int ko = perO.TryGetValue(c.O, out var y) ? y : 0;
                    if (ks >= r.MaxPerSubject || ko >= r.MaxPerObject) { stat.Rejected++; continue; }
                    perS[c.S] = ks + 1;
                    perO[c.O] = ko + 1;
                    var from = c.Reverse ? c.O : c.S;
                    var to = c.Reverse ? c.S : c.O;
                    double conf = c.P * Math.Max(0.01, c.S.ClassConfidence) * Math.Max(0.01, c.O.ClassConfidence);
                    var e = _g.AddEdge(from, rel, to, EdgeStatus.Inferred, Math.Round(conf, 6), r.Id);
                    stat.Accepted++;
                    e.Evidence["p"] = Math.Round(c.P, 4);
                    e.Evidence["distance"] = Math.Round(c.Distance, 4);
                    if (c.Tolerance > 0) e.Evidence["tolerance"] = Math.Round(c.Tolerance, 4);
                    if (c.Terms.Count > 0) e.Evidence["terms"] = string.Join("; ", c.Terms);
                    foreach (var kv in c.Vars)
                        if (kv.Key is not ("distance" or "tolerance") && kv.Value != null)
                            e.Evidence[kv.Key] = kv.Value is double d ? Math.Round(d, 4) : kv.Value;
                    if (r.MaxPerSubject == 1 && bySubject.TryGetValue(c.S, out var rivals))
                    {
                        // an exact twin of the winner (same symbol, same box) is an equivalent answer, not a rival
                        var runner = rivals.Where(o => !ReferenceEquals(o, c) && o.P >= r.Threshold && !Twin(o.O, c.O)).OrderByDescending(o => o.P).FirstOrDefault();
                        if (runner != null)
                        {
                            e.Evidence["runnerUp"] = runner.O.Key;
                            e.Evidence["runnerUpP"] = Math.Round(runner.P, 4);
                            if (Logit(c.P) - Logit(runner.P) < r.AmbiguityMargin) { e.Ambiguous = true; stat.Ambiguous++; }
                        }
                    }
                }
            }

            private static bool Twin(Node a, Node b)
            {
                if (!ReferenceEquals(a.Class, b.Class) || !string.Equals(a.Feature.Cell, b.Feature.Cell, StringComparison.Ordinal)) return false;
                var x = a.Feature.Geometry.Box; var y = b.Feature.Geometry.Box;
                double eps = Math.Max(1e-9, 1e-3 * Math.Max(x.Diagonal2D, y.Diagonal2D));
                return Math.Abs(x.Min.X - y.Min.X) <= eps && Math.Abs(x.Min.Y - y.Min.Y) <= eps && Math.Abs(x.Max.X - y.Max.X) <= eps && Math.Abs(x.Max.Y - y.Max.Y) <= eps;
            }

            /// <summary>Evaluates the rule's geometric (or key) test; sets test variables on the pair scope.</summary>
            private bool RunTest(RelateRule r, Node s, Node o, double tol, out double dist)
            {
                var sg = s.Feature.Geometry; var og = o.Feature.Geometry;
                var v = _ps.Vars;
                dist = 0;
                // anchor-to-anchor plan distance: separates "centred on" from "next to" when edge distances tie
                v["centerDistance"] = (sg.Anchor - og.Anchor).Length2D;
                switch (r.Test)
                {
                    case Test.Near:
                        dist = GeoOps.Distance(sg, og, r.Use3d);
                        break;
                    case Test.Intersects:
                        dist = GeoOps.Distance(sg, og, r.Use3d);
                        break;
                    case Test.Crosses:
                        if (!GeoOps.Crosses2D(sg, og)) return false;
                        dist = 0;
                        v["angle"] = GeoOps.AngleBetween(sg, og);
                        return true;
                    case Test.EndpointNear:
                    case Test.EndpointsMeet:
                    {
                        var ends = Endpoints(sg);
                        double best = double.MaxValue;
                        string? which = null;
                        Vec3 at = default, other = default;
                        string? objEnd = null;
                        foreach (var (name, p, opposite) in ends)
                        {
                            double d;
                            string? oe = null;
                            if (r.Test == Test.EndpointNear) d = GeoOps.PointToGeometry(p, og, r.Use3d, out _);
                            else
                            {
                                d = double.MaxValue;
                                foreach (var (on, op, _) in Endpoints(og))
                                {
                                    double dd = r.Use3d ? (op - p).Length : (op - p).Length2D;
                                    if (dd < d) { d = dd; oe = on; }
                                }
                            }
                            if (d < best) { best = d; which = name; at = p; other = opposite; objEnd = oe; }
                        }
                        if (which == null) return false;
                        dist = best;
                        v["contactEnd"] = which;
                        v["contactZ"] = at.Z;
                        v["otherEndZ"] = other.Z;
                        if (objEnd != null) v["objectEnd"] = objEnd;
                        break;
                    }
                    case Test.Within:
                    {
                        var a = sg.Anchor;
                        dist = og.HasPolygons || og.HasLines ? GeoOps.PointToGeometry(a, og, r.Use3d, out _) : og.Box.Contains(a, r.Use3d) ? 0 : og.Box.Distance2D(new Box3(a, a));
                        break;
                    }
                    case Test.Contains:
                    {
                        var a = og.Anchor;
                        dist = sg.HasPolygons || sg.HasLines ? GeoOps.PointToGeometry(a, sg, r.Use3d, out _) : sg.Box.Contains(a, r.Use3d) ? 0 : sg.Box.Distance2D(new Box3(a, a));
                        break;
                    }
                    case Test.Above:
                    case Test.Below:
                    {
                        var sb = sg.Box; var ob = og.Box;
                        double dz = r.Test == Test.Above ? ob.Min.Z - sb.Max.Z : sb.Min.Z - ob.Max.Z;
                        if (dz < -tol || dz > r.MaxGap) return false;
                        double minArea = Math.Min(sb.Area2D, ob.Area2D);
                        double ov = minArea > 0 ? sb.OverlapArea2D(ob) / minArea : (sb.Distance2D(ob) <= tol ? 1 : 0);
                        if (ov < r.MinOverlap) return false;
                        v["dz"] = dz;
                        v["overlap"] = ov;
                        dist = Math.Max(0, Math.Abs(dz) - tol) ;
                        return true;
                    }
                    case Test.Overlaps:
                    {
                        var sb = sg.Box.Inflate(tol); var ob = og.Box;
                        if (!sb.Intersects(ob, r.Use3d)) return false;
                        double minArea = Math.Min(sg.Box.Area2D, ob.Area2D);
                        double ov = minArea > 0 ? sg.Box.OverlapArea2D(ob) / minArea : 1;
                        if (ov < r.MinOverlap) return false;
                        v["overlap"] = ov;
                        dist = r.Use3d ? GeoOps.BoxGap3D(sg.Box, ob) : sg.Box.Distance2D(ob);
                        return true;
                    }
                    case Test.Along:
                    {
                        var a = sg.Anchor;
                        dist = GeoOps.PointToGeometry(a, og, r.Use3d, out _);
                        v["offset"] = dist;
                        break;
                    }
                    case Test.SameGroup:
                        dist = GeoOps.Distance(sg, og, false);
                        if (dist == double.MaxValue) dist = 0;
                        return true;
                    case Test.Attribute:
                        dist = GeoOps.Distance(sg, og, false);
                        if (dist == double.MaxValue) dist = 0;
                        return true;
                }
                if (r.Test is Test.Near or Test.Intersects or Test.EndpointNear or Test.EndpointsMeet or Test.Along)
                {
                    v["angle"] = GeoOps.AngleBetween(sg, og);
                }
                return dist <= tol;
            }

            private static List<(string Name, Vec3 P, Vec3 Opposite)> Endpoints(FeatureGeometry g)
            {
                var r = new List<(string, Vec3, Vec3)>();
                var line = g.MainLine;
                if (line != null)
                {
                    var a = line.Points[0]; var b = line.Points[line.Points.Count - 1];
                    r.Add(("start", a, b));
                    r.Add(("end", b, a));
                }
                else if (!g.IsEmpty)
                {
                    var p = g.Anchor;
                    r.Add(("point", p, p));
                }
                return r;
            }

            // ============================================================ derive

            private sealed class Heap
            {
                private readonly List<(double P, int Hops, Node N, int Step, string Path)> _a = new();
                public int Count => _a.Count;
                public void Push((double P, int Hops, Node N, int Step, string Path) x)
                {
                    _a.Add(x);
                    int i = _a.Count - 1;
                    while (i > 0)
                    {
                        int p = (i - 1) / 2;
                        if (Better(_a[p], _a[i])) break;
                        (_a[p], _a[i]) = (_a[i], _a[p]);
                        i = p;
                    }
                }
                public (double P, int Hops, Node N, int Step, string Path) Pop()
                {
                    var top = _a[0];
                    _a[0] = _a[_a.Count - 1];
                    _a.RemoveAt(_a.Count - 1);
                    int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, rr = l + 1, m = i;
                        if (l < _a.Count && Better(_a[l], _a[m])) m = l;
                        if (rr < _a.Count && Better(_a[rr], _a[m])) m = rr;
                        if (m == i) break;
                        (_a[m], _a[i]) = (_a[i], _a[m]);
                        i = m;
                    }
                    return top;
                }
                private static bool Better((double P, int Hops, Node N, int Step, string Path) a, (double P, int Hops, Node N, int Step, string Path) b) =>
                    a.P > b.P || (a.P == b.P && (a.Hops < b.Hops || (a.Hops == b.Hops && a.N.Index < b.N.Index)));
            }

            private void Derive(DeriveRule r)
            {
                var stat = Stat(r, "derive");
                var rel = _rules.Ontology.FindRelation(r.Relation)!;
                var objCls = _rules.Ontology.FindClass(r.Object)!;
                var steps = r.Path.Select(s => (Step: s, Rel: _rules.Ontology.FindRelation(s.Relation)!)).ToList();
                var created = new List<(Node S, Node O, double P, int Hops, string Path)>();
                foreach (var s in Members(r.Subject))
                {
                    stat.Applicable++;
                    var best = new Dictionary<(int, int), double>();
                    var heap = new Heap();
                    heap.Push((1.0, 0, s, 0, s.Key));
                    var found = new HashSet<Node>();
                    while (heap.Count > 0)
                    {
                        var (p, hops, n, i, path) = heap.Pop();
                        if (best.TryGetValue((n.Index, i), out var bp) && bp >= p) continue;
                        best[(n.Index, i)] = p;
                        if (i == steps.Count)
                        {
                            if (!ReferenceEquals(n, s) && n.IsA(objCls) && found.Add(n)) created.Add((s, n, p, hops, path));
                            continue;
                        }
                        if (hops >= r.MaxDepth) continue;
                        var (step, srel) = steps[i];
                        if (step.Repeat && step.AllowZero) heap.Push((p, hops, n, i + 1, path));
                        var dir = step.Inverse ? Direction.In : Direction.Out;
                        foreach (var (e, next) in _g.Neighbours(n, srel, dir))
                        {
                            if (ReferenceEquals(next, s)) continue;
                            double p2 = p * e.Confidence;
                            string path2 = path.Length < 400 ? path + " " + (step.Inverse ? "^" : "") + srel.Name + " " + next.Key : path;
                            // intermediate nodes (anything we continue from) must satisfy "through"
                            bool canContinue = r.Through == null || r.Through.Test(_ns.With(next));
                            if (step.Repeat)
                            {
                                if (canContinue) heap.Push((p2, hops + 1, next, i, path2));
                                heap.Push((p2, hops + 1, next, i + 1, path2));
                            }
                            else heap.Push((p2, hops + 1, next, i + 1, path2));
                            if (!canContinue && i + 1 < steps.Count) best[(next.Index, i + 1)] = double.MaxValue; // blocked: never expand further
                        }
                    }
                }
                foreach (var (s, o, p, hops, path) in created)
                {
                    var e = _g.AddEdge(s, rel, o, EdgeStatus.Derived, Math.Round(p, 6), r.Id);
                    e.Evidence["hops"] = (double)hops;
                    e.Evidence["path"] = path;
                    stat.Accepted++;
                }
            }

            // ============================================================ validate

            private void Validate()
            {
                foreach (var r in _rules.Cardinality) CheckCardinality(r);
                foreach (var r in _rules.Forbid) CheckForbid(r);
                foreach (var r in _rules.Check) CheckAssert(r);
                foreach (var r in _rules.Acyclic) CheckAcyclic(r);
                CheckDefaultDeny();
                Result.Findings.Sort((a, b) =>
                {
                    int k = b.Severity.CompareTo(a.Severity);
                    if (k != 0) return k;
                    k = string.CompareOrdinal(a.Rule, b.Rule);
                    if (k != 0) return k;
                    return (a.Subject?.Index ?? -1).CompareTo(b.Subject?.Index ?? -1);
                });
            }

            private Finding Add(Rule r, string kind, Severity sev, FindingStatus status, Node? s, Node? o, Edge? e, string message)
            {
                var f = new Finding { Rule = r.Id, Kind = kind, Severity = sev, Status = status, Subject = s, Object = o, Edge = e, Message = message };
                if (s != null) f.Scope = ContainerOf(s.Feature);
                Result.Findings.Add(f);
                return f;
            }

            private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

            private static string Format(string? template, IExprScope scope, string fallback)
            {
                if (string.IsNullOrEmpty(template)) return fallback;
                return Placeholder.Replace(template, m =>
                {
                    try { return Expr.ToStr(Expr.Parse(m.Groups[1].Value).Evaluate(scope)) ?? "?"; }
                    catch (FormatException) { return m.Value; }
                });
            }

            private static string Label(Node n) => n.Class != null ? $"{n.Class.Name} {n.Key}" : n.Key;

            private void CheckCardinality(CardinalityRule r)
            {
                string kind = r.Kind == CardinalityKind.Require ? "require" : "expect";
                var stat = Stat(r, kind);
                if (r.Kind == CardinalityKind.Expect) stat.Prior = r.Probability;
                var rel = _rules.Ontology.FindRelation(r.Relation)!;
                var objCls = r.Object == null ? null : _rules.Ontology.FindClass(r.Object);
                var byContainer = new Dictionary<string, (int N, int K)>(StringComparer.Ordinal);
                foreach (var s in Members(r.Subject))
                {
                    _ns.With(s);
                    if (r.When != null && !r.When.Test(_ns)) continue;
                    stat.Applicable++;
                    int count = 0;
                    foreach (var (e, other) in _g.Neighbours(s, rel, r.Direction))
                        if ((objCls == null || other.IsA(objCls)) && e.Confidence >= r.MinConfidence) count++;
                    bool ok = count >= r.Min && count <= r.Max;
                    var cont = ContainerOf(s.Feature);
                    var agg = byContainer.TryGetValue(cont, out var a) ? a : (N: 0, K: 0);
                    byContainer[cont] = (agg.N + 1, agg.K + (ok ? 1 : 0));
                    if (ok) { stat.Satisfied++; continue; }

                    var scope = new DictScope(_ns).Set("count", (double)count).Set("min", (double)r.Min).Set("max", r.Max == int.MaxValue ? null : (double)r.Max);
                    string expected = r.Min == r.Max ? $"exactly {r.Min}" : r.Max == int.MaxValue ? $"at least {r.Min}" : $"{r.Min}..{r.Max}";
                    string what = $"{r.Relation}{(objCls != null ? " " + objCls.Name : "")}";
                    if (r.Kind == CardinalityKind.Expect)
                    {
                        stat.Violated++;
                        if (_opt.ReportExpectInstances)
                            Add(r, kind, r.Severity, FindingStatus.Unusual, s, null, null,
                                Format(r.Message, scope, $"{Label(s)} has {count} {what} (typically {expected}, ~{r.Probability:P0} of cases)"))
                                .Details["count"] = (double)count;
                        continue;
                    }

                    FindingStatus status;
                    if (count > r.Max) status = FindingStatus.Violated;
                    // open world: nothing of the required kind exists anywhere in this container (not drawn on this
                    // sheet / kept in another file) — the absence says nothing about this object
                    else if (objCls != null && PeerCount(s, objCls.Name) == 0) status = FindingStatus.Unresolved;
                    else if (r.BoundaryMargin > 0 && NearExtent(s, r.BoundaryMargin)) status = FindingStatus.Unresolved;
                    else status = r.Absent switch
                    {
                        AbsentPolicy.Unresolved => FindingStatus.Unresolved,
                        AbsentPolicy.Implicit => FindingStatus.Implicit,
                        _ => FindingStatus.Violated,
                    };
                    switch (status)
                    {
                        case FindingStatus.Violated: stat.Violated++; break;
                        case FindingStatus.Unresolved: stat.Unresolved++; break;
                        default: stat.Implicit++; break;
                    }
                    var sev = status == FindingStatus.Violated ? r.Severity : Severity.Info;
                    string msg = Format(r.Message, scope, $"{Label(s)} has {count} {what}, requires {expected}");
                    if (status == FindingStatus.Unresolved && objCls != null && PeerCount(s, objCls.Name) == 0)
                        msg += $" — unresolved: no {objCls.Name} at all in {s.Feature.Container ?? "this data"}";
                    else if (status == FindingStatus.Unresolved && r.Absent != AbsentPolicy.Unresolved)
                        msg += " — unresolved: at the edge of the data";
                    var f = Add(r, kind, sev, status, s, null, null, msg);
                    f.Details["count"] = (double)count;
                }

                if (r.Kind != CardinalityKind.Expect) return;
                double p = Math.Max(1e-6, Math.Min(1 - 1e-6, r.Probability));
                foreach (var kv in byContainer.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var (n, k) = kv.Value;
                    if (n < _opt.AnomalyMinCount) continue;
                    double rate = (double)k / n;
                    double z = (rate - p) / Math.Sqrt(p * (1 - p) / n);
                    if (z >= _opt.AnomalyZ) continue;
                    var f = new Finding
                    {
                        Rule = r.Id, Kind = kind, Severity = Severity.Warning, Status = FindingStatus.Anomaly, Scope = kv.Key,
                        Message = $"only {k} of {n} {r.Subject} in {kv.Key.TrimEnd('|')} satisfy '{r.Relation}{(r.Object != null ? " " + r.Object : "")}' ({rate:P0}; typically {r.Probability:P0}) — check classification, missing data or an unusual design",
                    };
                    f.Details["n"] = (double)n; f.Details["k"] = (double)k; f.Details["z"] = Math.Round(z, 2);
                    Result.Findings.Add(f);
                }
            }

            private bool NearExtent(Node s, double margin)
            {
                if (!_extents.TryGetValue(ContainerOf(s.Feature), out var ext) || ext.IsEmpty) return false;
                var b = s.Feature.Geometry.Box;
                return b.Min.X - ext.Min.X <= margin || ext.Max.X - b.Max.X <= margin || b.Min.Y - ext.Min.Y <= margin || ext.Max.Y - b.Max.Y <= margin;
            }

            private IEnumerable<(Edge E, Node S, Node O)> Oriented(RelationDef rel, ClassDef? sCls, ClassDef? oCls)
            {
                foreach (var e in _g.EdgesOf(rel))
                {
                    if ((sCls == null || e.From.IsA(sCls)) && (oCls == null || e.To.IsA(oCls))) yield return (e, e.From, e.To);
                    else if (rel.Symmetric && (sCls == null || e.To.IsA(sCls)) && (oCls == null || e.From.IsA(oCls))) yield return (e, e.To, e.From);
                }
            }

            private void CheckForbid(ForbidRule r)
            {
                var stat = Stat(r, "forbid");
                var rel = _rules.Ontology.FindRelation(r.Relation)!;
                foreach (var (e, s, o) in Oriented(rel, _rules.Ontology.FindClass(r.Subject), _rules.Ontology.FindClass(r.Object)).ToList())
                {
                    stat.Applicable++;
                    _ps.With(s, o, e);
                    if (r.When != null && !r.When.Test(_ps)) { stat.Satisfied++; continue; }
                    stat.Violated++;
                    Add(r, "forbid", r.Severity, FindingStatus.Violated, s, o, e,
                        Format(r.Message, _ps, $"{Label(s)} {r.Relation} {Label(o)} is forbidden"));
                }
            }

            private void CheckAssert(CheckRule r)
            {
                var stat = Stat(r, "check");
                if (r.Relation == null)
                {
                    foreach (var s in Members(r.Subject))
                    {
                        _ns.With(s);
                        if (r.When != null && !r.When.Test(_ns)) continue;
                        stat.Applicable++;
                        var v = r.Assert.Evaluate(_ns);
                        if (v == null) { stat.Unknown++; continue; }
                        if (Expr.Truthy(v)) { stat.Satisfied++; continue; }
                        stat.Violated++;
                        Add(r, "check", r.Severity, FindingStatus.Violated, s, null, null, Format(r.Message, _ns, $"{Label(s)} fails {r.Assert}"));
                    }
                    return;
                }
                var rel = _rules.Ontology.FindRelation(r.Relation)!;
                foreach (var (e, s, o) in Oriented(rel, _rules.Ontology.FindClass(r.Subject), _rules.Ontology.FindClass(r.Object)).ToList())
                {
                    _ps.With(s, o, e);
                    if (r.When != null && !r.When.Test(_ps)) continue;
                    stat.Applicable++;
                    var v = r.Assert.Evaluate(_ps);
                    if (v == null) { stat.Unknown++; continue; }
                    if (Expr.Truthy(v)) { stat.Satisfied++; continue; }
                    stat.Violated++;
                    Add(r, "check", r.Severity, FindingStatus.Violated, s, o, e, Format(r.Message, _ps, $"{Label(s)} {r.Relation} {Label(o)} fails {r.Assert}"));
                }
            }

            private void CheckAcyclic(AcyclicRule r)
            {
                var stat = Stat(r, "acyclic");
                var rel = _rules.Ontology.FindRelation(r.Relation)!;
                var cls = _rules.Ontology.FindClass(r.Subject)!;
                var nodes = Members(r.Subject);
                stat.Applicable = nodes.Count;
                // Tarjan's strongly connected components (iterative)
                int index = 0;
                var idx = new Dictionary<Node, int>(); var low = new Dictionary<Node, int>();
                var onStack = new HashSet<Node>(); var stack = new Stack<Node>();
                foreach (var root in nodes)
                {
                    if (idx.ContainsKey(root)) continue;
                    var work = new Stack<(Node N, IEnumerator<Node> It)>();
                    void Open(Node n)
                    {
                        idx[n] = low[n] = index++;
                        stack.Push(n); onStack.Add(n);
                        work.Push((n, n.Out.Where(e => ReferenceEquals(e.Relation, rel) && e.To.IsA(cls)).Select(e => e.To).ToList().GetEnumerator()));
                    }
                    Open(root);
                    while (work.Count > 0)
                    {
                        var (n, it) = work.Peek();
                        if (it.MoveNext())
                        {
                            var m = it.Current;
                            if (!idx.ContainsKey(m)) Open(m);
                            else if (onStack.Contains(m)) low[n] = Math.Min(low[n], idx[m]);
                            continue;
                        }
                        work.Pop();
                        if (work.Count > 0) { var parent = work.Peek().N; low[parent] = Math.Min(low[parent], low[n]); }
                        if (low[n] != idx[n]) continue;
                        var comp = new List<Node>();
                        Node x;
                        do { x = stack.Pop(); onStack.Remove(x); comp.Add(x); } while (!ReferenceEquals(x, n));
                        bool selfLoop = comp.Count == 1 && comp[0].Out.Any(e => ReferenceEquals(e.Relation, rel) && ReferenceEquals(e.To, comp[0]));
                        if (comp.Count < 2 && !selfLoop) continue;
                        comp.Sort((a, b) => a.Index.CompareTo(b.Index));
                        stat.Violated++;
                        var f = Add(r, "acyclic", r.Severity, FindingStatus.Violated, comp[0], null, null,
                            r.Message ?? $"{rel.Name} cycle through {comp.Count} {cls.Name} objects: {string.Join(", ", comp.Take(8).Select(c => c.Key))}{(comp.Count > 8 ? " …" : "")}");
                        f.Details["members"] = comp.Select(c => (object?)c.Key).ToList();
                    }
                }
                stat.Satisfied = stat.Applicable - Result.Findings.Where(f => f.Rule == r.Id).Sum(f => f.Details["members"] is List<object?> l ? l.Count : 0);
            }

            private void CheckDefaultDeny()
            {
                foreach (var rel in _rules.Ontology.Relations.Where(x => x.DefaultDeny).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    var allowed = _rules.Relate.Where(r => r.Grants && string.Equals(r.Relation, rel.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(r => (S: _rules.Ontology.FindClass(r.Subject)!, O: _rules.Ontology.FindClass(r.Object)!))
                        .Concat(_rules.Allow.Where(r => string.Equals(r.Relation, rel.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(r => (S: _rules.Ontology.FindClass(r.Subject)!, O: _rules.Ontology.FindClass(r.Object)!)))
                        .ToList();
                    var pseudo = new AllowRule { Id = "default-deny:" + rel.Name, Pack = "engine" };
                    var stat = Stat(pseudo, "default-deny");
                    var forbidden = new HashSet<Edge>(Result.Findings.Where(f => f.Kind == "forbid" && f.Edge != null).Select(f => f.Edge!));
                    foreach (var e in _g.EdgesOf(rel).ToList())
                    {
                        if (forbidden.Contains(e)) continue; // already reported as a prohibition
                        stat.Applicable++;
                        bool ok = allowed.Any(a => (e.From.IsA(a.S) && e.To.IsA(a.O)) || (rel.Symmetric && e.To.IsA(a.S) && e.From.IsA(a.O)));
                        if (ok) { stat.Satisfied++; continue; }
                        stat.Violated++;
                        Add(pseudo, "default-deny", Severity.Warning, FindingStatus.Violated, e.From, e.To, e,
                            $"{Label(e.From)} {rel.Name} {Label(e.To)}: no rule permits this connection (default deny)");
                    }
                }
            }
        }
    }
}
