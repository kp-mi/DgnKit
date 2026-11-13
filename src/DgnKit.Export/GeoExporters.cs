using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DgnKit.Graphics;
using DgnKit.Model;
using DPoint3d = DgnKit.Geometry.DPoint3d;

namespace DgnKit.Export;

/// <summary>
/// Shared geographic pipeline: primitives → (optionally projected) coordinate rings/lines/points.
/// The source CRS is <see cref="ExportOptions.Crs"/>, else the GCS stored in the DGN model/file. Without either (or with
/// Crs = "local"), coordinates stay in master units (GeoJSON gets a "dgnkit:crs" note; KML requires a CRS).
/// </summary>
internal static class GeoGeometry
{
    public static List<DPoint3d> Coords(Primitive p, double chordTol)
    {
        return p.Kind switch
        {
            PrimitiveKind.Arc => p.ArcPoints(16, chordTol).ToList(),
            _ => p.Points,
        };
    }

    public static (double X, double Y, double Z) Project(GeoProjector? pr, DPoint3d q)
    {
        if (pr == null) return (q.X, q.Y, q.Z);
        var r = pr.ToWgs84(q);
        return (r.Lon, r.Lat, q.Z);
    }
}

public sealed class GeoJsonExporter : IExporter
{
    public string Format => "geojson";

    public ExportReport Export(DgnFile file, string outputPath, ExportOptions options)
    {
        var report = new ExportReport();
        var model = ExportHelpers.ResolveModel(file, options);
        var dl = options.ToDisplayListOptions(); dl.ModelName = model.Name;
        var prims = DisplayListBuilder.Build(file, model, dl);
        report.Primitives = prims.Count;
        GeoProjector? pr = ExportHelpers.ResolveProjector(file, model, options, report, prims);
        if (pr == null && !ExportHelpers.IsLocalCrs(options.Crs))
            report.Warnings.Add("no --crs given and the file has no usable GCS: coordinates are in DGN master units (not WGS84)");
        double tol = options.ChordTolerance > 0 ? options.ChordTolerance : 0;

        using var fs = File.Create(outputPath);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteString("type", "FeatureCollection");
        w.WriteString("name", Path.GetFileNameWithoutExtension(file.Path ?? "dgn"));
        if (pr == null)
        {
            w.WriteStartObject("dgnkit:crs");
            w.WriteString("note", "Coordinates are DGN master units; supply a CRS to obtain WGS84 lon/lat.");
            w.WriteString("masterUnits", model.Units.Master.Label);
            w.WriteEndObject();
        }
        else
        {
            w.WriteStartObject("dgnkit:crs");
            w.WriteString("projectedFrom", pr.Description);
            w.WriteEndObject();
        }
        w.WriteStartArray("features");
        foreach (var p in prims)
        {
            var c = GeoGeometry.Coords(p, tol);
            if (c.Count == 0) continue;
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteStartObject("geometry");
            switch (p.Kind)
            {
                case PrimitiveKind.Point:
                case PrimitiveKind.Text:
                    w.WriteString("type", "Point");
                    w.WritePropertyName("coordinates"); Pos(w, GeoGeometry.Project(pr, c[0]));
                    report.Count("Point");
                    break;
                case PrimitiveKind.Mesh when p.Faces != null:
                    w.WriteString("type", "MultiPolygon");
                    w.WriteStartArray("coordinates");
                    foreach (var f in p.Faces)
                    {
                        w.WriteStartArray(); w.WriteStartArray();
                        foreach (var i in f) Pos(w, GeoGeometry.Project(pr, p.Points[i]));
                        Pos(w, GeoGeometry.Project(pr, p.Points[f[0]]));
                        w.WriteEndArray(); w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    report.Count("MultiPolygon");
                    break;
                default:
                    bool polygon = p.Closed && c.Count >= 3;
                    w.WriteString("type", polygon ? "Polygon" : "LineString");
                    w.WriteStartArray("coordinates");
                    if (polygon) w.WriteStartArray();
                    foreach (var q in c) Pos(w, GeoGeometry.Project(pr, q));
                    if (polygon && c[0].DistanceTo(c[c.Count - 1]) > 0) Pos(w, GeoGeometry.Project(pr, c[0]));
                    if (polygon) w.WriteEndArray();
                    w.WriteEndArray();
                    report.Count(polygon ? "Polygon" : "LineString");
                    break;
            }
            w.WriteEndObject();
            w.WriteStartObject("properties");
            foreach (var kv in ExportHelpers.Attributes(p, file)) WriteValue(w, kv.Key, kv.Value);
            if (p.Kind == PrimitiveKind.Text) { w.WriteNumber("TextHeight", p.TextHeight); w.WriteNumber("TextRotationDeg", Math.Atan2(p.TextRotation.M10, p.TextRotation.M00) * 180 / Math.PI); }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        return report;
    }

    private static void Pos(Utf8JsonWriter w, (double X, double Y, double Z) p)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Math.Round(p.X, 9)); w.WriteNumberValue(Math.Round(p.Y, 9));
        if (p.Z != 0) w.WriteNumberValue(Math.Round(p.Z, 6));
        w.WriteEndArray();
    }

