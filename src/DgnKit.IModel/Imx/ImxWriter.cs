using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DgnKit.Geometry;

namespace DgnKit.IModel.Imx;

/// <summary>
/// Writes IMX records as newline-delimited JSON. Each content record gets a <c>checksum</c> (SHA-256 of the record's
/// canonical JSON without the checksum), which the loader stores in ExternalSourceAspect.Checksum for change detection.
/// </summary>
public sealed class ImxWriter : IDisposable
{
    private readonly Stream _out;
    private readonly bool _own;
    private readonly MemoryStream _buf = new();
    private static readonly JsonWriterOptions Opts = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public Dictionary<string, int> Counts { get; } = new();

    public ImxWriter(Stream output, bool ownStream = false) { _out = output; _own = ownStream; }
    public static ImxWriter Create(string path) => new(File.Create(path), true);

    public void Dispose()
    {
        _out.Flush();
        if (_own) _out.Dispose();
    }

    private void Record(string kind, Action<Utf8JsonWriter> body, bool checksum)
    {
        _buf.SetLength(0);
        using (var w = new Utf8JsonWriter(_buf, Opts))
        {
            w.WriteStartObject();
            w.WriteString("kind", kind);
            body(w);
            w.WriteEndObject();
        }
        var bytes = _buf.GetBuffer().AsSpan(0, (int)_buf.Length);
        if (checksum)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant();
            _out.Write(bytes[..^1]); // drop the closing brace
            _out.Write(Encoding.UTF8.GetBytes($",\"checksum\":\"{hash}\"}}"));
        }
        else _out.Write(bytes);
        _out.WriteByte((byte)'\n');
        Counts[kind] = Counts.TryGetValue(kind, out var n) ? n + 1 : 1;
    }

    public void Write(ImxHeader h) => Record("header", w =>
    {
        w.WriteString("format", ImxHeader.FormatName);
        w.WriteNumber("version", ImxHeader.FormatVersion);
        w.WriteString("generator", h.Generator);
        w.WriteString("units", "meter");
        w.WritePropertyName("source");
        w.WriteStartObject();
        w.WriteString("path", h.SourcePath);
        w.WriteString("name", h.SourceName);
        w.WriteString("format", h.SourceFormat);
        if (h.SourceFormatVersion != null) w.WriteString("formatVersion", h.SourceFormatVersion);
        w.WriteNumber("size", h.SourceSize);
        if (h.SourceModifiedUtc.HasValue) w.WriteString("modified", h.SourceModifiedUtc.Value.ToString("o", CultureInfo.InvariantCulture));
        if (h.SourceSha256 != null) w.WriteString("sha256", h.SourceSha256);
        w.WriteEndObject();
        if (!h.SpatialExtents.IsEmpty) { w.WritePropertyName("spatialExtents"); WriteRange(w, h.SpatialExtents); }
        if (h.Gcs != null)
        {
            w.WritePropertyName("gcs");
            w.WriteStartObject();
            if (h.Gcs.Epsg.HasValue) w.WriteNumber("epsg", h.Gcs.Epsg.Value);
            if (h.Gcs.CsMapKey != null) w.WriteString("csmapKey", h.Gcs.CsMapKey);
            if (h.Gcs.Description != null) w.WriteString("description", h.Gcs.Description);
            if (h.Gcs.UnitMeters.HasValue) w.WriteNumber("unitMeters", h.Gcs.UnitMeters.Value);
            if (h.Gcs.Wkt != null) w.WriteString("wkt", h.Gcs.Wkt);
            if (h.Gcs.Source != null) w.WriteString("source", h.Gcs.Source);
            w.WriteEndObject();
        }
    }, checksum: false);

    public void Write(ImxFont f) => Record("font", w =>
    {
        w.WriteString("key", f.Key); w.WriteString("name", f.Name); w.WriteString("type", f.Type);
    }, checksum: false);

    public void Write(ImxLineStyle s) => Record("lineStyle", w =>
    {
        w.WriteString("key", s.Key); w.WriteString("name", s.Name);
        if (s.Description != null) w.WriteString("description", s.Description);
        if (s.LinePixels.HasValue) w.WriteNumber("linePixels", s.LinePixels.Value);
        if (s.Strokes != null)
        {
            w.WritePropertyName("strokes");
            w.WriteStartArray();
            foreach (var st in s.Strokes)
            {
                w.WriteStartObject();
                w.WriteNumber("length", Num.R(st.Length)); w.WriteBoolean("dash", st.Dash);
                if (st.Width > 0) w.WriteNumber("width", Num.R(st.Width));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (s.Phase != 0) w.WriteNumber("phase", Num.R(s.Phase));
        }
        if (s.Annotation) w.WriteBoolean("annotation", true);
        if (s.Approximation != null) w.WriteString("approximation", s.Approximation);
        WriteSource(w, s.Source);
    }, checksum: true);

    public void Write(ImxCategory c) => Record("category", w =>
    {
        w.WriteString("key", c.Key); w.WriteString("name", c.Name);
        if (!string.IsNullOrEmpty(c.Description)) w.WriteString("description", c.Description);
        w.WritePropertyName("usage");
        w.WriteStartArray(); foreach (var u in c.Usage) w.WriteStringValue(u); w.WriteEndArray();
        WriteAppearance(w, c.Appearance);
        WriteSource(w, c.Source);
    }, checksum: true);

    public void Write(ImxSubCategory s) => Record("subCategory", w =>
    {
        w.WriteString("key", s.Key); w.WriteString("category", s.Category); w.WriteString("name", s.Name);
        WriteAppearance(w, s.Appearance);
    }, checksum: true);

    public void Write(ImxModel m) => Record("model", w =>
    {
        w.WriteString("key", m.Key); w.WriteString("name", m.Name);
        if (!string.IsNullOrEmpty(m.Description)) w.WriteString("description", m.Description);
        w.WriteString("type", m.Type);
        w.WriteBoolean("is3d", m.Is3d);
        if (m.MasterUnit != null) w.WriteString("masterUnit", m.MasterUnit);
        w.WriteNumber("metersPerMaster", m.MetersPerMaster);
        if (!m.Extents.IsEmpty) { w.WritePropertyName("extents"); WriteRange(w, m.Extents); }
        if (m.SheetSize.HasValue)
        {
            w.WritePropertyName("sheetSize");
            w.WriteStartArray(); w.WriteNumberValue(Num.R(m.SheetSize.Value.Width)); w.WriteNumberValue(Num.R(m.SheetSize.Value.Height)); w.WriteEndArray();
        }
        WriteSource(w, m.Source);
    }, checksum: true);

    public void Write(ImxPart p) => Record("part", w =>
    {
        w.WriteString("key", p.Key); w.WriteString("name", p.Name);
        w.WriteBoolean("is3d", p.Geometry.Is3d);
        w.WritePropertyName("geometry"); p.Geometry.WriteJson(w, DPoint3d.Zero);
        WriteSource(w, p.Source);
    }, checksum: true);

    public void Write(ImxElement e) => Record("element", w =>
    {
        w.WriteString("key", e.Key);
        w.WriteString("model", e.Model);
        w.WriteString("category", e.Category);
        w.WriteString("role", e.Role);
        if (e.Parent != null) w.WriteString("parent", e.Parent);
        if (!string.IsNullOrEmpty(e.UserLabel)) w.WriteString("userLabel", e.UserLabel);
        w.WritePropertyName("origin");
        w.WriteStartArray();
        w.WriteNumberValue(Num.R(e.Origin.X)); w.WriteNumberValue(Num.R(e.Origin.Y));
        if (e.Geometry.Is3d) w.WriteNumberValue(Num.R(e.Origin.Z));
        w.WriteEndArray();
        w.WritePropertyName("geometry"); e.Geometry.WriteJson(w, e.Origin);
        if (e.Properties.Count > 0)
        {
            w.WritePropertyName("properties");
            w.WriteStartArray();
            foreach (var g in e.Properties)
            {
                w.WriteStartObject();
                w.WriteString("source", g.Source); w.WriteString("name", g.Name);
                if (g.Schema != null) w.WriteString("schema", g.Schema);
                if (g.ClassName != null) w.WriteString("class", g.ClassName);
                w.WritePropertyName("values");
                w.WriteStartObject();
                foreach (var kv in g.Properties) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (e.SourceModifiedUtc.HasValue) w.WriteString("modified", e.SourceModifiedUtc.Value.ToString("o", CultureInfo.InvariantCulture));
        WriteSource(w, e.Source);
    }, checksum: true);

    public void Write(ImxRelationshipClass c) => Record("relationshipClass", w =>
    {
        w.WriteString("name", c.Name);
        if (c.Description != null) w.WriteString("description", c.Description);
        w.WriteString("base", "bis:ElementRefersToElements");
        if (c.SourceRole != null) w.WriteString("sourceRole", c.SourceRole);
        if (c.TargetRole != null) w.WriteString("targetRole", c.TargetRole);
        w.WritePropertyName("properties");
        w.WriteStartObject();
        foreach (var kv in c.Properties) w.WriteString(kv.Key, kv.Value);
        w.WriteEndObject();
    }, checksum: false);

    public void Write(ImxRelationship r) => Record("relationship", w =>
    {
        w.WriteString("key", r.Key);
        w.WriteString("class", r.Class);
        w.WriteString("source", r.SourceKey);
        w.WriteString("target", r.TargetKey);
        if (r.Properties.Count > 0)
        {
            w.WritePropertyName("properties");
            w.WriteStartObject();
            foreach (var kv in r.Properties) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); }
            w.WriteEndObject();
        }
    }, checksum: true);

    private static void WriteAppearance(Utf8JsonWriter w, ImxAppearance a)
    {
        w.WritePropertyName("appearance");
        w.WriteStartObject();
        w.WriteNumber("color", a.Color);
        w.WriteNumber("weight", a.Weight);
        if (a.Style != null) w.WriteString("style", a.Style);
        if (a.Priority != 0) w.WriteNumber("priority", a.Priority);
        if (a.Transparency > 0) w.WriteNumber("transparency", Math.Round(a.Transparency, 6));
        if (a.Invisible) w.WriteBoolean("invisible", true);
        if (a.DontPlot) w.WriteBoolean("dontPlot", true);
        if (a.DontLocate) w.WriteBoolean("dontLocate", true);
        w.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter w, ImxSource s)
    {
        w.WritePropertyName("source");
        w.WriteStartObject();
        w.WriteString("kind", s.Kind);
        w.WriteString("id", s.Id);
        if (s.Info != null && s.Info.Count > 0)
        {
            w.WritePropertyName("info");
            w.WriteStartObject();
            foreach (var kv in s.Info) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteRange(Utf8JsonWriter w, Range3d r)
    {
        w.WriteStartObject();
        w.WritePropertyName("low");
        w.WriteStartArray(); w.WriteNumberValue(Num.R(r.Low.X)); w.WriteNumberValue(Num.R(r.Low.Y)); w.WriteNumberValue(Num.R(r.Low.Z)); w.WriteEndArray();
        w.WritePropertyName("high");
        w.WriteStartArray(); w.WriteNumberValue(Num.R(r.High.X)); w.WriteNumberValue(Num.R(r.High.Y)); w.WriteNumberValue(Num.R(r.High.Z)); w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case uint ui: w.WriteNumberValue(ui); break;
            case ulong ul: w.WriteNumberValue(ul); break;
            case short sh: w.WriteNumberValue(sh); break;
            case byte by: w.WriteNumberValue(by); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case decimal m: w.WriteNumberValue(m); break;
            case DateTime dt: w.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture)); break;
            case DPoint3d p:
                w.WriteStartArray(); w.WriteNumberValue(p.X); w.WriteNumberValue(p.Y); w.WriteNumberValue(p.Z); w.WriteEndArray(); break;
            case System.Collections.IEnumerable seq when v is not string:
                w.WriteStartArray(); foreach (var x in seq) WriteValue(w, x); w.WriteEndArray(); break;
            default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }
}
