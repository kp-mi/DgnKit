using System;
using System.Collections.Generic;
using DgnKit.Logic.Geometry;

namespace DgnKit.Logic.Spatial
{
    /// <summary>
    /// Static, bulk-loaded R-tree over axis-aligned boxes (Sort-Tile-Recursive packing; Leutenegger et al. 1997).
    /// Build is O(n log n); a window query visits O(log n + k) nodes. Items are ints (node indices in the graph).
    /// Plan-oriented: packing tiles on X then Y; Z is kept in the boxes for 3D queries.
    /// </summary>
    public sealed class StrTree
    {
        private const int Fanout = 16;

        private struct Node
        {
            public Box3 Box;
            public int First;   // index into _children (internal) or _items (leaf)
            public int Count;
            public bool Leaf;
        }

        private readonly List<Node> _nodes = new();
        private readonly List<int> _children = new();
        private readonly int[] _items;
        private readonly Box3[] _boxes;
        private readonly int _root = -1;

        public int Count => _items.Length;

        public StrTree(IReadOnlyList<(int Item, Box3 Box)> entries)
        {
            var list = new List<(int Item, Box3 Box)>(entries.Count);
            foreach (var e in entries) if (!e.Box.IsEmpty) list.Add(e);
            _items = new int[list.Count];
            _boxes = new Box3[list.Count];
            if (list.Count == 0) return;

            // leaf level: STR-sort the entries, then cut into runs of Fanout
            var order = new int[list.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            var centers = new Vec3[list.Count];
            for (int i = 0; i < list.Count; i++) centers[i] = list[i].Box.Center;
            StrSort(order, centers);
            for (int i = 0; i < order.Length; i++) { _items[i] = list[order[i]].Item; _boxes[i] = list[order[i]].Box; }

            var level = new List<int>();
            for (int i = 0; i < _items.Length; i += Fanout)
            {
                int n = Math.Min(Fanout, _items.Length - i);
                var b = Box3.Empty;
                for (int k = 0; k < n; k++) b = b.Extend(_boxes[i + k]);
                _nodes.Add(new Node { Box = b, First = i, Count = n, Leaf = true });
                level.Add(_nodes.Count - 1);
            }

            while (level.Count > 1)
            {
                var idx = level.ToArray();
                var c = new Vec3[idx.Length];
                for (int i = 0; i < idx.Length; i++) c[i] = _nodes[idx[i]].Box.Center;
                var ord = new int[idx.Length];
                for (int i = 0; i < ord.Length; i++) ord[i] = i;
                StrSort(ord, c);
                var next = new List<int>();
                for (int i = 0; i < ord.Length; i += Fanout)
                {
                    int n = Math.Min(Fanout, ord.Length - i);
                    var b = Box3.Empty;
                    int first = _children.Count;
                    for (int k = 0; k < n; k++)
                    {
                        int child = idx[ord[i + k]];
                        _children.Add(child);
                        b = b.Extend(_nodes[child].Box);
                    }
                    _nodes.Add(new Node { Box = b, First = first, Count = n, Leaf = false });
                    next.Add(_nodes.Count - 1);
                }
                level = next;
            }
            _root = level[0];
        }

        /// <summary>Sorts indices by X, then within vertical slices by Y (the STR tiling).</summary>
        private static void StrSort(int[] order, Vec3[] centers)
        {
            Array.Sort(order, (a, b) => centers[a].X.CompareTo(centers[b].X));
            int leaves = (order.Length + Fanout - 1) / Fanout;
            int slices = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(leaves)));
            int sliceSize = slices * Fanout;
            for (int s = 0; s < order.Length; s += sliceSize)
            {
                int n = Math.Min(sliceSize, order.Length - s);
                Array.Sort(order, s, n, Comparer<int>.Create((a, b) => centers[a].Y.CompareTo(centers[b].Y)));
            }
        }

        /// <summary>Visits every item whose box intersects <paramref name="window"/>.</summary>
        public void Query(Box3 window, bool use3d, Action<int> visit)
        {
            if (_root < 0 || window.IsEmpty) return;
            var stack = new Stack<int>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                var n = _nodes[stack.Pop()];
                if (!n.Box.Intersects(window, use3d)) continue;
                if (n.Leaf)
                {
                    for (int k = 0; k < n.Count; k++)
                        if (_boxes[n.First + k].Intersects(window, use3d)) visit(_items[n.First + k]);
                }
                else
                {
                    for (int k = 0; k < n.Count; k++) stack.Push(_children[n.First + k]);
                }
            }
        }

        public List<int> Query(Box3 window, bool use3d = false)
        {
            var r = new List<int>();
            Query(window, use3d, r.Add);
            return r;
        }
    }
}
