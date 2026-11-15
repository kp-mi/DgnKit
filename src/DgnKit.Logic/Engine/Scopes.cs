using System;
using System.Collections.Generic;
using DgnKit.Logic.Model;
using DgnKit.Logic.Rules;

namespace DgnKit.Logic.Engine
{
    /// <summary>
    /// Functions every rule expression can call in addition to the built-ins, backed by the running graph:
    /// <c>peers(entity?, cls)</c> — objects of class <c>cls</c> in the entity's container (model / layer group);
    /// <c>degree(entity, rel, cls?)</c> — edges of <c>rel</c> (either direction) to objects of <c>cls</c>;
    /// <c>hasEdge(entity, rel, cls?)</c>.
    /// </summary>
    internal sealed class EngineFunctions
    {
        private readonly LogicEngine.RunState _st;
        public readonly Func<object?[], object?> Peers, Degree, HasEdge;
        public Node? Self;

        public EngineFunctions(LogicEngine.RunState st)
        {
            _st = st;
            Peers = a =>
            {
                var (entity, rest) = SplitEntity(a);
                if (entity == null || rest.Length < 1 || Expr.ToStr(rest[0]) is not string cls) return null;
                return (double)_st.PeerCount(entity, cls);
            };
            Degree = a =>
            {
                var (entity, rest) = SplitEntity(a);
                if (entity == null || rest.Length < 1 || Expr.ToStr(rest[0]) is not string rel) return null;
                return (double)_st.Degree(entity, rel, rest.Length > 1 ? Expr.ToStr(rest[1]) : null);
            };
            HasEdge = a => Degree(a) is double d && d > 0;
            Linked = a =>
            {
                if (a.Length < 2 || a[0] is not Node x || a[1] is not Node y) return false;
                foreach (var e in x.Out) if (ReferenceEquals(e.To, y)) return true;
                foreach (var e in x.In) if (ReferenceEquals(e.From, y)) return true;
                return false;
            };
        }

        /// <summary><c>linked(a, b)</c>: any edge (any relation, either direction) between a and b.</summary>
        public readonly Func<object?[], object?> Linked;

        /// <summary>
        /// <c>peersWhere(entity?, cls, condition)</c>: objects of class cls in the entity's container for which the condition
        /// (an expression string; the peer's members are bare names, <c>self</c> is the entity) holds — e.g.
        /// <c>peersWhere('LRS.Route', 'RouteID == self.RouteID and self.ARM &gt;= BARM and self.ARM &lt;= EARM')</c>.
        /// </summary>
        public Func<object?[], object?> PeersWhere => a =>
        {
            var (entity, rest) = SplitEntity(a);
            if (entity == null || rest.Length < 2 || Expr.ToStr(rest[0]) is not string cls || Expr.ToStr(rest[1]) is not string cond) return null;
            var expr = _st.CompiledCondition(cond);
            int k = 0;
            foreach (var p in _st.PeersOf(entity, cls))
                if (!ReferenceEquals(p, entity) && expr.Test(new PeerScope(p, entity))) k++;
            return (double)k;
        };

        private sealed class PeerScope : IExprScope
        {
            private readonly Node _peer, _self;
            public PeerScope(Node peer, Node self) { _peer = peer; _self = self; }
            public bool TryGet(string name, out object? value)
            {
                if (name is "self") { value = _self; return true; }
                if (name is "peer" or "this") { value = _peer; return true; }
                value = _peer.Member(name);
                return true;
            }
        }

        /// <summary><c>common(a, b, cls?)</c>: number of nodes (of class cls) adjacent to both a and b — e.g. a shared manhole.</summary>
        public Func<object?[], object?> Common => a =>
        {
            if (a.Length < 2 || a[0] is not Node x || a[1] is not Node y) return null;
            string? cls = a.Length > 2 ? Expr.ToStr(a[2]) : null;
            var nx = new HashSet<Node>();
            foreach (var e in x.Out) nx.Add(e.To);
            foreach (var e in x.In) nx.Add(e.From);
            int k = 0;
            var seen = new HashSet<Node>();
            foreach (var e in y.Out) if (nx.Contains(e.To) && seen.Add(e.To) && (cls == null || e.To.IsA(cls))) k++;
            foreach (var e in y.In) if (nx.Contains(e.From) && seen.Add(e.From) && (cls == null || e.From.IsA(cls))) k++;
            return (double)k;
        };

        private (Node? Entity, object?[] Args) SplitEntity(object?[] a)
        {
            if (a.Length > 0 && a[0] is Node n)
            {
                var rest = new object?[a.Length - 1];
                Array.Copy(a, 1, rest, 0, rest.Length);
                return (n, rest);
            }
            return (Self, a);
        }

        public bool TryGet(string name, out object? value)
        {
            switch (name)
            {
                case "fn:peers": value = Peers; return true;
                case "fn:degree": value = Degree; return true;
                case "fn:hasEdge": case "fn:hasedge": value = HasEdge; return true;
                case "fn:linked": value = Linked; return true;
                case "fn:common": value = Common; return true;
                case "fn:peersWhere": case "fn:peerswhere": value = PeersWhere; return true;
            }
            value = null;
            return false;
        }
    }

    /// <summary>Scope of node-level expressions (classify, compute, require/expect gates, node checks): members of the node are bare names.</summary>
    internal sealed class NodeScope : IExprScope
    {
        private readonly EngineFunctions _fn;
        public Node Node = null!;
        public NodeScope(EngineFunctions fn) { _fn = fn; }

        public NodeScope With(Node n) { Node = n; _fn.Self = n; return this; }

        public bool TryGet(string name, out object? value)
        {
            if (name.StartsWith("fn:", StringComparison.Ordinal)) return _fn.TryGet(name, out value);
            switch (name.ToLowerInvariant())
            {
                case "self": case "this": case "feature": case "subject": value = Node; return true;
            }
            value = Node.Member(name);
            return true;
        }
    }

    /// <summary>Scope of pair expressions (relate where/evidence/tolerance/reverseWhen, edge checks).</summary>
    internal sealed class PairScope : IExprScope
    {
        private readonly EngineFunctions _fn;
        public Node Subject = null!, Object = null!;
        public Edge? Edge;
        public readonly Dictionary<string, object?> Vars = new(StringComparer.OrdinalIgnoreCase);

        public PairScope(EngineFunctions fn) { _fn = fn; }

        public PairScope With(Node s, Node o, Edge? e = null)
        {
            Subject = s; Object = o; Edge = e;
            _fn.Self = s;
            Vars.Clear();
            return this;
        }

        public bool TryGet(string name, out object? value)
        {
            if (name.StartsWith("fn:", StringComparison.Ordinal)) return _fn.TryGet(name, out value);
            switch (name.ToLowerInvariant())
            {
                case "subject": case "s": value = Subject; return true;
                case "object": case "o": value = Object; return true;
                case "edge":
                    value = Edge == null ? null : new Dictionary<string, object?>
                    {
                        ["confidence"] = Edge.Confidence,
                        ["rule"] = Edge.Rule,
                        ["status"] = Edge.Status.ToString().ToLowerInvariant(),
                        ["relation"] = Edge.Relation.Name,
                    };
                    return true;
            }
            if (Vars.TryGetValue(name, out value)) return true;
            if (Edge != null && Edge.Evidence.TryGetValue(name, out value)) return true;
            value = null;
            return true;
        }
    }
}
