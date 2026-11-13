using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;

namespace DgnKit.Graphics
{
    /// <summary>Curve evaluation / stroking helpers (arcs, MicroStation "curve" elements, NURBS).</summary>
    public static class Curves
    {
        public static List<DPoint3d> StrokeArc(ArcElement a, double chordTolUor)
        {
            double r = Math.Max(Math.Abs(a.PrimaryAxis), Math.Abs(a.SecondaryAxis));
            int n = 16;
            if (chordTolUor > 0 && r > 0)
            {
                double dTheta = 2 * Math.Acos(Math.Max(-1, Math.Min(1, 1 - chordTolUor / r)));
                if (dTheta > 1e-6) n = Math.Max(4, (int)Math.Ceiling(Math.Abs(a.SweepAngle) / dTheta));
            }
            n = Math.Min(Math.Max(n, 4), 1024);
            var pts = new List<DPoint3d>(n + 1);
            for (int i = 0; i <= n; i++) pts.Add(a.PointAt(a.StartAngle + a.SweepAngle * i / n));
            return pts;
        }

        /// <summary>
        /// MicroStation "curve" (type 11): an interpolating curve through vertices 3..n-2; the first two and last two
        /// vertices only control end tangents. Approximated with a Catmull-Rom spline.
        /// </summary>
        public static List<DPoint3d> StrokeCurveElement(IList<DPoint3d> p, int segPerSpan = 8)
        {
            var o = new List<DPoint3d>();
            if (p.Count < 5) { o.AddRange(p); return o; }
            for (int i = 2; i < p.Count - 3; i++)
            {
                var p0 = p[i - 1]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[i + 2];
                for (int k = 0; k < segPerSpan; k++)
                {
                    double t = (double)k / segPerSpan, t2 = t * t, t3 = t2 * t;
                    o.Add((p1 * 2 + (p2 - p0) * t + (p0 * 2 - p1 * 5 + p2 * 4 - p3) * t2 + (p1 * 3 - p0 - p2 * 3 + p3) * t3) * 0.5);
                }
            }
            o.Add(p[p.Count - 3]);
            return o;
        }

        /// <summary>Strokes a B-spline curve element (MicroStation knot conventions) into points.</summary>
        public static List<DPoint3d> StrokeBSpline(BSplineCurveElement bc, int samplesPerSpan)
        {
            var poles = bc.Poles.ToList();
            int order = Math.Max(2, bc.Order);
            if (poles.Count < 2) return poles;
            if (order > poles.Count) order = poles.Count;
            var w = bc.Rational && bc.Weights.Count == poles.Count ? bc.Weights.ToList() : null;
            // MicroStation stores rational poles pre-multiplied by their weights (homogeneous form);
            // De Boor runs on the weighted poles and the result is divided by the interpolated weight.

            List<double> knots;
            if (bc.Closed)
            {
                // periodic: wrap order-1 poles and use uniform knots
                for (int i = 0; i < order - 1; i++) { poles.Add(poles[i]); w?.Add(w[i]); }
                int nk = poles.Count + order;
                knots = Enumerable.Range(0, nk).Select(i => (double)i).ToList();
            }
            else if (!bc.InteriorKnotsOnly && bc.Knots.Count == poles.Count + order)
            {
                knots = bc.Knots.ToList();
            }
            else
            {
                int interior = poles.Count - order;
                var inner = bc.Knots.Count == interior ? bc.Knots.ToList()
                    : Enumerable.Range(1, Math.Max(0, interior)).Select(i => (double)i / (interior + 1)).ToList();
                knots = Enumerable.Repeat(0.0, order).Concat(inner).Concat(Enumerable.Repeat(1.0, order)).ToList();
            }

            int n = poles.Count;
            double t0 = knots[order - 1], t1 = knots[n];
            int spans = Math.Max(1, n - order + 1);
            int samples = Math.Max(16, (samplesPerSpan > 0 ? samplesPerSpan : 12) * spans);
            samples = Math.Min(samples, 4000);
            var outPts = new List<DPoint3d>(samples + 1);
            for (int i = 0; i <= samples; i++)
            {
                double t = t0 + (t1 - t0) * i / samples;
                outPts.Add(DeBoor(poles, w, knots, order, t));
            }
            return outPts;
        }

        private static DPoint3d DeBoor(List<DPoint3d> P, List<double>? W, List<double> U, int order, double t)
        {
            int p = order - 1, n = P.Count;
            int k = p;
            while (k < n - 1 && t >= U[k + 1]) k++;
            var d = new DPoint3d[p + 1];
            var dw = new double[p + 1];
            for (int j = 0; j <= p; j++)
            {
                int idx = Math.Max(0, Math.Min(n - 1, j + k - p));
                d[j] = P[idx];
                dw[j] = W != null ? W[idx] : 1.0;
            }
            for (int r = 1; r <= p; r++)
            {
                for (int j = p; j >= r; j--)
                {
                    int i = j + k - p;
                    double denom = U[Math.Min(U.Count - 1, i + p - r + 1)] - U[Math.Max(0, i)];
                    double alpha = denom == 0 ? 0 : (t - U[Math.Max(0, i)]) / denom;
                    d[j] = d[j - 1] * (1 - alpha) + d[j] * alpha;
                    dw[j] = dw[j - 1] * (1 - alpha) + dw[j] * alpha;
                }
            }
            return W != null && dw[p] != 0 ? d[p] / dw[p] : d[p];
        }
    }
}
