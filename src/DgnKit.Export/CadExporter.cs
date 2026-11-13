using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using ACadSharp.XData;
using CSMath;
using DgnKit.Graphics;
using DgnKit.Model;
using AColor = ACadSharp.Color;
using DPoint3d = DgnKit.Geometry.DPoint3d;

namespace DgnKit.Export;

/// <summary>
/// DXF / DWG writer (ACadSharp). Levels → layers (with level colour/weight/style when known), line styles → linetypes,
/// element symbology → entity true-colour/lineweight/linetype, element metadata → XData (app "DGNKIT").
/// </summary>
public sealed class CadExporter : IExporter
{
    private readonly bool _dwg;
    private readonly bool _binary;
    public CadExporter(bool binaryDxf, bool dwg) { _binary = binaryDxf; _dwg = dwg; }
    public string Format => _dwg ? "dwg" : "dxf";

    private const string AppName = "DGNKIT";

    public ExportReport Export(DgnFile file, string outputPath, ExportOptions options)
    {
        var report = new ExportReport();
        var model = ExportHelpers.ResolveModel(file, options);
        var dl = options.ToDisplayListOptions(); dl.ModelName = model.Name;
        var prims = DisplayListBuilder.Build(file, model, dl);
        report.Primitives = prims.Count;

        var version = Enum.TryParse<ACadVersion>(options.CadVersion, out var v) ? v : ACadVersion.AC1032;
        var doc = new CadDocument(version);
        TrySetUnits(doc, model);
        var app = new AppId(AppName);
        doc.AppIds.Add(app);

        // extents → linetype pattern scale (built-in MicroStation styles are device-based; size them to ~1/400 of extent)
        var ext = DgnKit.Geometry.Range3d.Empty;
        foreach (var p in prims) ext.Extend(p.Range());
        double patternUnit = ext.IsEmpty ? 1 : Math.Max(ext.XSize, ext.YSize) / 400.0 / 3.0;

        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        var lineTypes = new Dictionary<string, LineType>(StringComparer.OrdinalIgnoreCase);

        Layer GetLayer(Primitive p)
        {
            string name = ExportHelpers.SafeLayerName(p.LevelName);
            if (layers.TryGetValue(name, out var l)) return l;
            l = doc.Layers.TryGetValue(name, out var existing) ? existing : new Layer(name);
            var lvl = file.Levels[p.LevelId];
            if (lvl != null && lvl.HasSymbology)
            {
                var c = DisplayListBuilder.ColorOf(file, lvl.Color);
                l.Color = new AColor(c.R, c.G, c.B);
                l.LineWeight = ToLineWeight(WeightTable.ToMm((int)Math.Min(31, lvl.Weight)));
                l.IsOn = lvl.IsDisplayed;
            }
            if (!doc.Layers.Contains(name)) doc.Layers.Add(l);
            layers[name] = l;
            return l;
        }

        // pattern length (drawing units) each custom linetype was created with → per-entity LineTypeScale
        var baseLength = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        LineType? GetLineType(ResolvedSymbology s)
        {
            if (s.StyleNumber == 0) return null;
            string name = ExportHelpers.SafeLayerName(s.StyleName);
            if (lineTypes.TryGetValue(name, out var lt)) return lt;
            if (doc.LineTypes.TryGetValue(name, out var ex)) { lineTypes[name] = ex; return ex; }
            lt = new LineType(name) { Description = $"MicroStation line style {s.StyleNumber} ({s.StyleName})" };
            // decoded custom styles: pattern in drawing (root master) units; built-in styles: device pattern sized to the extent
            double[]? pat = s.DashPattern ?? LineStyleDefinition.BuiltInPatternMm(s.StyleNumber)?.Select(x => x * patternUnit).ToArray();
            if (pat != null)
            {
                for (int i = 0; i < pat.Length; i++)
                    lt.AddSegment(new LineType.Segment { Length = i % 2 == 0 ? pat[i] : -pat[i] });
                if (s.DashPattern != null) baseLength[name] = pat.Sum();
            }
            doc.LineTypes.Add(lt);
            lineTypes[name] = lt;
            return lt;
        }

        double LineTypeScale(ResolvedSymbology s)
        {
            if (s.DashPattern == null || !baseLength.TryGetValue(ExportHelpers.SafeLayerName(s.StyleName), out double b) || b <= 0) return 1;
            double k = s.DashPattern.Sum() / b;
            return k > 0 && !double.IsInfinity(k) ? k : 1;
        }

        foreach (var p in prims)
        {
            try
            {
                var ents = ToEntities(p, report);
                foreach (var e in ents)
                {
                    e.Layer = GetLayer(p);
                    var c = p.Symbology.Color;
                    e.Color = new AColor(c.R, c.G, c.B);
                    e.LineWeight = ToLineWeight(p.Symbology.WeightMm);
                    var lt = GetLineType(p.Symbology);
                    if (lt != null)
                    {
                        e.LineType = lt;
                        double lts = LineTypeScale(p.Symbology);
                        if (Math.Abs(lts - 1) > 1e-9) e.LineTypeScale = lts;
                    }
                    AttachXData(e, app, p, file);
                    doc.Entities.Add(e);
                }
            }
            catch (Exception ex)
            {
                report.Skipped++;
                if (report.Warnings.Count < 50) report.Warnings.Add($"element {p.Leaf.Id}: {ex.Message}");
            }
        }

        if (_dwg) DwgWriter.Write(outputPath, doc, new DwgWriterConfiguration(), null);
        else DxfWriter.Write(outputPath, doc, _binary, new DxfWriterConfiguration(), null);
        return report;
    }

