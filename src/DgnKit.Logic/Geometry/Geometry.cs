using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DgnKit.Logic.Geometry
{
    /// <summary>A point / vector in metres (world frame of the source).</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z = 0) { X = x; Y = y; Z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double Length2D => Math.Sqrt(X * X + Y * Y);
        public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>Axis-aligned box. <see cref="Empty"/> has Min &gt; Max.</summary>
    public readonly struct Box3
    {
        public readonly Vec3 Min, Max;
        public Box3(Vec3 min, Vec3 max) { Min = min; Max = max; }
        public static readonly Box3 Empty = new(new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), new Vec3(double.MinValue, double.MinValue, double.MinValue));
        public bool IsEmpty => Min.X > Max.X;
        public Vec3 Center => new((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);
        public double SizeX => IsEmpty ? 0 : Max.X - Min.X;
        public double SizeY => IsEmpty ? 0 : Max.Y - Min.Y;
        public double SizeZ => IsEmpty ? 0 : Max.Z - Min.Z;
        /// <summary>Plan (XY) diagonal — the characteristic size of a symbol.</summary>
        public double Diagonal2D => IsEmpty ? 0 : Math.Sqrt(SizeX * SizeX + SizeY * SizeY);
        public double Area2D => SizeX * SizeY;

        public Box3 Extend(Vec3 p) => IsEmpty ? new Box3(p, p) : new Box3(
            new Vec3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z)),
            new Vec3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z)));

        public Box3 Extend(Box3 b) => b.IsEmpty ? this : IsEmpty ? b : Extend(b.Min).Extend(b.Max);
        public Box3 Inflate(double d) => IsEmpty ? this : new Box3(Min - new Vec3(d, d, d), Max + new Vec3(d, d, d));

        public bool Intersects(Box3 o, bool use3d = true) =>
            !IsEmpty && !o.IsEmpty && Min.X <= o.Max.X && Max.X >= o.Min.X && Min.Y <= o.Max.Y && Max.Y >= o.Min.Y &&
            (!use3d || (Min.Z <= o.Max.Z && Max.Z >= o.Min.Z));

        public bool Contains(Vec3 p, bool use3d = true) =>
            p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && (!use3d || (p.Z >= Min.Z && p.Z <= Max.Z));

        /// <summary>Area of the XY overlap of two boxes.</summary>
        public double OverlapArea2D(Box3 o)
        {
            double w = Math.Min(Max.X, o.Max.X) - Math.Max(Min.X, o.Min.X);
            double h = Math.Min(Max.Y, o.Max.Y) - Math.Max(Min.Y, o.Min.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        /// <summary>Gap between the boxes in XY (0 when they overlap).</summary>
        public double Distance2D(Box3 o)
        {
            double dx = Math.Max(0, Math.Max(o.Min.X - Max.X, Min.X - o.Max.X));
            double dy = Math.Max(0, Math.Max(o.Min.Y - Max.Y, Min.Y - o.Max.Y));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public override string ToString() => IsEmpty ? "(empty)" : $"{Min}..{Max}";
    }

    public enum PartKind { Point, Polyline, Polygon }

    /// <summary>One geometric part of a feature: a point, an open polyline, or a closed ring (polygon outline).</summary>
    public sealed class GeometryPart
    {
        public PartKind Kind { get; }
        public IReadOnlyList<Vec3> Points { get; }
        public GeometryPart(PartKind kind, IReadOnlyList<Vec3> points)
        {
            Kind = kind;
            Points = points;
        }
        public static GeometryPart Point(Vec3 p) => new(PartKind.Point, new[] { p });
        public static GeometryPart Line(IEnumerable<Vec3> pts) => new(PartKind.Polyline, pts.ToArray());
        public static GeometryPart Ring(IEnumerable<Vec3> pts) => new(PartKind.Polygon, pts.ToArray());

        /// <summary>Segments of the part (a ring is closed implicitly). A point yields one degenerate segment.</summary>
        public IEnumerable<(Vec3 A, Vec3 B)> Segments()
        {
            var p = Points;
            if (p.Count == 1) { yield return (p[0], p[0]); yield break; }
            for (int i = 0; i + 1 < p.Count; i++) yield return (p[i], p[i + 1]);
            if (Kind == PartKind.Polygon && p.Count > 2 && !p[0].Equals(p[p.Count - 1])) yield return (p[p.Count - 1], p[0]);
        }
    }

    /// <summary>
    /// Feature geometry in metres: parts plus a cached box. Linear features expose start/end points (first/last vertex of
    /// the longest polyline) for connectivity tests.
    /// </summary>
    public sealed class FeatureGeometry
    {
        public List<GeometryPart> Parts { get; } = new();
        private Box3? _box;
        private double? _length;

        /// <summary>Optional explicit 3D extent (e.g. a wall polyline extruded by a height attribute).</summary>
        public Box3? ExplicitBox { get; set; }

        public Box3 Box
        {
            get
            {
                if (_box is Box3 b) return b;
                var r = Box3.Empty;
                foreach (var part in Parts) foreach (var p in part.Points) r = r.Extend(p);
                if (ExplicitBox is Box3 e) r = r.Extend(e);
                _box = r;
                return r;
            }
        }

        public void Invalidate() { _box = null; _length = null; }

        public bool IsEmpty => Parts.Count == 0 && ExplicitBox == null;
        public bool HasLines => Parts.Any(p => p.Kind == PartKind.Polyline);
        public bool HasPolygons => Parts.Any(p => p.Kind == PartKind.Polygon);
        public bool IsPointLike => Parts.Count > 0 && Parts.All(p => p.Kind == PartKind.Point);

        /// <summary>Total plan length of polylines and ring outlines.</summary>
        public double Length
        {
            get
            {
                if (_length is double l) return l;
                double s = 0;
                foreach (var part in Parts)
                    if (part.Kind != PartKind.Point)
                        foreach (var (a, b) in part.Segments()) s += (b - a).Length2D;
                _length = s;
                return s;
            }
        }

        /// <summary>The main linear part (longest polyline), or null.</summary>
        public GeometryPart? MainLine
        {
            get
            {
                GeometryPart? best = null;
                double bestLen = -1;
                foreach (var part in Parts)
                {
                    if (part.Kind != PartKind.Polyline || part.Points.Count < 2) continue;
                    double len = 0;
                    foreach (var (a, b) in part.Segments()) len += (b - a).Length;
                    if (len > bestLen) { bestLen = len; best = part; }
                }
                return best;
            }
        }

        public Vec3? Start => MainLine?.Points[0];
        public Vec3? End { get { var m = MainLine; return m?.Points[m.Points.Count - 1]; } }

        /// <summary>A representative location: point, polyline midpoint or box centre.</summary>
        public Vec3 Anchor
        {
            get
            {
                if (IsPointLike) return Parts[0].Points[0];
                return Box.Center;
            }
        }

        public IEnumerable<(Vec3 A, Vec3 B)> AllSegments()
        {
            foreach (var part in Parts) foreach (var s in part.Segments()) yield return s;
        }
    }

    /// <summary>Exact distance and containment predicates.</summary>
    public static class GeoOps
    {
        /// <summary>Distance from p to segment ab (XY only when <paramref name="use3d"/> is false) and the parameter t∈[0,1].</summary>
        public static double PointSegment(Vec3 p, Vec3 a, Vec3 b, bool use3d, out double t)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = use3d ? b.Z - a.Z : 0;
            double len2 = dx * dx + dy * dy + dz * dz;
            t = 0;
            if (len2 > 0)
            {
                t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (use3d ? (p.Z - a.Z) * dz : 0)) / len2;
                t = Math.Max(0, Math.Min(1, t));
            }
            double qx = a.X + t * dx - p.X, qy = a.Y + t * dy - p.Y, qz = use3d ? a.Z + t * dz - p.Z : 0;
            return Math.Sqrt(qx * qx + qy * qy + qz * qz);
        }

        /// <summary>Distance from a point to the geometry (to segments; 0 inside a ring in XY).</summary>
        public static double PointToGeometry(Vec3 p, FeatureGeometry g, bool use3d, out Vec3 closest)
        {
            double best = double.MaxValue;
            closest = p;
            foreach (var part in g.Parts)
            {
                if (part.Kind == PartKind.Polygon && InRing(p, part.Points)) { closest = p; return 0; }
                foreach (var (a, b) in part.Segments())
                {
                    double d = PointSegment(p, a, b, use3d, out double t);
                    if (d < best) { best = d; closest = a + (b - a) * t; }
                }
            }
            if (g.Parts.Count == 0 && g.ExplicitBox is Box3 box)
            {
                var c = new Vec3(Clamp(p.X, box.Min.X, box.Max.X), Clamp(p.Y, box.Min.Y, box.Max.Y), use3d ? Clamp(p.Z, box.Min.Z, box.Max.Z) : p.Z);
                closest = c;
                return (c - p).Length;
            }
            return best;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Minimum distance between two geometries (segment–segment), 0 when they cross or one contains the other.</summary>
        public static double Distance(FeatureGeometry g1, FeatureGeometry g2, bool use3d)
        {
            if (g1.IsEmpty || g2.IsEmpty) return double.MaxValue;
            if (g1.Parts.Count == 0 || g2.Parts.Count == 0)
            {
                // box-only geometry (e.g. an extruded solid with no outline): box gap
                return use3d ? BoxGap3D(g1.Box, g2.Box) : g1.Box.Distance2D(g2.Box);
            }
            double best = double.MaxValue;
            foreach (var p1 in g1.Parts)
            foreach (var p2 in g2.Parts)
            {
                if (p2.Kind == PartKind.Polygon && p1.Points.Count > 0 && InRing(p1.Points[0], p2.Points)) return 0;
                if (p1.Kind == PartKind.Polygon && p2.Points.Count > 0 && InRing(p2.Points[0], p1.Points)) return 0;
                foreach (var (a, b) in p1.Segments())
                foreach (var (c, d) in p2.Segments())
                {
                    double s = SegmentSegment(a, b, c, d, use3d);
                    if (s < best) { best = s; if (best == 0) return 0; }
                }
            }
            return best;
        }

        public static double BoxGap3D(Box3 a, Box3 b)
        {
            double dx = Math.Max(0, Math.Max(b.Min.X - a.Max.X, a.Min.X - b.Max.X));
            double dy = Math.Max(0, Math.Max(b.Min.Y - a.Max.Y, a.Min.Y - b.Max.Y));
            double dz = Math.Max(0, Math.Max(b.Min.Z - a.Max.Z, a.Min.Z - b.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Segment–segment distance. In 2D an exact crossing gives 0.</summary>
        public static double SegmentSegment(Vec3 a, Vec3 b, Vec3 c, Vec3 d, bool use3d)
        {
            if (!use3d && SegmentsCross2D(a, b, c, d)) return 0;
            if (use3d)
            {
                // closest points of two 3D segments (Ericson, Real-Time Collision Detection 5.1.9)
                var d1 = b - a; var d2 = d - c; var r = a - c;
                double aa = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
                double s, t;
                if (aa <= 1e-18 && e <= 1e-18) return (a - c).Length;
                if (aa <= 1e-18) { s = 0; t = Clamp(f / e, 0, 1); }
                else
                {
                    double cc = d1.Dot(r);
                    if (e <= 1e-18) { t = 0; s = Clamp(-cc / aa, 0, 1); }
                    else
                    {
                        double bb = d1.Dot(d2), denom = aa * e - bb * bb;
                        s = denom != 0 ? Clamp((bb * f - cc * e) / denom, 0, 1) : 0;
                        t = (bb * s + f) / e;
                        if (t < 0) { t = 0; s = Clamp(-cc / aa, 0, 1); }
                        else if (t > 1) { t = 1; s = Clamp((bb - cc) / aa, 0, 1); }
                    }
                }
                return ((a + d1 * s) - (c + d2 * t)).Length;
            }
            return Math.Min(Math.Min(PointSegment(a, c, d, false, out _), PointSegment(b, c, d, false, out _)),
                            Math.Min(PointSegment(c, a, b, false, out _), PointSegment(d, a, b, false, out _)));
        }

        public static bool SegmentsCross2D(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            double o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
            return ((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0));
        }

        private static double Orient(Vec3 a, Vec3 b, Vec3 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>True when the two geometries properly cross in plan (some segment pair intersects).</summary>
        public static bool Crosses2D(FeatureGeometry g1, FeatureGeometry g2)
        {
            if (!g1.Box.Intersects(g2.Box, false)) return false;
            foreach (var (a, b) in g1.AllSegments())
            foreach (var (c, d) in g2.AllSegments())
                if (SegmentsCross2D(a, b, c, d)) return true;
            return false;
        }

        /// <summary>Even–odd point-in-ring test in XY.</summary>
        public static bool InRing(Vec3 p, IReadOnlyList<Vec3> ring)
        {
            bool inside = false;
            int n = ring.Count;
            if (n < 3) return false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>Unit direction of a polyline near parameter point q (the segment closest to q), XY.</summary>
        public static Vec3 DirectionAt(GeometryPart line, Vec3 q)
        {
            double best = double.MaxValue;
            Vec3 dir = new(1, 0, 0);
            foreach (var (a, b) in line.Segments())
            {
                double d = PointSegment(q, a, b, false, out _);
                if (d < best)
                {
                    best = d;
                    var v = b - a;
                    double l = v.Length2D;
                    if (l > 0) dir = new Vec3(v.X / l, v.Y / l, 0);
                }
            }
            return dir;
        }

        /// <summary>Acute angle (degrees, 0..90) between the main directions of two linear geometries.</summary>
        public static double AngleBetween(FeatureGeometry g1, FeatureGeometry g2)
        {
            var l1 = g1.MainLine; var l2 = g2.MainLine;
            if (l1 == null || l2 == null) return double.NaN;
            var a = l1.Points[l1.Points.Count - 1] - l1.Points[0];
            var mid = g1.Box.Center;
            var b = DirectionAt(l2, mid);
            double la = a.Length2D;
            if (la == 0) return double.NaN;
            double cos = Math.Abs((a.X * b.X + a.Y * b.Y) / la);
            return Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
        }

        /// <summary>Signed area (XY) of a ring.</summary>
        public static double RingArea(IReadOnlyList<Vec3> ring)
        {
            double s = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) s += (ring[j].X + ring[i].X) * (ring[j].Y - ring[i].Y);
            return Math.Abs(s) / 2;
        }
    }
}
