using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DgnKit.Logic.Rules;

namespace DgnKit.Logic.Model
{
    /// <summary>One classification candidate of a node, with the rule that proposed it.</summary>
    public sealed class Classification
    {
        public ClassDef Class { get; }
        public double Confidence { get; }
        public string Rule { get; }
        public Classification(ClassDef cls, double confidence, string rule) { Class = cls; Confidence = confidence; Rule = rule; }
        public override string ToString() => $"{Class.Name} {Confidence:0.00} ({Rule})";
    }

    /// <summary>A typed engineering object in the graph (wraps the source <see cref="Feature"/>).</summary>
    public sealed class Node : IExprEntity
    {
        public int Index { get; }
        public Feature Feature { get; }
        public string Key => Feature.Key;
        /// <summary>Winning class, or null when the object was not recognised.</summary>
        public ClassDef? Class { get; internal set; }
        public double ClassConfidence { get; internal set; }
        public string? ClassRule { get; internal set; }
        /// <summary>All candidates proposed by classify rules (best first).</summary>
        public List<Classification> Candidates { get; } = new();
        internal readonly List<Edge> OutEdges = new();
        internal readonly List<Edge> InEdges = new();
        /// <summary>Values derived during the run (e.g. parsed sign size), readable like properties.</summary>
        public Dictionary<string, object?> Derived { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Node(int index, Feature feature) { Index = index; Feature = feature; }

        public IReadOnlyList<Edge> Out => OutEdges;
        public IReadOnlyList<Edge> In => InEdges;

        public bool IsA(string className)
        {
            if (Class == null) return false;
            foreach (var c in Class.SelfAndAncestors()) if (string.Equals(c.Name, className, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool IsA(ClassDef? cls) => cls != null && Class != null && Class.IsA(cls);

        /// <summary>
        /// Members readable from rules. Pseudo-members first (<c>key class layer layers cell text kind source container group
        /// length size width height zmin zmax area startZ endZ x y confidence</c>), then derived values, properties, and context.
        /// </summary>
        public object? Member(string name)
        {
            var f = Feature;
            var g = f.Geometry;
            switch (name.ToLowerInvariant())
            {
                case "key": return f.Key;
                case "class": return Class?.Name;
                case "domain": return Class?.Domain;
                case "layer": case "level": return f.Layer;
                case "layers": case "levels": return f.Layers.Cast<object?>().ToList();
                case "cell": return f.Cell;
                case "text": return f.Text;
                case "kind": return f.Kind;
                case "source": return f.Source;
                case "container": case "model": return f.Container;
                case "group": return f.Group == 0 ? null : (double)f.Group;
                case "length": return g.Length;
                case "size": return g.Box.Diagonal2D;
                case "width": return g.Box.SizeX;
                case "depth": return g.Box.SizeY;
                case "height": return Derived.TryGetValue("height", out var h) ? h : f.Properties.TryGetValue("height", out var ph) ? ph : g.Box.SizeZ;
                case "zmin": return g.Box.Min.Z;
                case "zmax": return g.Box.Max.Z;
                case "x": return g.Anchor.X;
                case "y": return g.Anchor.Y;
                case "z": return g.Anchor.Z;
                case "area": return g.Parts.Where(p => p.Kind == Geometry.PartKind.Polygon).Sum(p => Geometry.GeoOps.RingArea(p.Points));
                case "startz": return g.Start?.Z;
                case "endz": return g.End?.Z;
                case "confidence": return ClassConfidence;
                case "degree": return (double)(OutEdges.Count + InEdges.Count);
                case "islinear": return g.HasLines && !g.HasPolygons;
                case "ispoint": return g.IsPointLike;
                case "isareal": return g.HasPolygons;
            }
            if (Derived.TryGetValue(name, out var d)) return d;
            if (f.Properties.TryGetValue(name, out var v)) return v;
            if (f.Context.TryGetValue(name, out var c)) return c;
            return null;
        }

        public override string ToString() => $"{Key} [{Class?.Name ?? "?"}]";
    }

    public enum EdgeStatus { Asserted, Inferred, Derived }

    /// <summary>A relationship between two nodes, with how it came to be.</summary>
    public sealed class Edge
    {
        public int Index { get; internal set; }
        public Node From { get; }
        public Node To { get; }
        public RelationDef Relation { get; }
        public EdgeStatus Status { get; internal set; }
        public double Confidence { get; internal set; }
        /// <summary>Rule id that created the edge (or "source" for asserted data).</summary>
        public string Rule { get; internal set; }
        /// <summary>Measured values and evidence terms behind the decision (distance, weights, runner-up …).</summary>
        public Dictionary<string, object?> Evidence { get; } = new(StringComparer.Ordinal);
        /// <summary>Set when a cardinality-one slot had a close runner-up.</summary>
        public bool Ambiguous { get; internal set; }

        internal Edge(Node from, Node to, RelationDef rel, EdgeStatus status, double confidence, string rule)
        {
            From = from; To = to; Relation = rel; Status = status; Confidence = confidence; Rule = rule;
        }

        public Node Other(Node n) => ReferenceEquals(n, From) ? To : From;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0} -{1}-> {2} ({3} {4:0.00} {5})", From.Key, Relation.Name, To.Key, Status, Confidence, Rule);
    }

    /// <summary>
    /// The in-memory engineering graph: nodes (typed objects) and labelled, directed, attributed edges, with indices by
    /// class and relation. One edge per (from, relation, to); a second derivation of the same edge merges into it
    /// (noisy-OR of confidences, both rules recorded).
    /// </summary>
    public sealed class LogicGraph
    {
        private readonly List<Node> _nodes = new();
        private readonly List<Edge> _edges = new();
        private readonly Dictionary<string, Node> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<(int, string, int), Edge> _edgeIndex = new();

        public Ontology Ontology { get; }
        public IReadOnlyList<Node> Nodes => _nodes;
        public IReadOnlyList<Edge> Edges => _edges;

        public LogicGraph(Ontology ontology) { Ontology = ontology; }

        public Node AddNode(Feature f)
        {
            if (_byKey.TryGetValue(f.Key, out var existing)) return existing;
            var n = new Node(_nodes.Count, f);
            _nodes.Add(n);
            _byKey[f.Key] = n;
            return n;
        }

        public Node? Find(string key) => _byKey.TryGetValue(key, out var n) ? n : null;

        public IEnumerable<Node> OfClass(ClassDef? cls) => cls == null ? Enumerable.Empty<Node>() : _nodes.Where(n => n.IsA(cls));
        public IEnumerable<Node> OfClass(string cls) => OfClass(Ontology.FindClass(cls));

        public Edge? FindEdge(Node from, RelationDef rel, Node to)
        {
            if (_edgeIndex.TryGetValue((from.Index, rel.Name, to.Index), out var e)) return e;
            if (rel.Symmetric && _edgeIndex.TryGetValue((to.Index, rel.Name, from.Index), out e)) return e;
            return null;
        }

        /// <summary>Adds an edge, or merges into the existing one (noisy-OR confidence, strongest status kept).</summary>
        public Edge AddEdge(Node from, RelationDef rel, Node to, EdgeStatus status, double confidence, string rule)
        {
            var e = FindEdge(from, rel, to);
            if (e != null)
            {
                e.Confidence = 1 - (1 - e.Confidence) * (1 - confidence);
                if (status < e.Status) e.Status = status;
                if (!e.Rule.Split('+').Contains(rule)) e.Rule += "+" + rule;
                return e;
            }
            e = new Edge(from, to, rel, status, confidence, rule) { Index = _edges.Count };
            _edges.Add(e);
            _edgeIndex[(from.Index, rel.Name, to.Index)] = e;
            from.OutEdges.Add(e);
            to.InEdges.Add(e);
            return e;
        }

        public void RemoveEdge(Edge e)
        {
            if (!_edgeIndex.Remove((e.From.Index, e.Relation.Name, e.To.Index))) return;
            e.From.OutEdges.Remove(e);
            e.To.InEdges.Remove(e);
            _edges.Remove(e);
            for (int i = 0; i < _edges.Count; i++) _edges[i].Index = i;
        }

        /// <summary>
        /// Neighbours of <paramref name="n"/> over <paramref name="rel"/> (null = any): outgoing, incoming, or both.
        /// Symmetric relations are always traversed both ways.
        /// </summary>
        public IEnumerable<(Edge Edge, Node Node)> Neighbours(Node n, RelationDef? rel, Direction dir)
        {
            if (dir != Direction.In)
                foreach (var e in n.OutEdges)
                    if (rel == null || ReferenceEquals(e.Relation, rel)) yield return (e, e.To);
            if (dir != Direction.Out || (rel?.Symmetric ?? false))
                foreach (var e in n.InEdges)
                    if ((rel == null || ReferenceEquals(e.Relation, rel)) && (dir != Direction.Out || e.Relation.Symmetric)) yield return (e, e.From);
            if (dir == Direction.In && rel != null && rel.Symmetric)
                foreach (var e in n.OutEdges)
                    if (ReferenceEquals(e.Relation, rel)) yield return (e, e.To);
        }

        public IEnumerable<Edge> EdgesOf(RelationDef rel) => _edges.Where(e => ReferenceEquals(e.Relation, rel));
    }

    public enum Direction { Out, In, Both }
}