    internal static void WriteValue(Utf8JsonWriter w, string k, object? v)
    {
        switch (v)
        {
            case null: w.WriteNull(k); break;
            case string s: w.WriteString(k, s); break;
            case bool b: w.WriteBoolean(k, b); break;
            case int i: w.WriteNumber(k, i); break;
            case uint u: w.WriteNumber(k, u); break;
            case long l: w.WriteNumber(k, l); break;
            case ulong ul: w.WriteNumber(k, ul); break;
            case double d when double.IsFinite(d): w.WriteNumber(k, d); break;
            default: w.WriteString(k, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }
}

/// <summary>KML / KMZ writer: one folder per level, styles from colour/weight, ExtendedData from metadata.</summary>
public sealed class KmlExporter : IExporter
{
    private readonly bool _kmz;
    public KmlExporter(bool kmz) { _kmz = kmz; }
    public string Format => _kmz ? "kmz" : "kml";

    public ExportReport Export(DgnFile file, string outputPath, ExportOptions options)
    {
        var report = new ExportReport();
        var model = ExportHelpers.ResolveModel(file, options);
        var dl = options.ToDisplayListOptions(); dl.ModelName = model.Name;
        var prims = DisplayListBuilder.Build(file, model, dl);
        report.Primitives = prims.Count;
        var pr = ExportHelpers.ResolveProjector(file, model, options, report, prims)
            ?? throw new InvalidOperationException("KML/KMZ requires a source coordinate system: the model has no usable GCS, so pass --crs EPSG:#### (e.g. EPSG:2927 for WA South ftUS) or a .prj file."
                + (report.Warnings.Count > 0 ? " (" + string.Join("; ", report.Warnings) + ")" : ""));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\">");
        sb.AppendLine("<Document>");
        sb.Append("<name>").Append(Esc(Path.GetFileName(file.Path ?? "dgn"))).AppendLine("</name>");
        sb.Append("<description>").Append(Esc($"Converted by DgnKit from model '{model.Name}' ({pr.Description})")).AppendLine("</description>");

        // styles: one per (color, weight, filled)
        var styles = new Dictionary<string, string>();
        string StyleId(Primitive p)
        {
            var c = p.Symbology.Color;
            if (c.R > 240 && c.G > 240 && c.B > 240) c = new RgbColor(0, 0, 0); // white → black for map backgrounds
            string key = $"{c.ToHex()}_{p.Symbology.WeightIndex}_{(p.Symbology.Filled ? 1 : 0)}";
            if (styles.ContainsKey(key)) return styles[key];
            string id = "s" + styles.Count;
            styles[key] = id;
            string abgr = $"ff{c.B:x2}{c.G:x2}{c.R:x2}";
            double width = Math.Max(1, p.Symbology.WeightMm * 4);
            var fc = p.Symbology.FillColor ?? c;
            string fill = p.Symbology.Filled ? $"<PolyStyle><color>99{fc.B:x2}{fc.G:x2}{fc.R:x2}</color></PolyStyle>" : "<PolyStyle><fill>0</fill></PolyStyle>";
            sb.Append($"<Style id=\"{id}\"><LineStyle><color>{abgr}</color><width>{ExportHelpers.F(width)}</width></LineStyle>{fill}<IconStyle><color>{abgr}</color><scale>0.6</scale></IconStyle><LabelStyle><color>{abgr}</color><scale>0.8</scale></LabelStyle></Style>\n");
            return id;
        }

        var body = new StringBuilder();
        foreach (var lvl in prims.GroupBy(p => p.LevelName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            body.Append("<Folder><name>").Append(Esc(lvl.Key)).AppendLine("</name>");
            foreach (var p in lvl)
            {
                var c = GeoGeometry.Coords(p, options.ChordTolerance);
                if (c.Count == 0) continue;
                body.Append("<Placemark>");
                string name = p.Text ?? p.CellName ?? p.Source.TypeName;
                body.Append("<name>").Append(Esc(name)).Append("</name>");
                body.Append("<styleUrl>#").Append(StyleId(p)).Append("</styleUrl>");
                body.Append("<ExtendedData>");
                foreach (var kv in ExportHelpers.Attributes(p, file))
                    body.Append("<Data name=\"").Append(Esc(kv.Key)).Append("\"><value>").Append(Esc(Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? "")).Append("</value></Data>");
                body.Append("</ExtendedData>");
                switch (p.Kind)
                {
                    case PrimitiveKind.Point:
                    case PrimitiveKind.Text:
                        body.Append("<Point><coordinates>").Append(Coord(pr, c[0])).Append("</coordinates></Point>");
                        report.Count("Point");
                        break;
                    default:
                        if (p.Closed && c.Count >= 3)
                        {
                            body.Append("<Polygon><outerBoundaryIs><LinearRing><coordinates>");
                            foreach (var q in c) body.Append(Coord(pr, q)).Append(' ');
                            body.Append(Coord(pr, c[0]));
                            body.Append("</coordinates></LinearRing></outerBoundaryIs></Polygon>");
                            report.Count("Polygon");
                        }
                        else
                        {
                            body.Append("<LineString><tessellate>1</tessellate><coordinates>");
                            foreach (var q in c) body.Append(Coord(pr, q)).Append(' ');
                            body.Append("</coordinates></LineString>");
                            report.Count("LineString");
                        }
                        break;
                }
                body.AppendLine("</Placemark>");
            }
            body.AppendLine("</Folder>");
        }
        sb.Append(body);
        sb.AppendLine("</Document>");
        sb.AppendLine("</kml>");

        var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
        if (_kmz)
        {
            using var fs = File.Create(outputPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("doc.kml", CompressionLevel.Optimal);
            using var es = entry.Open();
            es.Write(bytes, 0, bytes.Length);
        }
        else File.WriteAllBytes(outputPath, bytes);
        return report;
    }

    private static string Coord(GeoProjector pr, DPoint3d q)
    {
        var r = pr.ToWgs84(q);
        return string.Create(CultureInfo.InvariantCulture, $"{r.Lon:0.#########},{r.Lat:0.#########}");
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
