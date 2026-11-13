using System.Globalization;
using System.Text;
using DgnKit.Elements;
using DgnKit.Graphics;
using DgnKit.Model;

namespace DgnKit.Export;

internal static class ExportHelpers
{
    public static DgnModel ResolveModel(DgnFile f, ExportOptions o) =>
        (o.ModelName != null ? f.FindModel(o.ModelName) : f.PrimaryModel)
        ?? throw new InvalidOperationException(o.ModelName != null ? $"Model '{o.ModelName}' not found" : "File has no models");

    /// <summary>Meters per master unit, or 0 when the file carries no physical unit definition (V7).</summary>
    public static double MetersPerMaster(DgnFile f, DgnModel m) => f.Format == DgnFormat.V7 ? 0 : m.Units.MetersPerMaster;

    /// <summary>True when the caller asked for untransformed master-unit coordinates (<c>--crs local</c>).</summary>
    public static bool IsLocalCrs(string? crs) =>
        crs != null && (crs.Trim().Equals("local", StringComparison.OrdinalIgnoreCase) || crs.Trim().Equals("none", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Source CRS for geographic exports: <see cref="ExportOptions.Crs"/> when given, otherwise the model's own GCS, otherwise
    /// the file-level GCS (another model's GCS, or the SpatialRef of imported GIS features). A file-level fallback is used
    /// only if the model's data actually projects into the CRS's area of use (vicinity maps and sheets often hold scaled GIS
    /// data). Returns null when there is none (or "local" was requested); problems go to the report.
    /// </summary>
    public static GeoProjector? ResolveProjector(DgnFile f, DgnModel m, ExportOptions o, ExportReport report, IReadOnlyList<Primitive> prims)
    {
        double mpm = MetersPerMaster(f, m);
        if (IsLocalCrs(o.Crs)) return null;
        if (o.Crs != null) return GeoProjector.Create(o.Crs, mpm);
        var gcs = m.GeoCoordinateSystem;
        bool fallback = gcs == null;
        gcs ??= f.GeoCoordinateSystem;
        if (gcs == null) return null;
        GeoProjector pr;
        try { pr = GeoProjector.FromGcs(gcs, mpm); }
        catch (ArgumentException ex) { report.Warnings.Add(ex.Message); return null; }
        var med = MedianLonLat(pr, prims);
        if (med.HasValue && !pr.IsInAreaOfUse(med.Value.Lon, med.Value.Lat))
        {
            string msg = $"GCS '{gcs.Name}' ({gcs.Source}) puts model '{m.Name}' at lon/lat {med.Value.Lon:F3},{med.Value.Lat:F3}, outside its area of use";
            if (fallback) { report.Warnings.Add(msg + "; not applied (pass --crs to force)"); return null; }
            report.Warnings.Add(msg);
        }
        return pr;
    }

    /// <summary>Median projected lon/lat of (a sample of) the primitives' points; null when there are none.</summary>
    private static (double Lon, double Lat)? MedianLonLat(GeoProjector pr, IReadOnlyList<Primitive> prims)
    {
        var pts = prims.SelectMany(p => p.Points).Where(q => q.IsFinite).ToList();
        if (pts.Count == 0) return null;
        int step = Math.Max(1, pts.Count / 1000);
        var lons = new List<double>(); var lats = new List<double>();
        for (int i = 0; i < pts.Count; i += step)
        {
            try { var r = pr.ToWgs84(pts[i]); if (double.IsFinite(r.Lon) && double.IsFinite(r.Lat)) { lons.Add(r.Lon); lats.Add(r.Lat); } }
            catch (Exception) { /* outside projection domain */ }
        }
        if (lons.Count == 0) return (double.NaN, double.NaN);
        lons.Sort(); lats.Sort();
        return (lons[lons.Count / 2], lats[lats.Count / 2]);
    }

    /// <summary>Flat attribute set for a primitive: identity, symbology, and element metadata.</summary>
    public static Dictionary<string, object?> Attributes(Primitive p, DgnFile f)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ElementId"] = p.Source.Id,
            ["ElementType"] = p.Source.TypeName,
            ["Level"] = p.LevelName,
            ["LevelId"] = p.LevelId,
            ["Color"] = p.Symbology.Color.ToHex(),
            ["ColorIndex"] = p.Symbology.ColorIndex,
            ["Weight"] = p.Symbology.WeightIndex,
            ["LineStyle"] = p.Symbology.StyleName,
        };
        if (p.Leaf != p.Source) d["LeafType"] = p.Leaf.TypeName;
        if (p.CellName != null) d["Cell"] = p.CellName;
        if (p.ReferencePath != null) d["Reference"] = p.ReferencePath;
        if (p.Text != null) d["Text"] = p.Text;
        if (p.Source.Class != ElementClass.Primary) d["Class"] = p.Source.Class.ToString();
        foreach (var g in p.Source.PropertyGroups)
        {
            if (g.Source == "Strings" && g.Properties.Count == 1 && g.Properties.ContainsKey("Name")) continue;
            foreach (var kv in g.Properties)
            {
                if (kv.Key.StartsWith("$", StringComparison.Ordinal)) continue;
                string key = g.Name + "." + kv.Key;
                if (!d.ContainsKey(key)) d[key] = kv.Value;
            }
        }
        return d;
    }

    public static string SafeLayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "0";
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append("<>/\\\":;?*|=,`".IndexOf(c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length > 255 ? s.Substring(0, 255) : s;
    }

    public static string F(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);
}
