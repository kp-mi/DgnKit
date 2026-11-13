using System;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;

namespace DgnKit.Graphics
{
    /// <summary>Computes an element's geometric range (UORs of its model) from decoded geometry.</summary>
    public static class ElementRange
    {
        public static Range3d Compute(Element e, bool includeText = true)
        {
            var r = Range3d.Empty;
            Accumulate(e, Transform.Identity, ref r, includeText, 0);
            return r;
        }

        private static void Accumulate(Element e, Transform T, ref Range3d r, bool includeText, int depth)
        {
            if (depth > 32) return;
            switch (e)
            {
                case LineElement l: r.Extend(T.Apply(l.Start)); r.Extend(T.Apply(l.End)); break;
                case LineStringElement ls: foreach (var p in ls.Points) r.Extend(T.Apply(p)); break;
                case ArcElement a:
                {
                    // P(θ) = O + A cosθ + B sinθ (after transform); extrema per axis at θ = atan2(B_k, A_k) (+π)
                    var O = T.Apply(a.Origin);
                    var A = T.ApplyVector(a.Rotation.Column(0) * a.PrimaryAxis);
                    var B = T.ApplyVector(a.Rotation.Column(1) * a.SecondaryAxis);
                    DPoint3d At(double th) => O + A * Math.Cos(th) + B * Math.Sin(th);
                    r.Extend(At(a.StartAngle)); r.Extend(At(a.StartAngle + a.SweepAngle));
                    foreach (var th in new[] { Math.Atan2(B.X, A.X), Math.Atan2(B.Y, A.Y), Math.Atan2(B.Z, A.Z) })
                    {
                        if (InSweep(th, a.StartAngle, a.SweepAngle)) r.Extend(At(th));
                        if (InSweep(th + Math.PI, a.StartAngle, a.SweepAngle)) r.Extend(At(th + Math.PI));
                    }
                    break;
                }
                case TextElement t when includeText:
                {
                    var o = t.Origin;
                    var dx = t.Rotation.Column(0) * (t.StoredLength > 0 ? t.StoredLength : t.Width * Math.Max(1, t.Text.Length));
                    var dy = t.Rotation.Column(1) * (t.StoredHeight > 0 ? t.StoredHeight : t.Height);
                    r.Extend(T.Apply(o)); r.Extend(T.Apply(o + dx)); r.Extend(T.Apply(o + dy)); r.Extend(T.Apply(o + dx + dy));
                    break;
                }
                case BSplineCurveElement bc: foreach (var p in Curves.StrokeBSpline(bc, 8)) r.Extend(T.Apply(p)); break;
                case ConeElement c:
                    r.Extend(T.Apply(c.Center1 + c.Rotation.Column(0) * c.Radius1)); r.Extend(T.Apply(c.Center1 - c.Rotation.Column(0) * c.Radius1));
                    r.Extend(T.Apply(c.Center1 + c.Rotation.Column(1) * c.Radius1)); r.Extend(T.Apply(c.Center1 - c.Rotation.Column(1) * c.Radius1));
                    r.Extend(T.Apply(c.Center2 + c.Rotation.Column(0) * c.Radius2)); r.Extend(T.Apply(c.Center2 - c.Rotation.Column(0) * c.Radius2));
                    r.Extend(T.Apply(c.Center2 + c.Rotation.Column(1) * c.Radius2)); r.Extend(T.Apply(c.Center2 - c.Rotation.Column(1) * c.Radius2));
                    break;
                case MeshElement m: foreach (var p in m.Vertices) r.Extend(T.Apply(p)); break;
                case SharedCellElement sc when sc.Definition != null:
                {
                    var inst = new Transform(sc.Transform, sc.Origin - sc.Transform.Multiply(sc.Definition.Origin));
                    var T2 = T * inst;
                    foreach (var c in sc.Definition.Children) Accumulate(c, T2, ref r, includeText, depth + 1);
                    break;
                }
                default:
                    foreach (var c in e.Children) Accumulate(c, T, ref r, includeText, depth + 1);
                    break;
            }
        }

        /// <summary>True if angle <paramref name="a"/> lies on the arc that starts at <paramref name="start"/> and sweeps <paramref name="sweep"/> (signed).</summary>
        private static bool InSweep(double a, double start, double sweep)
        {
            if (Math.Abs(sweep) >= 2 * Math.PI - 1e-12) return true;
            double d = (a - start) * Math.Sign(sweep);           // measure in the sweep direction
            d %= 2 * Math.PI; if (d < 0) d += 2 * Math.PI;
            return d <= Math.Abs(sweep);
        }
    }
}
