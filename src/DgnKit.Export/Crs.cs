using System.Globalization;
using DgnKit.Geometry;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace DgnKit.Export;

/// <summary>
/// Projects master-unit coordinates to WGS84 lon/lat for KML/GeoJSON. Built-in WKT for common US State Plane zones
/// used by DOTs (WA, NE, TX, FL, OH, NJ) and UTM; any other CRS can be supplied as WKT or a .prj file.
/// </summary>
public sealed class GeoProjector
{
    private readonly MathTransform _toWgs84;
    public string Description { get; }
    /// <summary>Factor converting master units to the CRS linear unit (e.g. survey feet → feet = 1).</summary>
    public double MasterToCrs { get; }
    /// <summary>Approximate lon/lat box where the CRS is meant to be used (null = unknown).</summary>
    public (double MinLon, double MinLat, double MaxLon, double MaxLat)? AreaOfUse { get; private set; }

    private GeoProjector(MathTransform t, string description, double masterToCrs)
    {
        _toWgs84 = t; Description = description; MasterToCrs = masterToCrs;
    }

    /// <summary>True when lon/lat lies inside <see cref="AreaOfUse"/> grown by <paramref name="marginDeg"/> (true when unknown).</summary>
    public bool IsInAreaOfUse(double lon, double lat, double marginDeg = 0.5)
    {
        if (double.IsNaN(lon) || double.IsNaN(lat) || Math.Abs(lat) > 90 || Math.Abs(lon) > 540) return false;
        if (AreaOfUse is not { } a) return true;
        return lon >= a.MinLon - marginDeg && lon <= a.MaxLon + marginDeg && lat >= a.MinLat - marginDeg && lat <= a.MaxLat + marginDeg;
    }

    // Approximate state extents (degrees) for the built-in State Plane zones.
    private static readonly Dictionary<string, (double, double, double, double)> StateBounds = new()
    {
        ["WA"] = (-124.9, 45.5, -116.8, 49.1), ["NE"] = (-104.1, 39.9, -95.3, 43.1), ["TX"] = (-106.7, 25.8, -93.5, 36.6),
        ["FL"] = (-87.7, 24.4, -79.9, 31.1), ["OH"] = (-84.9, 38.3, -80.5, 42.4), ["NJ"] = (-75.6, 38.9, -73.8, 41.4),
    };

    /// <summary>Approximate area of use of a built-in EPSG code (state extent for State Plane, zone strip for UTM).</summary>
    public static (double MinLon, double MinLat, double MaxLon, double MaxLat)? AreaOfUseFor(int epsg)
    {
        if (epsg == 4326) return (-180, -90, 180, 90);
        if ((epsg >= 32601 && epsg <= 32660) || (epsg >= 26901 && epsg <= 26923))
        {
            int zone = epsg >= 32601 ? epsg - 32600 : epsg - 26900;
            double cm = -183 + 6 * zone;
            return (cm - 6, -1, cm + 6, 85);
        }
        if (!Epsg.TryGetValue(epsg, out var e)) return null;
        string state = e.Zone.Split('-')[0];
        return StateBounds.TryGetValue(state, out var b) ? b : null;
    }

    public (double Lon, double Lat, double Z) ToWgs84(DPoint3d masterPt)
    {
        var r = _toWgs84.Transform(new[] { masterPt.X * MasterToCrs, masterPt.Y * MasterToCrs });
        return (r[0], r[1], masterPt.Z);
    }

    /// <summary>Creates a projector; <paramref name="metersPerMaster"/> is the model's master unit length.</summary>
    public static GeoProjector Create(string crs, double metersPerMaster) => Create(crs, metersPerMaster, null);