    private static XYZ X(DPoint3d p) => new(p.X, p.Y, p.Z);

    private static IEnumerable<Entity> ToEntities(Primitive p, ExportReport report)
    {
        switch (p.Kind)
        {
            case PrimitiveKind.Polyline:
            {
                if (p.Points.Count < 2) yield break;
                bool flat = p.Points.All(q => Math.Abs(q.Z - p.Points[0].Z) < 1e-9);
                if (p.Points.Count == 2 && !p.Closed)
                {
                    report.Count("Line");
                    yield return new Line(X(p.Points[0]), X(p.Points[1]));
                }
                else if (flat)
                {
                    report.Count("LwPolyline");
                    var pl = new LwPolyline(p.Points.Select(q => new LwPolyline.Vertex(q.X, q.Y))) { Elevation = p.Points[0].Z, IsClosed = p.Closed };
                    yield return pl;
                }
                else
                {
                    report.Count("Polyline3D");
                    yield return new Polyline3D(p.Points.Select(X), p.Closed);
                }
                if (p.Closed && p.Symbology.Filled && flat && p.Points.Count >= 3)
                {
                    var h = new Hatch { IsSolid = true, Elevation = p.Points[0].Z };
                    h.Pattern = new HatchPattern("SOLID");
                    var path = new Hatch.BoundaryPath();
                    path.Edges.Add(new Hatch.BoundaryPath.Polyline(p.Points.Select(q => new XYZ(q.X, q.Y, 0)), true));
                    h.Paths.Add(path);
                    if (p.Symbology.FillColor is { } fc) h.Color = new AColor(fc.R, fc.G, fc.B);
                    report.Count("Hatch");
                    yield return h;
                }
                break;
            }
            case PrimitiveKind.Arc:
            {
                double rx = p.AxisX.Length, ry = p.AxisY.Length;
                if (rx <= 0 || ry <= 0) yield break;
                var normal = p.AxisX.Cross(p.AxisY).Normalized();
                bool circular = Math.Abs(rx - ry) <= 1e-9 * Math.Max(rx, 1) && Math.Abs(p.AxisX.Normalized().Dot(p.AxisY.Normalized())) < 1e-9;
                bool zUp = Math.Abs(normal.Z - 1) < 1e-12;
                if (circular && zUp)
                {
                    double baseAng = Math.Atan2(p.AxisX.Y, p.AxisX.X);
                    if (p.Closed) { report.Count("Circle"); yield return new Circle(X(p.Center), rx); }
                    else
                    {
                        double a0 = baseAng + p.StartAngle, a1 = baseAng + p.StartAngle + p.SweepAngle;
                        if (p.SweepAngle < 0) (a0, a1) = (a1, a0);
                        report.Count("Arc");
                        yield return new Arc(X(p.Center), rx, Norm(a0), Norm(a1));
                    }
                }
                else
                {
                    // general ellipse (or tilted circle): major axis + ratio, parameters relative to major axis
                    var major = rx >= ry ? p.AxisX : p.AxisY;
                    double ratio = Math.Min(rx, ry) / Math.Max(rx, ry);
                    double shift = rx >= ry ? 0 : -Math.PI / 2;
                    double s0 = p.StartAngle + shift, s1 = p.StartAngle + p.SweepAngle + shift;
                    if (p.SweepAngle < 0) { (s0, s1) = (s1, s0); }
                    var el = new Ellipse
                    {
                        Center = X(p.Center), MajorAxisEndPoint = X(major), Normal = X(normal), RadiusRatio = ratio,
                        StartParameter = p.Closed ? 0 : Norm(s0), EndParameter = p.Closed ? 2 * Math.PI : Norm(s1),
                    };
                    report.Count("Ellipse");
                    yield return el;
                }
                break;
            }
            case PrimitiveKind.Text:
            {
                if (string.IsNullOrEmpty(p.Text)) yield break;
                var dir = p.TextRotation.Column(0);
                double rot = Math.Atan2(dir.Y, dir.X);
                // Height = MicroStation text size as is: MicroStation sizes TrueType and RSC text by cap height, and AutoCAD's
                // TEXT height is the cap height too (SHX fonts by definition — our "Standard" style names no font file, so
                // AutoCAD falls back to an SHX font — and approximately for TrueType styles). No em conversion here, unlike
                // the PDF exporter's Helvetica (em = size / 0.718). Font widths are left to the CAD font (no WidthFactor).
                var t = new TextEntity(p.Text!.Replace("\r", "").Replace("\n", " "))
                {
                    InsertPoint = X(p.Points[0]), Height = Math.Max(1e-6, p.TextHeight), Rotation = rot,
                    WidthFactor = p.TextHeight > 0 && p.TextWidth > 0 ? p.TextWidth / p.TextHeight : 1,
                };
                report.Count("Text");
                yield return t;
                break;
            }
            case PrimitiveKind.Point:
                report.Count("Point");
                yield return new Point(X(p.Points[0]));
                break;
            case PrimitiveKind.Mesh:
                if (p.Faces == null) yield break;
                foreach (var f in p.Faces)
                {
                    if (f.Length < 3) continue;
                    var a = p.Points[f[0]]; var b = p.Points[f[1]]; var c = p.Points[f[2]]; var d = f.Length > 3 ? p.Points[f[3]] : c;
                    report.Count("3DFace");
                    yield return new Face3D { FirstCorner = X(a), SecondCorner = X(b), ThirdCorner = X(c), FourthCorner = X(d) };
                }
                break;
        }
    }

