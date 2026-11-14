using System.Text.Json;
using DgnKit.Geometry;

namespace DgnKit.IModel.Imx;

/// <summary>
/// A format-neutral, BIS-ready geometry stream: an ordered list of symbology changes and geometric primitives in
/// <b>world metres</b>. <see cref="WriteJson"/> serialises it as iTwin.js <c>GeometryStreamProps</c> (IModelJson geometry
/// + <c>appearance</c>/<c>fill</c>/<c>styleMod</c>/<c>textString</c>/<c>geomPart</c> entries) relative to the element's
/// placement origin. Keys (<c>font:…</c>, <c>ls:…</c>, <c>part:…</c>) are resolved to element ids by the loader.
/// Any reader (DGN, DWG, IFC, LandXML …) can produce these; nothing here is DGN-specific.
/// </summary>
public sealed class GeometryStream
{
    public List<GsEntry> Entries { get; } = new();
    public bool Is3d { get; }
    public GeometryStream(bool is3d) { Is3d = is3d; }

    public bool HasGeometry => Entries.Any(e => e is GsPrimitive);
    public void Add(GsEntry e) => Entries.Add(e);

    /// <summary>World range of the primitives (arcs and text are approximated).</summary>
    public Range3d Range()
    {
        var r = Range3d.Empty;
        foreach (var e in Entries.OfType<GsPrimitive>()) e.ExtendRange(ref r);
        return r;
    }

    /// <summary>Writes the stream as a JSON array of GeometryStreamEntryProps with coordinates relative to <paramref name="origin"/>.</summary>
    public void WriteJson(Utf8JsonWriter w, DPoint3d origin)
    {
        var ctx = new GsWriteContext(origin, Is3d);
        w.WriteStartArray();
        foreach (var e in Entries) e.Write(w, ctx);
        w.WriteEndArray();
    }
}

internal readonly struct GsWriteContext
{
    public readonly DPoint3d Origin;
    public readonly bool Is3d;
    public GsWriteContext(DPoint3d origin, bool is3d) { Origin = origin; Is3d = is3d; }

    public void Point(Utf8JsonWriter w, DPoint3d p)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Num.R(p.X - Origin.X));
        w.WriteNumberValue(Num.R(p.Y - Origin.Y));
        w.WriteNumberValue(Is3d ? Num.R(p.Z - Origin.Z) : 0);
        w.WriteEndArray();
    }

    public void Vector(Utf8JsonWriter w, DPoint3d v)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Num.R(v.X)); w.WriteNumberValue(Num.R(v.Y)); w.WriteNumberValue(Is3d ? Num.R(v.Z) : 0);
        w.WriteEndArray();
    }

    public void Points(Utf8JsonWriter w, IEnumerable<DPoint3d> pts)
    {
        w.WriteStartArray();
        foreach (var p in pts) Point(w, p);
        w.WriteEndArray();
    }
}

internal static class Num
{
    /// <summary>Rounds metres to 1e-9 (nanometre) to keep the JSON compact and stable for checksums.</summary>
    public static double R(double v) => double.IsFinite(v) ? Math.Round(v, 9) : 0;
    public static double Deg(double radians) => Math.Round(radians * 180.0 / Math.PI, 12);
}

public abstract class GsEntry
{
    internal abstract void Write(Utf8JsonWriter w, GsWriteContext c);
}

/// <summary>
/// Symbology change (iTwin.js <c>GeometryAppearanceProps</c>). Every appearance entry restarts from the sub-category's
/// appearance; null members inherit it (DGN "ByLevel").
/// </summary>
public sealed class GsAppearance : GsEntry, IEquatable<GsAppearance>
{
    /// <summary>Sub-category key (default sub-category of the element's category when null).</summary>
    public string? SubCategory { get; init; }
    /// <summary>Colour as iTwin.js ColorDef integer 0xTTBBGGRR.</summary>
    public uint? Color { get; init; }
    /// <summary>Line weight 0..31 (same scale as MicroStation weights).</summary>
    public int? Weight { get; init; }
    /// <summary>Line style key (<c>ls:…</c>).</summary>
    public string? Style { get; init; }
    public double? Transparency { get; init; }
    /// <summary>2D display priority.</summary>
    public int? DisplayPriority { get; init; }
    /// <summary>GeometryClass: 0 primary, 1 construction, 2 dimension, 3 pattern.</summary>
    public int? GeometryClass { get; init; }