    /// <summary>
    /// Creates a projector from a coordinate system decoded from the DGN file: a built-in EPSG definition (stored EPSG
    /// code, CS-MAP key name or ESRI name), else the WKT stored in / built from the file.
    /// </summary>
    public static GeoProjector FromGcs(DgnKit.Model.GeoCoordinateInfo gcs, double metersPerMaster)
    {
        string origin = $"file GCS '{gcs.Name}'";
        var errors = new List<string>();
        foreach (var crs in Candidates(gcs))
        {
            try
            {
                var pr = Create(crs, metersPerMaster, origin);
                if (pr.AreaOfUse == null && gcs.MinLongitude is double x0 && gcs.MinLatitude is double y0 && gcs.MaxLongitude is double x1 && gcs.MaxLatitude is double y1)
                    pr.AreaOfUse = (x0, y0, x1, y1);
                return pr;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or NotImplementedException or FormatException or InvalidOperationException)
            { errors.Add($"{crs.Substring(0, Math.Min(crs.Length, 40))}: {ex.Message}"); }
        }
        throw new ArgumentException($"{origin} ({gcs.Source}) has no usable definition" + (errors.Count > 0 ? ": " + string.Join("; ", errors) : ""));
    }

    private static IEnumerable<string> Candidates(DgnKit.Model.GeoCoordinateInfo g)
    {
        if (g.Epsg is int e && (e == 4326 || Epsg.ContainsKey(e) || EpsgWkt(e, out _) != null)) yield return "EPSG:" + e;
        if (g.Name != null)
        {
            if (CsMapKeyToEpsg(g.Name) is int k) yield return "EPSG:" + k;
            if (EsriNameToEpsg(g.Name) is int n) yield return "EPSG:" + n;
        }
        if (!string.IsNullOrWhiteSpace(g.Wkt)) yield return g.Wkt!;
    }

