using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Logic.Engine;
using DgnKit.Logic.Model;
using DgnKit.Logic.Rules;

namespace DgnKit.Logic.Query
{
    /// <summary>One reached node of a trace, with its depth and the edge it was reached by.</summary>
    public sealed class TraceStep
    {
        public Node Node { get; internal set; } = null!;
        public int Depth { get; internal set; }
        public Edge? Via { get; internal set; }
        public Node? From { get; internal set; }
        /// <summary>Product of edge confidences along the path.</summary>
        public double Confidence { get; internal set; }
        public bool Stopped { get; internal set; }
    }

    /// <summary>Graph traversals over a <see cref="LogicGraph"/> (read-only).</summary>
    public static class GraphQuery
    {
        /// <summary>
        /// Breadth-first trace from <paramref name="start"/> over the given relations. <paramref name="through"/> must hold
        /// for a node to be passed through (e.g. not a closed valve); <paramref name="stopAt"/> nodes are reported but not
        /// expanded (e.g. the outfall).
        /// </summary>
        public static List<TraceStep> Trace(LogicGraph g, Node start, IEnumerable<string> relations, Direction dir,
            Expr? through = null, Expr? stopAt = null, int maxDepth = int.MaxValue)
        {
            // relation specs: "rel" (the query direction), "^rel" (walk it backwards), "~rel" (either way)
            var rels = relations.Select(spec =>
            {
                var d = dir;
                var name = spec.Trim();
                if (name.StartsWith("^")) { d = dir == Direction.In ? Direction.Out : Direction.In; name = name.Substring(1); }
                else if (name.StartsWith("~")) { d = Direction.Both; name = name.Substring(1); }
                return (Rel: g.Ontology.FindRelation(name) ?? throw new ArgumentException("unknown relation " + name), Dir: d);
            }).ToList();
            var result = new List<TraceStep> { new() { Node = start, Depth = 0, Confidence = 1 } };
            var seen = new HashSet<Node> { start };
            var queue = new Queue<TraceStep>();
            queue.Enqueue(result[0]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (cur.Depth >= maxDepth) continue;
                if (!ReferenceEquals(cur.Node, start))
                {
                    if (stopAt != null && stopAt.Test(new NodeOnlyScope(cur.Node, start))) { cur.Stopped = true; continue; }
                    if (through != null && !through.Test(new NodeOnlyScope(cur.Node, start))) { cur.Stopped = true; continue; }
                }
                foreach (var (rel, d) in rels)
                    foreach (var (e, next) in g.Neighbours(cur.Node, rel, d).OrderBy(x => x.Node.Index))
                    {
                        if (!seen.Add(next)) continue;
                        var step = new TraceStep { Node = next, Depth = cur.Depth + 1, Via = e, From = cur.Node, Confidence = cur.Confidence * e.Confidence };
                        result.Add(step);
                        queue.Enqueue(step);
                    }
            }
            return result;
        }

        /// <summary>Runs a named query from the rule set.</summary>
        public static List<TraceStep> Run(LogicResult result, string queryName, Node start)
        {
            var q = result.Rules.Queries.FirstOrDefault(x => string.Equals(x.Name, queryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"no query '{queryName}' (known: {string.Join(", ", result.Rules.Queries.Select(x => x.Name))})");
            return Trace(result.Graph, start, q.Relations, q.Direction, q.Through, q.StopAt);
        }

        /// <summary>Scope of trace predicates: the node's members, <c>self</c>, and <c>start</c> (the trace origin).</summary>
        private sealed class NodeOnlyScope : IExprScope
        {
            private readonly Node _n, _start;
            public NodeOnlyScope(Node n, Node start) { _n = n; _start = start; }
            public bool TryGet(string name, out object? value)
            {
                if (name is "self" or "this") { value = _n; return true; }
                if (name == "start") { value = _start; return true; }
                value = _n.Member(name);
                return true;
            }
        }

        /// <summary>Connected components over the given relations (undirected view), largest first.</summary>
        public static List<List<Node>> Components(LogicGraph g, IEnumerable<string> relations, Func<Node, bool>? filter = null)
        {
            var rels = relations.Select(r => g.Ontology.FindRelation(r)).Where(r => r != null).ToList();
            var seen = new HashSet<Node>();
            var comps = new List<List<Node>>();
            foreach (var n in g.Nodes)
            {
                if (seen.Contains(n) || (filter != null && !filter(n))) continue;
                if (!rels.Any(r => g.Neighbours(n, r, Direction.Both).Any())) continue;
                var comp = new List<Node>();
                var stack = new Stack<Node>();
                stack.Push(n);
                seen.Add(n);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    comp.Add(x);
                    foreach (var r in rels)
                        foreach (var (_, y) in g.Neighbours(x, r, Direction.Both))
                            if ((filter == null || filter(y)) && seen.Add(y)) stack.Push(y);
                }
                comp.Sort((a, b) => a.Index.CompareTo(b.Index));
                comps.Add(comp);
            }
            comps.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : a[0].Index.CompareTo(b[0].Index));
            return comps;
        }

        /// <summary>Objects of class <paramref name="cls"/> with no edge of <paramref name="relation"/> (anti-join, e.g. walls hosting no window).</summary>
        public static List<Node> Without(LogicGraph g, string cls, string relation, Direction dir = Direction.Both, string? otherClass = null)
        {
            var rel = g.Ontology.FindRelation(relation) ?? throw new ArgumentException("unknown relation " + relation);
            var oc = otherClass == null ? null : g.Ontology.FindClass(otherClass);
            return g.OfClass(cls).Where(n => !g.Neighbours(n, rel, dir).Any(x => oc == null || x.Node.IsA(oc))).ToList();
        }
    }
}