    public bool IsDefault => SubCategory == null && Color == null && Weight == null && Style == null && Transparency == null &&
                             DisplayPriority == null && (GeometryClass ?? 0) == 0;

    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("appearance");
        w.WriteStartObject();
        if (SubCategory != null) w.WriteString("subCategory", SubCategory);
        if (Color.HasValue) w.WriteNumber("color", Color.Value);
        if (Weight.HasValue) w.WriteNumber("weight", Weight.Value);
        if (Style != null) w.WriteString("style", Style);
        if (Transparency.HasValue && Transparency.Value > 0) w.WriteNumber("transparency", Math.Round(Transparency.Value, 6));
        if (DisplayPriority.HasValue && DisplayPriority.Value != 0) w.WriteNumber("displayPriority", DisplayPriority.Value);
        if (GeometryClass.HasValue && GeometryClass.Value != 0) w.WriteNumber("geometryClass", GeometryClass.Value);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public bool Equals(GsAppearance? o) => o != null && SubCategory == o.SubCategory && Color == o.Color && Weight == o.Weight &&
        Style == o.Style && Transparency == o.Transparency && DisplayPriority == o.DisplayPriority && GeometryClass == o.GeometryClass;
    public override bool Equals(object? obj) => Equals(obj as GsAppearance);
    public override int GetHashCode() => HashCode.Combine(SubCategory, Color, Weight, Style, Transparency, DisplayPriority, GeometryClass);
}