    private static GeoProjector Create(string crs, double metersPerMaster, string? origin)
    {
        string wkt = ResolveWkt(crs, out string desc);
        if (origin != null) desc = $"{desc}, from {origin}";
        var csf = new CoordinateSystemFactory();
        var src = csf.CreateFromWkt(wkt);
        var ctf = new CoordinateTransformationFactory();
        var tr = ctf.CreateFromCoordinateSystems(src, GeographicCoordinateSystem.WGS84);
        double unitMeters = 1.0;
        if (src is ProjectedCoordinateSystem pcs) unitMeters = pcs.LinearUnit.MetersPerUnit;
        // metersPerMaster <= 0 means "unknown physical unit" (V7): master units are taken to be CRS units.
        double masterToCrs = metersPerMaster > 0 ? metersPerMaster / unitMeters : 1.0;
        var result = new GeoProjector(tr.MathTransform, desc, masterToCrs);
        string code = crs.Trim().ToUpperInvariant();
        int? epsg = code.StartsWith("EPSG:", StringComparison.Ordinal) && int.TryParse(code.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n
            : int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : CsMapKeyToEpsg(crs);
        if (epsg.HasValue) result.AreaOfUse = AreaOfUseFor(epsg.Value);
        return result;
    }

    public static string ResolveWkt(string crs, out string description)
    {
        crs = crs.Trim();
        if (File.Exists(crs)) { description = Path.GetFileName(crs); return File.ReadAllText(crs); }
        if (crs.StartsWith("PROJCS", StringComparison.OrdinalIgnoreCase) || crs.StartsWith("GEOGCS", StringComparison.OrdinalIgnoreCase))
        { description = "custom WKT"; return crs; }
        string code = crs.ToUpperInvariant().Replace("EPSG:", "").Trim();
        if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out int epsg))
        {
            var w = EpsgWkt(epsg, out description);
            if (w != null) return w;
            throw new ArgumentException($"EPSG:{epsg} is not in the built-in table; pass WKT or a .prj file instead.");
        }
        var byKey = CsMapKeyToEpsg(crs);
        if (byKey.HasValue) return EpsgWkt(byKey.Value, out description)!;
        throw new ArgumentException($"Unrecognised CRS '{crs}'.");
    }

    private const double FtUs = 0.3048006096012192;

    private static string Nad83(string name, string proj, string parms, double unit, int epsg) =>
        $"PROJCS[\"{name}\",GEOGCS[\"NAD83\",DATUM[\"North_American_Datum_1983\",SPHEROID[\"GRS 1980\",6378137,298.257222101]]," +
        $"PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"{proj}\"],{parms}," +
        $"UNIT[\"{(unit == 1 ? "metre" : "US survey foot")}\",{unit.ToString("R", CultureInfo.InvariantCulture)}],AUTHORITY[\"EPSG\",\"{epsg}\"]]";

    private static string Lcc(double lat1, double lat2, double lat0, double lon0, double x0m, double y0m, double unit) =>
        string.Format(CultureInfo.InvariantCulture,
            "PARAMETER[\"standard_parallel_1\",{0}],PARAMETER[\"standard_parallel_2\",{1}],PARAMETER[\"latitude_of_origin\",{2}],PARAMETER[\"central_meridian\",{3}],PARAMETER[\"false_easting\",{4}],PARAMETER[\"false_northing\",{5}]",
            lat1, lat2, lat0, lon0, x0m / unit, y0m / unit);

    private static string Tm(double lat0, double lon0, double k, double x0m, double y0m, double unit) =>
        string.Format(CultureInfo.InvariantCulture,
            "PARAMETER[\"latitude_of_origin\",{0}],PARAMETER[\"central_meridian\",{1}],PARAMETER[\"scale_factor\",{2}],PARAMETER[\"false_easting\",{3}],PARAMETER[\"false_northing\",{4}]",
            lat0, lon0, k, x0m / unit, y0m / unit);

    private sealed record Zone(string Name, bool IsLcc, double A, double B, double C, double D, double X0, double Y0);

    // State plane NAD83 zone definitions (meters for false origins). LCC: A=lat1,B=lat2,C=lat0,D=lon0. TM: A=lat0,B=lon0,C=k.
    private static readonly Dictionary<string, Zone> Zones = new()
    {
        ["WA-N"] = new("Washington North", true, 48.73333333333333, 47.5, 47.0, -120.8333333333333, 500000, 0),
        ["WA-S"] = new("Washington South", true, 47.33333333333334, 45.83333333333334, 45.33333333333334, -120.5, 500000, 0),
        ["NE"] = new("Nebraska", true, 43.0, 40.0, 39.83333333333334, -100.0, 500000, 0),
        ["TX-N"] = new("Texas North", true, 36.18333333333333, 34.65, 34.0, -101.5, 200000, 1000000),
        ["TX-NC"] = new("Texas North Central", true, 33.96666666666667, 32.13333333333333, 31.66666666666667, -98.5, 600000, 2000000),
        ["TX-C"] = new("Texas Central", true, 31.88333333333333, 30.11666666666667, 29.66666666666667, -100.3333333333333, 700000, 3000000),
        ["TX-SC"] = new("Texas South Central", true, 30.28333333333333, 28.38333333333333, 27.83333333333333, -99.0, 600000, 4000000),
        ["TX-S"] = new("Texas South", true, 27.83333333333333, 26.16666666666667, 25.66666666666667, -98.5, 300000, 5000000),
        ["FL-E"] = new("Florida East", false, 24.33333333333333, -81.0, 0.999941177, 0, 200000, 0),
        ["FL-W"] = new("Florida West", false, 24.33333333333333, -82.0, 0.999941177, 0, 200000, 0),
        ["FL-N"] = new("Florida North", true, 30.75, 29.58333333333333, 29.0, -84.5, 600000, 0),
        ["OH-N"] = new("Ohio North", true, 41.7, 40.43333333333333, 39.66666666666666, -82.5, 600000, 0),
        ["OH-S"] = new("Ohio South", true, 40.03333333333333, 38.73333333333333, 38.0, -82.5, 600000, 0),
        ["NJ"] = new("New Jersey", false, 38.83333333333334, -74.5, 0.9999, 0, 150000, 0),
    };

    // EPSG code → (zone, unit)
    private static readonly Dictionary<int, (string Zone, double Unit)> Epsg = new()
    {
        [32148] = ("WA-N", 1), [32149] = ("WA-S", 1), [2285] = ("WA-N", FtUs), [2286] = ("WA-S", FtUs),
        [2926] = ("WA-N", FtUs), [2927] = ("WA-S", FtUs), [6597] = ("WA-N", FtUs), [6599] = ("WA-S", FtUs),
        [32104] = ("NE", 1), [26852] = ("NE", FtUs), [6880] = ("NE", FtUs),
        [32137] = ("TX-N", 1), [32138] = ("TX-NC", 1), [32139] = ("TX-C", 1), [32140] = ("TX-SC", 1), [32141] = ("TX-S", 1),
        [2275] = ("TX-N", FtUs), [2276] = ("TX-NC", FtUs), [2277] = ("TX-C", FtUs), [2278] = ("TX-SC", FtUs), [2279] = ("TX-S", FtUs),
        [2236] = ("FL-E", FtUs), [2237] = ("FL-W", FtUs), [2238] = ("FL-N", FtUs), [26958] = ("FL-E", 1), [26959] = ("FL-W", 1), [26960] = ("FL-N", 1),
        [3734] = ("OH-N", FtUs), [3735] = ("OH-S", FtUs), [32122] = ("OH-N", 1), [32123] = ("OH-S", 1),
        [3424] = ("NJ", FtUs), [32111] = ("NJ", 1),
    };

    public static IReadOnlyCollection<int> BuiltInEpsgCodes => Epsg.Keys;

    public static string? EpsgWkt(int epsg, out string description)
    {
        if (epsg == 4326) { description = "WGS 84"; return GeographicCoordinateSystem.WGS84.WKT; }
        if ((epsg >= 32601 && epsg <= 32660) || (epsg >= 26901 && epsg <= 26923))
        {
            bool wgs = epsg >= 32601;
            int zone = wgs ? epsg - 32600 : epsg - 26900;
            description = (wgs ? "WGS 84 / UTM zone " : "NAD83 / UTM zone ") + zone + "N";
            string geog = wgs
                ? "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]"
                : "GEOGCS[\"NAD83\",DATUM[\"North_American_Datum_1983\",SPHEROID[\"GRS 1980\",6378137,298.257222101]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";
            return $"PROJCS[\"{description}\",{geog},PROJECTION[\"Transverse_Mercator\"]," + Tm(0, -183 + 6 * zone, 0.9996, 500000, 0, 1) + $",UNIT[\"metre\",1],AUTHORITY[\"EPSG\",\"{epsg}\"]]";
        }
        if (!Epsg.TryGetValue(epsg, out var e)) { description = ""; return null; }
        var z = Zones[e.Zone];
        description = $"NAD83 / {z.Name}{(e.Unit == 1 ? "" : " (ftUS)")} EPSG:{epsg}";
        string parms = z.IsLcc ? Lcc(z.A, z.B, z.C, z.D, z.X0, z.Y0, e.Unit) : Tm(z.A, z.B, z.C, z.X0, z.Y0, e.Unit);
        return Nad83(description, z.IsLcc ? "Lambert_Conformal_Conic_2SP" : "Transverse_Mercator", parms, e.Unit, epsg);
    }

    /// <summary>Maps common CS-MAP / MicroStation GCS key names (as stored in DGN geo-coordinate data) to EPSG.</summary>
    public static int? CsMapKeyToEpsg(string key) => DgnKit.Model.GcsKeys.CsMapKeyToEpsg(key);

    /// <summary>Maps ESRI State Plane PROJCS names (GIS feature SpatialRef WKT) to EPSG.</summary>
    public static int? EsriNameToEpsg(string name) => DgnKit.Model.GcsKeys.EsriNameToEpsg(name);
}
