using System.Text.Encodings.Web;
using System.Text.Json;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.Graphics;
using DgnKit.Model;

namespace DgnKit.Export;

public sealed class JsonDumpOptions
{
    public string? ModelName { get; set; }
    public bool IncludeElements { get; set; } = true;
    public bool IncludePrimitives { get; set; }
    public bool IncludeRaw { get; set; }
    public int MaxElements { get; set; } = int.MaxValue;
    public DisplayListOptions? DisplayOptions { get; set; }
}

/// <summary>
/// Serialises the full engineering data of a DGN (file info, models, units, levels, line styles, fonts, elements with
/// geometry, symbology and metadata) to JSON. Coordinates are in master units of each model.
/// </summary>
public static class JsonDump
{
    public static void Write(DgnFile f, Stream stream, JsonDumpOptions? o = null)
    {
        o ??= new JsonDumpOptions();
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteString("generator", "DgnKit");
        w.WriteString("file", f.Path);
        w.WriteString("format", f.Format.ToString());
        w.WriteString("version", $"{f.MajorVersion}.{f.MinorVersion}");
        if (f.GeoCoordinateSystem != null) WriteGcs(w, "geoCoordinateSystem", f.GeoCoordinateSystem);

        w.WriteStartArray("levels");
        foreach (var l in f.Levels.All.OrderBy(l => l.Id))
        {
            w.WriteStartObject();
            w.WriteNumber("id", l.Id); w.WriteString("name", l.Name);
            if (l.Description != null) w.WriteString("description", l.Description);
            if (l.Code.HasValue) w.WriteNumber("code", l.Code.Value);
            if (l.HasSymbology)
            {
                w.WriteNumber("color", l.Color); w.WriteString("rgb", DisplayListBuilder.ColorOf(f, l.Color).ToHex());
                w.WriteNumber("weight", l.Weight); w.WriteNumber("style", l.Style);
                w.WriteBoolean("displayed", l.IsDisplayed); w.WriteBoolean("frozen", l.IsFrozen); w.WriteBoolean("plot", l.IsPlot);
            }
            if (o.IncludeRaw) WriteDict(w, "raw", l.Properties);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("lineStyles");
        foreach (var ls in f.LineStyles.All.OrderBy(x => x.Number))
        {
            w.WriteStartObject(); w.WriteNumber("number", ls.Number); w.WriteString("name", ls.Name);
            w.WriteString("units", ls.Units.ToString()); w.WriteNumber("unitScale", ls.UnitScale);
            if (ls.UnitsArePhysical) w.WriteBoolean("physical", true);
            if (ls.Root != null) w.WriteString("root", ls.Root.Kind.ToString());
            w.WriteBoolean("hasDefinition", ls.HasDefinition);
            if (ls.DashPattern != null)
            {
                w.WriteStartArray("dashPattern"); foreach (var d in ls.DashPattern) w.WriteNumberValue(d); w.WriteEndArray();
                if (ls.DashPhase != 0) w.WriteNumber("dashPhase", ls.DashPhase);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("fonts");
        foreach (var ft in f.Fonts.ById.Values.OrderBy(x => x.Id)) { w.WriteStartObject(); w.WriteNumber("id", ft.Id); w.WriteString("name", ft.Name); w.WriteEndObject(); }
        w.WriteEndArray();

        w.WriteStartArray("textStyles");
        foreach (var ts in f.TextStyles) w.WriteStringValue(ts.Name);
        w.WriteEndArray();

        w.WriteStartArray("sharedCellDefinitions");
        foreach (var d in f.SharedCellDefinitions.Values) { w.WriteStartObject(); w.WriteNumber("id", d.Id); w.WriteString("name", d.Name); w.WriteNumber("components", d.Children.Count); w.WriteEndObject(); }
        w.WriteEndArray();

        w.WriteStartArray("models");
        foreach (var m in f.Models)
        {
            if (o.ModelName != null && !string.Equals(m.Name, o.ModelName, StringComparison.OrdinalIgnoreCase)) continue;
            WriteModel(w, f, m, o);
        }
        w.WriteEndArray();

        w.WriteStartArray("diagnostics");
        foreach (var d in f.Diagnostics) w.WriteStringValue(d);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteGcs(Utf8JsonWriter w, string name, GeoCoordinateInfo g)
    {
        w.WriteStartObject(name);
        void S(string k, string? v) { if (v != null) w.WriteString(k, v); }
        void D(string k, double? v) { if (v.HasValue && double.IsFinite(v.Value)) w.WriteNumber(k, v.Value); }
        S("name", g.Name); S("description", g.Description);
        if (g.Epsg.HasValue) w.WriteNumber("epsg", g.Epsg.Value);
        S("group", g.Group); S("projection", g.ProjectionKey); S("projectionName", g.ProjectionName);
        S("datum", g.DatumKey); S("datumName", g.DatumName); S("ellipsoidName", g.EllipsoidName); S("units", g.Units);
        D("semiMajorAxis", g.SemiMajorAxis); D("semiMinorAxis", g.SemiMinorAxis);
        D("centralMeridian", g.CentralMeridian); D("originLatitude", g.OriginLatitude);
        D("standardParallel1", g.StandardParallel1); D("standardParallel2", g.StandardParallel2); D("scaleReduction", g.ScaleReduction);
        D("falseEasting", g.FalseEasting); D("falseNorthing", g.FalseNorthing);
        D("minLongitude", g.MinLongitude); D("minLatitude", g.MinLatitude); D("maxLongitude", g.MaxLongitude); D("maxLatitude", g.MaxLatitude);
        S("wkt", g.Wkt); S("source", g.Source);
        w.WriteEndObject();
    }

    private static void WriteModel(Utf8JsonWriter w, DgnFile f, DgnModel m, JsonDumpOptions o)
    {
        w.WriteStartObject();
        w.WriteNumber("id", m.Id); w.WriteString("name", m.Name);
        if (m.Description != null) w.WriteString("description", m.Description);
        w.WriteString("type", m.Type.ToString()); w.WriteBoolean("is3d", m.Is3d); w.WriteBoolean("isDefinitionModel", m.IsDictionary);
        w.WriteStartObject("units");
        w.WriteString("master", m.Units.Master.Label); w.WriteNumber("metersPerMaster", R(m.Units.MetersPerMaster));
        w.WriteString("sub", m.Units.Sub.Label); w.WriteNumber("uorPerMaster", R(m.Units.UorPerMaster));
        w.WriteEndObject();
        if (m.GeoCoordinateSystem != null) WriteGcs(w, "geoCoordinateSystem", m.GeoCoordinateSystem);
        WritePoint(w, "globalOrigin", m.GlobalOrigin);
        w.WriteStartArray("references");
        foreach (var r in m.References)
        {
            w.WriteStartObject();
            w.WriteString("fileName", r.FileName); w.WriteString("modelName", r.ModelName); w.WriteString("logicalName", r.LogicalName);
            w.WriteNumber("scale", R(r.Scale)); w.WriteBoolean("displayed", r.Displayed);
            WritePoint(w, "masterOrigin", m.ToMaster(r.MasterOrigin));
            w.WriteBoolean("loaded", r.Loaded != null);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (o.IncludeElements)
        {
            w.WriteStartArray("elements");
            int n = 0;
            foreach (var e in m.Elements)
            {
                if (n++ >= o.MaxElements) break;
                WriteElement(w, f, m, e, o);
            }
            w.WriteEndArray();
        }
        if (o.IncludePrimitives)
        {
            var opts = o.DisplayOptions ?? new DisplayListOptions();
            opts.ModelName = m.Name;
            w.WriteStartArray("primitives");
            foreach (var p in DisplayListBuilder.Build(f, m, opts)) WritePrimitive(w, p);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteElement(Utf8JsonWriter w, DgnFile f, DgnModel m, Element e, JsonDumpOptions o)
    {
        w.WriteStartObject();
        w.WriteNumber("id", e.Id);
        w.WriteNumber("type", e.RawType);
        w.WriteString("typeName", e.TypeName);
        if (e.IsGraphic)
        {
            w.WriteNumber("levelId", e.LevelId);
            w.WriteString("level", f.Levels.NameOf(e.LevelId));
            w.WriteNumber("color", e.Color); w.WriteNumber("weight", e.Weight); w.WriteNumber("style", e.Style);
            w.WriteString("styleName", e.Style == Symbology.StyleByLevel ? "ByLevel" : e.Style == Symbology.StyleByCell ? "ByCell" : f.LineStyles.NameOf(e.Style));
            if (e.Class != ElementClass.Primary) w.WriteString("class", e.Class.ToString());
            if (e.IsInvisible) w.WriteBoolean("invisible", true);
            if (e.Priority != 0) w.WriteNumber("priority", e.Priority);
            if (e.GraphicGroup != 0) w.WriteNumber("graphicGroup", e.GraphicGroup);
            if (e.Fill != null) { w.WriteStartObject("fill"); if (e.Fill.Color.HasValue) w.WriteNumber("color", e.Fill.Color.Value); w.WriteEndObject(); }
        }
        WriteGeometry(w, m, e);
        if (e.PropertyGroups.Count > 0)
        {
            w.WriteStartArray("properties");
            foreach (var g in e.PropertyGroups)
            {
                w.WriteStartObject();
                w.WriteString("group", g.Name); w.WriteString("source", g.Source);
                if (g.Schema != null) w.WriteString("schema", g.Schema);
                WriteDict(w, "values", g.Properties);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (e.XAttributes.Count > 0)
        {
            w.WriteStartArray("xattributes");
            foreach (var x in e.XAttributes) w.WriteStringValue(x.ToString());
            w.WriteEndArray();
        }
        if (o.IncludeRaw && e.RawBytes != null) w.WriteString("raw", Convert.ToBase64String(e.RawBytes));
        if (o.IncludeRaw && e.HasExtra) WriteDict(w, "extra", e.Extra);
        if (e.Children.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var c in e.Children) WriteElement(w, f, m, c, o);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteGeometry(Utf8JsonWriter w, DgnModel m, Element e)
    {
        switch (e)
        {
            case LineElement l:
                w.WriteStartArray("points"); WriteXyz(w, m.ToMaster(l.Start)); WriteXyz(w, m.ToMaster(l.End)); w.WriteEndArray();
                break;
            case LineStringElement ls:
                // a disconnect vertex (MicroStation's DISCONNECT run separator) is written as null
                w.WriteStartArray("points");
                foreach (var p in ls.Points) { if (p.IsDisconnect) w.WriteNullValue(); else WriteXyz(w, m.ToMaster(p)); }
                w.WriteEndArray();
                break;
            case ArcElement a:
                WritePoint(w, "center", m.ToMaster(a.Origin));
                w.WriteNumber("primaryAxis", R(m.ToMaster(a.PrimaryAxis))); w.WriteNumber("secondaryAxis", R(m.ToMaster(a.SecondaryAxis)));
                w.WriteNumber("startAngleDeg", R(a.StartAngle * 180 / Math.PI)); w.WriteNumber("sweepAngleDeg", R(a.SweepAngle * 180 / Math.PI));
                w.WriteNumber("rotationDeg", R(Math.Atan2(a.Rotation.M10, a.Rotation.M00) * 180 / Math.PI));
                break;
            case TextElement t:
                w.WriteString("text", t.Text); WritePoint(w, "origin", m.ToMaster(t.Origin));
                w.WriteNumber("height", R(m.ToMaster(t.Height))); w.WriteNumber("width", R(m.ToMaster(t.Width)));
                w.WriteNumber("rotationDeg", R(t.RotationAngle * 180 / Math.PI)); w.WriteNumber("font", t.FontId);
                w.WriteString("justification", t.Justification.ToString());
                break;
            case TextNodeElement tn:
                WritePoint(w, "origin", m.ToMaster(tn.Origin)); w.WriteString("text", tn.Text);
                break;
            case CellElement c:
                w.WriteString("name", c.Name); WritePoint(w, "origin", m.ToMaster(c.Origin));
                break;
            case SharedCellElement sc:
                w.WriteString("name", sc.Name); WritePoint(w, "origin", m.ToMaster(sc.Origin));
                w.WriteBoolean("definitionFound", sc.Definition != null);
                break;
            case BSplineCurveElement bc:
                w.WriteNumber("order", bc.Order); w.WriteBoolean("closed", bc.Closed); w.WriteBoolean("rational", bc.Rational);
                w.WriteStartArray("poles"); foreach (var p in bc.Poles) WriteXyz(w, m.ToMaster(p)); w.WriteEndArray();
                w.WriteStartArray("knots"); foreach (var k in bc.Knots) w.WriteNumberValue(k); w.WriteEndArray();
                break;
            case ReferenceElement re:
                w.WriteString("refFile", re.Attachment.FileName); w.WriteString("refModel", re.Attachment.ModelName);
                break;
            case DimensionElement dim:
                w.WriteString("dimensionType", dim.Kind.ToString());
                w.WriteStartArray("points"); foreach (var p in dim.Points) WriteXyz(w, m.ToMaster(p)); w.WriteEndArray();
                if (dim.IsLinear) w.WriteNumber("height", R(m.ToMaster(dim.Height)));
                w.WriteNumber("textHeight", R(m.ToMaster(dim.TextHeight)));
                w.WriteStartArray("segments");
                foreach (var s in dim.Segments)
                {
                    w.WriteStartObject();
                    if (!double.IsNaN(s.Value)) w.WriteNumber("value", R(s.Value));
                    if (s.FormattedValue != null) w.WriteString("formatted", s.FormattedValue);
                    if (s.OverrideText != null) w.WriteString("override", s.OverrideText);
                    w.WriteString("text", s.Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
        }
    }

    private static void WritePrimitive(Utf8JsonWriter w, Primitive p)
    {
        w.WriteStartObject();
        w.WriteString("kind", p.Kind.ToString()); w.WriteNumber("elementId", p.Leaf.Id); w.WriteString("level", p.LevelName);
        w.WriteString("color", p.Symbology.Color.ToHex()); w.WriteNumber("weight", p.Symbology.WeightIndex); w.WriteString("lineStyle", p.Symbology.StyleName);
        if (p.CellName != null) w.WriteString("cell", p.CellName);
        if (p.Kind == PrimitiveKind.Arc)
        {
            WritePoint(w, "center", p.Center); WritePoint(w, "axisX", p.AxisX); WritePoint(w, "axisY", p.AxisY);
            w.WriteNumber("start", R(p.StartAngle)); w.WriteNumber("sweep", R(p.SweepAngle));
        }
        else
        {
            w.WriteStartArray("points"); foreach (var q in p.Points) WriteXyz(w, q); w.WriteEndArray();
            if (p.Closed) w.WriteBoolean("closed", true);
        }
        if (p.Text != null) { w.WriteString("text", p.Text); w.WriteNumber("height", R(p.TextHeight)); }
        w.WriteEndObject();
    }

    internal static void WriteDict(Utf8JsonWriter w, string name, IDictionary<string, object?> d)
    {
        w.WriteStartObject(name);
        foreach (var kv in d)
        {
            switch (kv.Value)
            {
                case null: w.WriteNull(kv.Key); break;
                case string s: w.WriteString(kv.Key, s); break;
                case bool b: w.WriteBoolean(kv.Key, b); break;
                case int i: w.WriteNumber(kv.Key, i); break;
                case uint u: w.WriteNumber(kv.Key, u); break;
                case long l: w.WriteNumber(kv.Key, l); break;
                case ulong ul: w.WriteNumber(kv.Key, ul); break;
                case double db: if (double.IsFinite(db)) w.WriteNumber(kv.Key, db); else w.WriteString(kv.Key, db.ToString()); break;
                default: w.WriteString(kv.Key, kv.Value.ToString()); break;
            }
        }
        w.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter w, string name, DPoint3d p)
    {
        w.WriteStartArray(name); w.WriteNumberValue(R(p.X)); w.WriteNumberValue(R(p.Y)); w.WriteNumberValue(R(p.Z)); w.WriteEndArray();
    }

    private static void WriteXyz(Utf8JsonWriter w, DPoint3d p)
    {
        w.WriteStartArray(); w.WriteNumberValue(R(p.X)); w.WriteNumberValue(R(p.Y)); w.WriteNumberValue(R(p.Z)); w.WriteEndArray();
    }

    private static double R(double v) => double.IsFinite(v) ? Math.Round(v, 6) : 0;
}