/// <summary>Area fill for the following closed region(s) (iTwin.js <c>AreaFillProps</c>).</summary>
public sealed class GsFill : GsEntry
{
    /// <summary>FillDisplay: 0 never, 1 by view, 2 always, 3 blanking.</summary>
    public int Display { get; init; } = 1;
    public uint? Color { get; init; }
    public double? Transparency { get; init; }
    /// <summary>Fill with the outline colour (DGN "fill colour = element colour").</summary>
    public bool UseOutlineColor { get; init; }

    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("fill");
        w.WriteStartObject();
        w.WriteNumber("display", Display);
        if (Color.HasValue && !UseOutlineColor) w.WriteNumber("color", Color.Value);
        if (Transparency.HasValue && Transparency.Value > 0) w.WriteNumber("transparency", Math.Round(Transparency.Value, 6));
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>Line style modifiers (iTwin.js <c>LineStyle.ModifierProps</c>), lengths in metres.</summary>
public sealed class GsStyleMod : GsEntry
{
    public double? Scale { get; init; }
    public double? DashScale { get; init; }
    public double? GapScale { get; init; }
    public double? StartWidth { get; init; }
    public double? EndWidth { get; init; }
    public double? DistPhase { get; init; }
    public double? FractPhase { get; init; }
    public bool CenterPhase { get; init; }

    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("styleMod");
        w.WriteStartObject();
        if (Scale.HasValue) w.WriteNumber("scale", Num.R(Scale.Value));
        if (DashScale.HasValue) w.WriteNumber("dashScale", Num.R(DashScale.Value));
        if (GapScale.HasValue) w.WriteNumber("gapScale", Num.R(GapScale.Value));
        if (StartWidth.HasValue) w.WriteNumber("startWidth", Num.R(StartWidth.Value));
        if (EndWidth.HasValue) w.WriteNumber("endWidth", Num.R(EndWidth.Value));
        if (DistPhase.HasValue) w.WriteNumber("distPhase", Num.R(DistPhase.Value));
        if (FractPhase.HasValue) w.WriteNumber("fractPhase", Num.R(FractPhase.Value));
        if (CenterPhase) w.WriteBoolean("centerPhase", true);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>A geometric primitive (anything that draws).</summary>
public abstract class GsPrimitive : GsEntry
{
    internal abstract void ExtendRange(ref Range3d r);
}

/// <summary>A curve that can stand alone or be a member of a path/loop.</summary>
public abstract class GsCurve : GsPrimitive
{
    /// <summary>Writes just the IModelJson curve object (no wrapping), e.g. <c>{"arc":{…}}</c>.</summary>
    internal abstract void WriteCurve(Utf8JsonWriter w, GsWriteContext c);
    internal override void Write(Utf8JsonWriter w, GsWriteContext c) => WriteCurve(w, c);
    public abstract DPoint3d StartPoint { get; }
    public abstract DPoint3d EndPoint { get; }
}

public sealed class GsLineSegment : GsCurve
{
    public DPoint3d P0 { get; }
    public DPoint3d P1 { get; }
    public GsLineSegment(DPoint3d p0, DPoint3d p1) { P0 = p0; P1 = p1; }
    public override DPoint3d StartPoint => P0;
    public override DPoint3d EndPoint => P1;
    internal override void ExtendRange(ref Range3d r) { r.Extend(P0); r.Extend(P1); }
    internal override void WriteCurve(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("lineSegment");
        w.WriteStartArray(); c.Point(w, P0); c.Point(w, P1); w.WriteEndArray();
        w.WriteEndObject();
    }
}

public sealed class GsLineString : GsCurve
{
    public List<DPoint3d> Points { get; }
    public GsLineString(IEnumerable<DPoint3d> pts) { Points = pts.ToList(); }
    public override DPoint3d StartPoint => Points[0];
    public override DPoint3d EndPoint => Points[^1];
    internal override void ExtendRange(ref Range3d r) => r.Extend(Points);
    internal override void WriteCurve(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("lineString");
        c.Points(w, Points);
        w.WriteEndObject();
    }
}

/// <summary>Elliptical arc: <c>X(θ) = center + vectorX·cos θ + vectorY·sin θ</c>, θ from start to end (radians).</summary>
public sealed class GsArc : GsCurve
{
    public DPoint3d Center { get; init; }
    public DPoint3d VectorX { get; init; }
    public DPoint3d VectorY { get; init; }
    public double Start { get; init; }
    public double Sweep { get; init; } = 2 * Math.PI;
    public DPoint3d PointAt(double t) => Center + VectorX * Math.Cos(t) + VectorY * Math.Sin(t);
    public override DPoint3d StartPoint => PointAt(Start);
    public override DPoint3d EndPoint => PointAt(Start + Sweep);
    internal override void ExtendRange(ref Range3d r)
    {
        int n = 32;
        for (int i = 0; i <= n; i++) r.Extend(PointAt(Start + Sweep * i / n));
    }
    internal override void WriteCurve(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("arc");
        w.WriteStartObject();
        w.WritePropertyName("center"); c.Point(w, Center);
        w.WritePropertyName("vectorX"); c.Vector(w, VectorX);
        w.WritePropertyName("vectorY"); c.Vector(w, VectorY);
        w.WritePropertyName("sweepStartEnd");
        w.WriteStartArray(); w.WriteNumberValue(Num.Deg(Start)); w.WriteNumberValue(Num.Deg(Start + Sweep)); w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>
/// Clamped B-spline curve. <see cref="Poles"/> are Cartesian; when <see cref="Weights"/> is set the curve is rational and is
/// written in homogeneous form [wx, wy, wz, w]. <see cref="Knots"/> is the full knot vector (poles + order values).
/// </summary>
public sealed class GsBSpline : GsCurve
{
    public List<DPoint3d> Poles { get; init; } = new();
    public List<double>? Weights { get; init; }
    public List<double> Knots { get; init; } = new();
    public int Order { get; init; }
    public override DPoint3d StartPoint => Poles[0];
    public override DPoint3d EndPoint => Poles[^1];
    internal override void ExtendRange(ref Range3d r) => r.Extend(Poles); // control polygon hull contains the curve
    internal override void WriteCurve(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("bcurve");
        w.WriteStartObject();
        w.WritePropertyName("points");
        w.WriteStartArray();
        for (int i = 0; i < Poles.Count; i++)
        {
            var p = Poles[i];
            double x = p.X - c.Origin.X, y = p.Y - c.Origin.Y, z = c.Is3d ? p.Z - c.Origin.Z : 0;
            w.WriteStartArray();
            if (Weights != null)
            {
                double wt = Weights[i];
                w.WriteNumberValue(Num.R(x * wt)); w.WriteNumberValue(Num.R(y * wt)); w.WriteNumberValue(Num.R(z * wt)); w.WriteNumberValue(wt);
            }
            else { w.WriteNumberValue(Num.R(x)); w.WriteNumberValue(Num.R(y)); w.WriteNumberValue(Num.R(z)); }
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WritePropertyName("knots");
        w.WriteStartArray(); foreach (var k in Knots) w.WriteNumberValue(k); w.WriteEndArray();
        w.WriteNumber("order", Order);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>Open path (<c>path</c>) or closed loop (<c>loop</c>) of curves.</summary>
public sealed class GsCurveChain : GsPrimitive
{
    public List<GsCurve> Curves { get; } = new();
    public bool Closed { get; init; }
    internal override void ExtendRange(ref Range3d r) { foreach (var cv in Curves) cv.ExtendRange(ref r); }
    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName(Closed ? "loop" : "path");
        w.WriteStartArray();
        foreach (var cv in Curves) cv.WriteCurve(w, c);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}

public sealed class GsPointString : GsPrimitive
{
    public List<DPoint3d> Points { get; }
    public GsPointString(IEnumerable<DPoint3d> pts) { Points = pts.ToList(); }
    internal override void ExtendRange(ref Range3d r) => r.Extend(Points);
    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("pointString");
        c.Points(w, Points);
        w.WriteEndObject();
    }
}

/// <summary>Polyface (<c>indexedMesh</c>); faces are 0-based vertex index lists.</summary>
public sealed class GsMesh : GsPrimitive
{
    public List<DPoint3d> Points { get; init; } = new();
    public List<int[]> Faces { get; init; } = new();
    internal override void ExtendRange(ref Range3d r) => r.Extend(Points);
    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("indexedMesh");
        w.WriteStartObject();
        w.WritePropertyName("point"); c.Points(w, Points);
        w.WritePropertyName("pointIndex");
        w.WriteStartArray();
        foreach (var f in Faces)
        {
            foreach (var i in f) w.WriteNumberValue(i + 1); // IModelJson: 1-based, 0 terminates a face
            w.WriteNumberValue(0);
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>Cone / cylinder solid primitive.</summary>
public sealed class GsCone : GsPrimitive
{
    public DPoint3d Start { get; init; }
    public DPoint3d End { get; init; }
    public double StartRadius { get; init; }
    public double EndRadius { get; init; }
    public DPoint3d? VectorX { get; init; }
    public DPoint3d? VectorY { get; init; }
    public bool Capped { get; init; } = true;
    internal override void ExtendRange(ref Range3d r)
    {
        double rr = Math.Max(StartRadius, EndRadius);
        foreach (var p in new[] { Start, End })
        {
            r.Extend(p + new DPoint3d(rr, rr, rr)); r.Extend(p - new DPoint3d(rr, rr, rr));
        }
    }
    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("cone");
        w.WriteStartObject();
        w.WritePropertyName("start"); c.Point(w, Start);
        w.WritePropertyName("end"); c.Point(w, End);
        w.WriteNumber("startRadius", Num.R(StartRadius));
        w.WriteNumber("endRadius", Num.R(EndRadius));
        if (VectorX.HasValue && VectorY.HasValue)
        {
            w.WritePropertyName("vectorX"); c.Vector(w, VectorX.Value);
            w.WritePropertyName("vectorY"); c.Vector(w, VectorY.Value);
        }
        w.WriteBoolean("capped", Capped);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>
/// A text string (iTwin.js <c>TextStringProps</c>): <see cref="Origin"/> is the lower-left of the text, <see cref="Rotation"/>
/// the orientation (columns = text x, text up, normal). <see cref="Font"/> is a font key.
/// </summary>
public sealed class GsText : GsPrimitive
{
    public string Text { get; init; } = "";
    public string Font { get; init; } = "";
    public double Height { get; init; }
    public double WidthFactor { get; init; } = 1;
    public DPoint3d Origin { get; init; }
    public Matrix3 Rotation { get; init; } = Matrix3.Identity;
    public bool Underline { get; init; }
    internal override void ExtendRange(ref Range3d r)
    {
        double len = Height * WidthFactor * 0.8 * Math.Max(1, Text.Length);
        r.Extend(Origin);
        r.Extend(Origin + Rotation.Column(0) * len);
        r.Extend(Origin + Rotation.Column(1) * Height);
        r.Extend(Origin + Rotation.Column(0) * len + Rotation.Column(1) * Height);
    }
    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("textString");
        w.WriteStartObject();
        w.WriteString("text", Text);
        w.WriteString("font", Font);
        w.WriteNumber("height", Num.R(Height));
        if (Math.Abs(WidthFactor - 1) > 1e-9) w.WriteNumber("widthFactor", Math.Round(WidthFactor, 9));
        w.WritePropertyName("origin"); c.Point(w, Origin);
        var ypr = Ypr.FromMatrix(Rotation);
        if (!ypr.IsZero)
        {
            w.WritePropertyName("rotation");
            ypr.Write(w, c.Is3d);
        }
        if (Underline) w.WriteBoolean("underline", true);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>Instance of a geometry part (<c>geomPart</c>): rotation × uniform scale, no mirroring.</summary>
public sealed class GsPartInstance : GsPrimitive
{
    public string Part { get; init; } = "";
    public DPoint3d Origin { get; init; }
    public Matrix3 Rotation { get; init; } = Matrix3.Identity;
    public double Scale { get; init; } = 1;
    /// <summary>Part range in part-local metres (for the element range).</summary>
    public Range3d PartRange { get; init; } = Range3d.Empty;
    internal override void ExtendRange(ref Range3d r)
    {
        if (PartRange.IsEmpty) { r.Extend(Origin); return; }
        var lo = PartRange.Low; var hi = PartRange.High;
        foreach (var x in new[] { lo.X, hi.X })
        foreach (var y in new[] { lo.Y, hi.Y })
        foreach (var z in new[] { lo.Z, hi.Z })
            r.Extend(Origin + Rotation.Multiply(new DPoint3d(x, y, z)) * Scale);
    }
    internal override void Write(Utf8JsonWriter w, GsWriteContext c)
    {
        w.WriteStartObject();
        w.WritePropertyName("geomPart");
        w.WriteStartObject();
        w.WriteString("part", Part);
        w.WritePropertyName("origin"); c.Point(w, Origin);
        var ypr = Ypr.FromMatrix(Rotation);
        if (!ypr.IsZero) { w.WritePropertyName("rotation"); ypr.Write(w, c.Is3d); }
        if (Math.Abs(Scale - 1) > 1e-12) w.WriteNumber("scale", Math.Round(Scale, 12));
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>
/// Yaw/pitch/roll in degrees with iTwin.js conventions (<c>YawPitchRollAngles.createFromMatrix3d</c>):
/// pitch = atan2(m20, √(m21²+m22²)), yaw = atan2(m10, m00), roll = atan2(m21, m22).
/// </summary>
public readonly struct Ypr
{
    public readonly double Yaw, Pitch, Roll;
    public Ypr(double yaw, double pitch, double roll) { Yaw = yaw; Pitch = pitch; Roll = roll; }
    public bool IsZero => Math.Abs(Yaw) < 1e-12 && Math.Abs(Pitch) < 1e-12 && Math.Abs(Roll) < 1e-12;

    /// <summary>Orientation of a rigid rotation (columns are normalised and made right-handed first).</summary>
    public static Ypr FromMatrix(Matrix3 m)
    {
        var x = m.Column(0).Normalized();
        var y = m.Column(1);
        var z = x.Cross(y).Normalized();
        y = z.Cross(x).Normalized();
        double m00 = x.X, m10 = x.Y, m20 = x.Z, m01 = y.X, m11 = y.Y, m21 = y.Z, m22 = z.Z;
        double s1 = m20, c1 = Math.Sqrt(m21 * m21 + m22 * m22);
        double pitch = Math.Atan2(s1, c1), yaw, roll;
        if (c1 < 1e-10) { yaw = Math.Atan2(-m01, m11); roll = 0; }
        else { yaw = Math.Atan2(m10, m00); roll = Math.Atan2(m21, m22); }
        return new Ypr(Num.Deg(yaw), Num.Deg(pitch), Num.Deg(roll));
    }

    internal void Write(Utf8JsonWriter w, bool is3d)
    {
        w.WriteStartObject();
        if (Yaw != 0) w.WriteNumber("yaw", Yaw);
        if (is3d && Pitch != 0) w.WriteNumber("pitch", Pitch);
        if (is3d && Roll != 0) w.WriteNumber("roll", Roll);
        w.WriteEndObject();
    }
}
