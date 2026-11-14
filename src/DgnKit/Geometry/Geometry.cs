using System;
using System.Collections.Generic;
using System.Globalization;

namespace DgnKit.Geometry
{
    public readonly struct DPoint3d : IEquatable<DPoint3d>
    {
        public readonly double X, Y, Z;
        public DPoint3d(double x, double y, double z = 0) { X = x; Y = y; Z = z; }

        public static readonly DPoint3d Zero = new(0, 0, 0);
        public static DPoint3d operator +(DPoint3d a, DPoint3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DPoint3d operator -(DPoint3d a, DPoint3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DPoint3d operator *(DPoint3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static DPoint3d operator *(double s, DPoint3d a) => a * s;
        public static DPoint3d operator /(DPoint3d a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double Dot(DPoint3d o) => X * o.X + Y * o.Y + Z * o.Z;
        public DPoint3d Cross(DPoint3d o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public DPoint3d Normalized() { double l = Length; return l > 0 ? this / l : this; }
        public double DistanceTo(DPoint3d o) => (this - o).Length;
        public bool IsFinite => !(double.IsNaN(X) || double.IsNaN(Y) || double.IsNaN(Z) || double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Z));

        /// <summary>
        /// MicroStation's DISCONNECT coordinate value (DBL_MAX; SDK <c>Geom/GeomApi.h</c>, <c>Mstn/MstnDefs.h</c>). A vertex
        /// carrying it separates the runs of a "disconnected" line string or point string — detailing-symbol callouts
        /// (section/detail markers) store their leader this way. It is stored in UORs and is never a real position.
        /// </summary>
        public const double Disconnect = 1.7976931348623157e308;

        /// <summary>True for a disconnect (run separator) vertex: any coordinate equals <see cref="Disconnect"/> (as Bentley's <c>DPoint3d::IsDisconnect</c>). Test raw element points, before any transform.</summary>
        public bool IsDisconnect => X == Disconnect || Y == Disconnect || Z == Disconnect;

        /// <summary>Splits raw vertices at disconnect vertices into their runs; empty runs are dropped. Without disconnects the result is the input as one run.</summary>
        public static List<List<DPoint3d>> SplitAtDisconnects(IEnumerable<DPoint3d> points)
        {
            var runs = new List<List<DPoint3d>>();
            var cur = new List<DPoint3d>();
            foreach (var p in points)
            {
                if (p.IsDisconnect)
                {
                    if (cur.Count > 0) { runs.Add(cur); cur = new List<DPoint3d>(); }
                    continue;
                }
                cur.Add(p);
            }
            if (cur.Count > 0) runs.Add(cur);
            return runs;
        }

        public bool Equals(DPoint3d o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object? obj) => obj is DPoint3d p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:R}, {1:R}, {2:R})", X, Y, Z);
    }

    /// <summary>Row-major 3x3 matrix (MicroStation RotMatrix: form3d[row][col]).</summary>
    public readonly struct Matrix3
    {
        public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;
        public Matrix3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22;
        }

        public static readonly Matrix3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public static Matrix3 RotationZ(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Matrix3(c, -s, 0, s, c, 0, 0, 0, 1);
        }

        public static Matrix3 Scale(double s) => new(s, 0, 0, 0, s, 0, 0, 0, s);

        /// <summary>
        /// MicroStation quaternion (w, x, y, z) → rotation matrix. MicroStation stores the
        /// <em>inverse</em> (transposed) orientation in element quaternions, matching mdlRMatrix_fromQuat.
        /// </summary>
        public static Matrix3 FromQuaternion(double w, double x, double y, double z)
        {
            double n = Math.Sqrt(w * w + x * x + y * y + z * z);
            if (n < 1e-15) return Identity;
            w /= n; x /= n; y /= n; z /= n;
            // Standard rotation matrix for unit quaternion, then transposed (MicroStation convention).
            double xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
            var m = new Matrix3(
                1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy),
                2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx),
                2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy));
            return m.Transpose();
        }

        public Matrix3 Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public static Matrix3 operator *(Matrix3 a, Matrix3 b) => new(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public DPoint3d Multiply(DPoint3d p) => new(
            M00 * p.X + M01 * p.Y + M02 * p.Z,
            M10 * p.X + M11 * p.Y + M12 * p.Z,
            M20 * p.X + M21 * p.Y + M22 * p.Z);

        public DPoint3d Column(int i) => i == 0 ? new(M00, M10, M20) : i == 1 ? new(M01, M11, M21) : new(M02, M12, M22);
        public double Determinant => M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        /// <summary>Average scale of the columns (for scaling text heights, weights, etc.).</summary>
        public double AverageScale => (Column(0).Length + Column(1).Length + Column(2).Length) / 3.0;
        public double XYScale => Math.Sqrt(Math.Abs(M00 * M11 - M01 * M10));
    }

    /// <summary>Affine transform: p' = M·p + T.</summary>
    public readonly struct Transform
    {
        public readonly Matrix3 Matrix;
        public readonly DPoint3d Translation;
        public Transform(Matrix3 m, DPoint3d t) { Matrix = m; Translation = t; }

        public static readonly Transform Identity = new(Matrix3.Identity, DPoint3d.Zero);
        public static Transform FromTranslation(DPoint3d t) => new(Matrix3.Identity, t);
        public static Transform FromScale(double s) => new(Matrix3.Scale(s), DPoint3d.Zero);

        public DPoint3d Apply(DPoint3d p) => Matrix.Multiply(p) + Translation;
        public DPoint3d ApplyVector(DPoint3d v) => Matrix.Multiply(v);

        /// <summary>Returns this ∘ other (other applied first).</summary>
        public Transform Then(Transform outer) => new(outer.Matrix * Matrix, outer.Matrix.Multiply(Translation) + outer.Translation);
        public static Transform operator *(Transform outer, Transform inner) => inner.Then(outer);

        public bool IsIdentity =>
            Matrix.M00 == 1 && Matrix.M11 == 1 && Matrix.M22 == 1 && Matrix.M01 == 0 && Matrix.M02 == 0 && Matrix.M10 == 0 &&
            Matrix.M12 == 0 && Matrix.M20 == 0 && Matrix.M21 == 0 && Translation.X == 0 && Translation.Y == 0 && Translation.Z == 0;
    }

    public struct Range3d
    {
        public DPoint3d Low, High;
        public bool IsEmpty;

        public static Range3d Empty => new() { IsEmpty = true, Low = new DPoint3d(double.MaxValue, double.MaxValue, double.MaxValue), High = new DPoint3d(double.MinValue, double.MinValue, double.MinValue) };

        public void Extend(DPoint3d p)
        {
            if (!p.IsFinite) return;
            if (IsEmpty) { Low = p; High = p; IsEmpty = false; return; }
            Low = new DPoint3d(Math.Min(Low.X, p.X), Math.Min(Low.Y, p.Y), Math.Min(Low.Z, p.Z));
            High = new DPoint3d(Math.Max(High.X, p.X), Math.Max(High.Y, p.Y), Math.Max(High.Z, p.Z));
        }

        public void Extend(Range3d r) { if (!r.IsEmpty) { Extend(r.Low); Extend(r.High); } }
        public void Extend(IEnumerable<DPoint3d> pts) { foreach (var p in pts) Extend(p); }

        public double XSize => IsEmpty ? 0 : High.X - Low.X;
        public double YSize => IsEmpty ? 0 : High.Y - Low.Y;
        public DPoint3d Center => IsEmpty ? DPoint3d.Zero : (Low + High) / 2;
        public override string ToString() => IsEmpty ? "(empty)" : $"{Low} - {High}";
    }
}