    private static double Norm(double a)
    {
        double t = a % (2 * Math.PI);
        return t < 0 ? t + 2 * Math.PI : t;
    }

    private static readonly LineWeightType[] Weights = Enum.GetValues(typeof(LineWeightType)).Cast<LineWeightType>().Where(w => (short)w >= 0).OrderBy(w => (short)w).ToArray();

    private static LineWeightType ToLineWeight(double mm)
    {
        short target = (short)Math.Round(mm * 100);
        return Weights.OrderBy(w => Math.Abs((short)w - target)).FirstOrDefault();
    }

    private static void TrySetUnits(CadDocument doc, DgnModel m)
    {
        try
        {
            double mpu = m.Units.MetersPerMaster;
            UnitsType u = Math.Abs(mpu - 0.3048006096012192) < 1e-9 ? UnitsType.USSurveyFeet
                : Math.Abs(mpu - 0.3048) < 1e-9 ? UnitsType.Feet
                : Math.Abs(mpu - 1) < 1e-9 ? UnitsType.Meters
                : Math.Abs(mpu - 0.001) < 1e-9 ? UnitsType.Millimeters
                : Math.Abs(mpu - 0.0254) < 1e-9 ? UnitsType.Inches
                : UnitsType.Unitless;
            doc.Header.InsUnits = u;
        }
        catch { /* header API differences */ }
    }

    private static void AttachXData(Entity e, AppId app, Primitive p, DgnFile f)
    {
        var recs = new List<ExtendedDataRecord>();
        int bytes = 0;
        foreach (var kv in ExportHelpers.Attributes(p, f))
        {
            string s = $"{kv.Key}={Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture)}";
            if (s.Length > 250) s = s.Substring(0, 250);
            bytes += s.Length + 4;
            if (bytes > 15000) break;
            recs.Add(new ExtendedDataString(s));
        }
        if (recs.Count > 0) e.ExtendedData.Add(app, new ExtendedData(recs));
    }
}
