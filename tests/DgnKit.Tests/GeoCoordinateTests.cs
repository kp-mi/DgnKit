using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using DgnKit.Export;
using DgnKit.Geometry;
using DgnKit.Model;
using Xunit;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>
/// Geographic coordinate systems stored in DGN files (docs/FORMAT-V8.md §7.1): MicroStation's MSGEOCOORD element and
/// the GIS FeatureDefinition SpatialRef fallback, and their use by the KML/GeoJSON exporters when no --crs is given.
/// Corpus-dependent tests are skipped when the corpus has not been downloaded.
/// </summary>
public class GeoCoordinateTests
{
    private readonly ITestOutputHelper _out;
    public GeoCoordinateTests(ITestOutputHelper o) { _out = o; }

    private static string? Corpus(string rel)
    {
        var p = Path.Combine(TestPaths.Corpus, rel);
        return File.Exists(p) ? p : null;
    }

    private static string OutDir => Directory.CreateDirectory(Path.Combine(TestPaths.Reports, "exports")).FullName;

    private const string WaSouthGcsFile = "wsdot-XE3771/316251H_BP_VM.dgn";   // MSGEOCOORD key "EPSG:2927"
    private const string WaNorthGcsFile = "wsdot-XE3736/XL7089_CF_CS.dgn";    // MSGEOCOORD key "HARN.WA-NF"
    private const string GisFile = "wsdot-XE3691/XL7087_PS_VM.dgn";           // SpatialRef only (GIS import)
    private const string ScaledGisFile = "wsdot-XE3736/01_XL7089_PS_IV.dgn";  // SpatialRef, but vicinity map is scaled

    private static void Near(double expected, double actual, double tol) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected:R} ± {tol}, got {actual:R} (Δ {actual - expected:E2})");

    private static bool InWashington(double lon, double lat) => lat > 45.5 && lat < 49.1 && lon > -124.9 && lon < -116.8;

    [Fact]
    public void MsGeoCoord_EpsgKey_Decoded()
    {
        var src = Corpus(WaSouthGcsFile); if (src == null) return;
        var f = DgnFile.Open(src);
        var g = f.DefaultModel!.GeoCoordinateSystem;
        Assert.NotNull(g);
        _out.WriteLine(g!.ToString());
        Assert.Equal("EPSG:2927", g.Name);
        Assert.Equal(2927, g.Epsg);
        Assert.Equal("NAD83(HARN) / Washington South (ftUS)", g.Description);
        Assert.Equal("LM", g.ProjectionKey);
        Assert.Equal("Lambert Conformal Conic", g.ProjectionName);
        Assert.Equal("EPSG:6152", g.DatumKey);
        Assert.Equal("Foot", g.Units);
        Assert.Equal(0.3048006096012192, g.UnitMeters!.Value, 12);
        Assert.Equal(6378137.0, g.SemiMajorAxis!.Value, 3);
        Assert.Equal(-120.5, g.CentralMeridian!.Value, 9);
        Assert.Equal(47 + 20.0 / 60, g.StandardParallel1!.Value, 9);
        Assert.Equal(45 + 50.0 / 60, g.StandardParallel2!.Value, 9);
        Assert.Equal(45 + 20.0 / 60, g.OriginLatitude!.Value, 9);
        // stored as EPSG's published 1640416.67 ftUS (= 500000 m to 1 mm)
        Assert.InRange(g.FalseEasting!.Value, 1640416.66, 1640416.68);
        Assert.Equal(0.0, g.FalseNorthing!.Value, 9);
        Assert.Contains("MSGEOCOORD", g.Source);
        Assert.Same(g, f.GeoCoordinateSystem);
    }

    [Fact]
    public void MsGeoCoord_CsMapKey_Decoded()
    {
        var src = Corpus(WaNorthGcsFile); if (src == null) return;
        var f = DgnFile.Open(src);
        var g = f.DefaultModel!.GeoCoordinateSystem;
        Assert.NotNull(g);
        _out.WriteLine(g!.ToString());
        Assert.Equal("HARN.WA-NF", g.Name);
        Assert.Equal(2926, g.Epsg);                 // stored EPSG code
        Assert.Equal("SPCSHPF", g.Group);
        Assert.Equal("NAD83/HARN-A", g.DatumKey);
        Assert.Equal("HPGN Washington, North, US Foot", g.Description);
        Assert.Equal(-(120 + 50.0 / 60), g.CentralMeridian!.Value, 9);
        Assert.Equal(48 + 44.0 / 60, g.StandardParallel1!.Value, 9);
        Assert.Equal(47.5, g.StandardParallel2!.Value, 9);
        Assert.Equal(47.0, g.OriginLatitude!.Value, 9);
        // useful range is stored for this definition
        Assert.Equal(-125.0, g.MinLongitude!.Value, 6);
        Assert.Equal(47.0, g.MinLatitude!.Value, 6);
        Assert.Equal(-(116 + 1.0 / 3), g.MaxLongitude!.Value, 6);
        Assert.Equal(49 + 1.0 / 3, g.MaxLatitude!.Value, 6);
    }

    /// <summary>
    /// The WKT built from the stored LCC parameters projects like the built-in EPSG definition (to 1e-7° ≈ 1 cm; the
    /// stored false easting is EPSG's rounded 1640416.67 ftUS vs. exactly 500000 m in the built-in WKT).
    /// </summary>
    [Theory]
    [InlineData(WaSouthGcsFile, 2927)]
    [InlineData(WaNorthGcsFile, 2926)]
    public void MsGeoCoord_BuiltWkt_MatchesEpsg(string rel, int epsg)
    {
        var src = Corpus(rel); if (src == null) return;
        var f = DgnFile.Open(src);
        var m = f.DefaultModel!;
        var g = m.GeoCoordinateSystem!;
        Assert.StartsWith("PROJCS[", g.Wkt);
        double mpm = m.Units.MetersPerMaster;
        var fromWkt = GeoProjector.Create(g.Wkt!, mpm);
        var fromEpsg = GeoProjector.Create("EPSG:" + epsg, mpm);
        int n = 0;
        foreach (var e in m.Elements.Where(e => e.IsGraphic).Take(200))
        {
            DPoint3d uor;
            if (e is DgnKit.Elements.LineElement le) uor = le.Start;
            else if (e is DgnKit.Elements.LineStringElement ls && ls.Points.Count > 0) uor = ls.Points[0];
            else continue;
            var p = m.ToMaster(uor);
            var a = fromWkt.ToWgs84(p); var b = fromEpsg.ToWgs84(p);
            Near(b.Lon, a.Lon, 1e-7);
            Near(b.Lat, a.Lat, 1e-7);
            n++;
        }
        _out.WriteLine($"{n} points compared");
    }

    [Fact]
    public void FeatureSpatialRef_IsFileFallback()
    {
        var src = Corpus(GisFile); if (src == null) return;
        var f = DgnFile.Open(src);
        Assert.All(f.Models, m => Assert.Null(m.GeoCoordinateSystem));
        var g = f.GeoCoordinateSystem;
        Assert.NotNull(g);
        Assert.Equal("NAD_1983_HARN_StatePlane_Washington_South_FIPS_4602_Feet", g!.Name);
        Assert.Equal(2927, g.Epsg);
        Assert.StartsWith("PROJCS[\"NAD_1983_HARN_StatePlane_Washington_South_FIPS_4602_Feet\"", g.Wkt);
        Assert.Contains("SpatialRef", g.Source);
    }

    /// <summary>ProjNet reads the ESRI-flavoured SpatialRef WKT and gives the same result as EPSG:2927.</summary>
    [Fact]
    public void FeatureSpatialRef_EsriWkt_ProjectsLikeEpsg()
    {
        var src = Corpus(GisFile); if (src == null) return;
        var f = DgnFile.Open(src);
        var esri = GeoProjector.Create(f.GeoCoordinateSystem!.Wkt!, 0.3048006096012192);
        var epsg = GeoProjector.Create("EPSG:2927", 0.3048006096012192);
        foreach (var p in new[] { new DPoint3d(1_700_000, 200_000, 0), new DPoint3d(1_900_000, 640_000, 0), new DPoint3d(2_100_000, 900_000, 0) })
        {
            var a = esri.ToWgs84(p); var b = epsg.ToWgs84(p);
            Near(b.Lon, a.Lon, 1e-9);
            Near(b.Lat, a.Lat, 1e-9);
        }
    }

    [Theory]
    [InlineData(GisFile)]          // file-level SpatialRef fallback, accepted (data really is in WA South ftUS)
    [InlineData(WaSouthGcsFile)]   // model MSGEOCOORD
    public void Kml_WithoutCrs_UsesFileGcs_LandsInWashington(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var f = DgnFile.Open(src);
        var model = f.Models.OrderByDescending(m => m.Elements.Count).First();
        var kml = Path.Combine(OutDir, Path.GetFileNameWithoutExtension(src) + ".filegcs.kml");
        var rep = Exporters.Get("kml").Export(f, kml, new ExportOptions { ModelName = model.Name });
        _out.WriteLine(rep.ToString());
        var x = XDocument.Load(kml);
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var coords = x.Descendants(k + "coordinates")
            .SelectMany(c => c.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Split(','))
            .Select(t => (Lon: double.Parse(t[0], CultureInfo.InvariantCulture), Lat: double.Parse(t[1], CultureInfo.InvariantCulture)))
            .ToList();
        int inWa = coords.Count(c => InWashington(c.Lon, c.Lat));
        _out.WriteLine($"{inWa}/{coords.Count} coordinates inside Washington");
        Assert.NotEmpty(coords);
        Assert.True(inWa >= coords.Count * 0.9, $"only {inWa}/{coords.Count} coordinates fall inside Washington State");
        Assert.Contains("from file GCS", x.Descendants(k + "description").First().Value);
    }

    [Fact]
    public void GeoJson_WithoutCrs_UsesModelGcs()
    {
        var src = Corpus(WaSouthGcsFile); if (src == null) return;
        var f = DgnFile.Open(src);
        var outPath = Path.Combine(OutDir, "316251H_BP_VM.filegcs.geojson");
        var rep = Exporters.Get("geojson").Export(f, outPath, new ExportOptions());
        Assert.Empty(rep.Warnings);
        using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
        Assert.Contains("EPSG:2927", doc.RootElement.GetProperty("dgnkit:crs").GetProperty("projectedFrom").GetString());
        var pts = doc.RootElement.GetProperty("features").EnumerateArray()
            .Select(ft => ft.GetProperty("geometry")).Where(gm => gm.GetProperty("type").GetString() == "Point")
            .Select(gm => gm.GetProperty("coordinates")).Select(c => (c[0].GetDouble(), c[1].GetDouble())).ToList();
        Assert.NotEmpty(pts);
        Assert.All(pts, p => Assert.True(InWashington(p.Item1, p.Item2), $"{p} outside Washington"));

        // "local" keeps master units
        var local = Path.Combine(OutDir, "316251H_BP_VM.local.geojson");
        Exporters.Get("geojson").Export(f, local, new ExportOptions { Crs = "local" });
        using var ldoc = JsonDocument.Parse(File.ReadAllText(local));
        Assert.True(ldoc.RootElement.GetProperty("dgnkit:crs").TryGetProperty("masterUnits", out _));
    }

    /// <summary>A vicinity map holds GIS features scaled down: the file-level SpatialRef must not be applied to it.</summary>
    [Fact]
    public void FileGcsFallback_NotAppliedToScaledModel()
    {
        var src = Corpus(ScaledGisFile); if (src == null) return;
        var f = DgnFile.Open(src);
        Assert.NotNull(f.GeoCoordinateSystem);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Exporters.Get("kml").Export(f, Path.Combine(OutDir, "iv-scaled.kml"), new ExportOptions()));
        Assert.Contains("outside its area of use", ex.Message);
        var rep = Exporters.Get("geojson").Export(f, Path.Combine(OutDir, "iv-scaled.geojson"), new ExportOptions());
        Assert.Contains(rep.Warnings, w => w.Contains("outside its area of use"));
    }

    [Fact]
    public void GcsKeys_MapKnownNames()
    {
        Assert.Equal(2927, GcsKeys.CsMapKeyToEpsg("EPSG:2927"));
        Assert.Equal(2926, GcsKeys.CsMapKeyToEpsg("HARN.WA-NF"));
        Assert.Equal(2286, GcsKeys.CsMapKeyToEpsg("wa83-sf"));
        Assert.Null(GcsKeys.CsMapKeyToEpsg("NOT-A-KEY"));
        Assert.Equal(2927, GcsKeys.EsriNameToEpsg("NAD_1983_HARN_StatePlane_Washington_South_FIPS_4602_Feet"));
        Assert.Equal(32104, GcsKeys.EsriNameToEpsg("NAD_1983_StatePlane_Nebraska_FIPS_2600_Meters"));
        Assert.Equal(2278, GcsKeys.EsriNameToEpsg("NAD_1983_StatePlane_Texas_South_Central_FIPS_4204_Feet"));
        Assert.Null(GcsKeys.EsriNameToEpsg("WGS_1984_Web_Mercator_Auxiliary_Sphere"));
    }

    public static IEnumerable<object[]> GcsOracleFiles() =>
        Directory.Exists(Path.Combine(TestPaths.Oracle, "gcs"))
            ? Directory.EnumerateFiles(Path.Combine(TestPaths.Oracle, "gcs"), "*.gcs.oracle.json").OrderBy(x => x).Select(x => new object[] { Path.GetFileName(x) })
            : new[] { new object[] { "" } };

    /// <summary>
    /// Ground truth from MicroStation (tools/oracle/MstnOracle --gcs-only): every model GCS MicroStation reports is
    /// decoded with the same name/units/false origin, and DgnKit's projection reproduces MicroStation's
    /// LatLongFromMasterUnits for the sample points (both in the GCS datum; NAD83 ≈ WGS84 without a datum shift).
    /// </summary>
    [Theory]
    [MemberData(nameof(GcsOracleFiles))]
    public void Gcs_MatchesOracle(string oracleName)
    {
        if (oracleName.Length == 0) return;
        var oraclePath = Path.Combine(TestPaths.Oracle, "gcs", oracleName);
        string baseName = oracleName.Replace(".gcs.oracle.json", "", StringComparison.OrdinalIgnoreCase);
        var dgn = Directory.Exists(TestPaths.Corpus)
            ? Directory.EnumerateFiles(TestPaths.Corpus, "*.dgn", SearchOption.AllDirectories).FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), baseName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (dgn == null) return;
        var f = DgnFile.Open(dgn);
        using var o = JsonDocument.Parse(File.ReadAllText(oraclePath));
        foreach (var om in o.RootElement.GetProperty("models").EnumerateArray())
        {
            string name = om.GetProperty("name").GetString()!;
            var m = f.FindModel(name);
            Assert.NotNull(m);
            var og = om.GetProperty("gcs");
            if (og.ValueKind == JsonValueKind.Null) { Assert.Null(m!.GeoCoordinateSystem); continue; }
            var g = m!.GeoCoordinateSystem;
            Assert.NotNull(g);
            _out.WriteLine($"{baseName}/{name}: oracle {og.GetProperty("name").GetString()} '{og.GetProperty("description").GetString()}' vs {g}");
            Assert.Equal(og.GetProperty("name").GetString(), g!.Name);
            Assert.Equal(og.GetProperty("description").GetString(), g.Description);
            if (og.TryGetProperty("falseEasting", out var fe) && fe.ValueKind == JsonValueKind.Number) Assert.Equal(fe.GetDouble(), g.FalseEasting!.Value, 3);
            if (og.TryGetProperty("falseNorthing", out var fn) && fn.ValueKind == JsonValueKind.Number) Assert.Equal(fn.GetDouble(), g.FalseNorthing!.Value, 3);
            if (og.TryGetProperty("equatorialRadius", out var er) && er.ValueKind == JsonValueKind.Number) Assert.Equal(er.GetDouble(), g.SemiMajorAxis!.Value, 3);

            var pr = GeoProjector.FromGcs(g, m.Units.MetersPerMaster);
            foreach (var s in og.GetProperty("samples").EnumerateArray())
            {
                if (s.GetProperty("lon").ValueKind != JsonValueKind.Number) continue;
                var mp = s.GetProperty("master");
                var p = new DPoint3d(mp[0].GetDouble(), mp[1].GetDouble(), mp[2].GetDouble());
                var r = pr.ToWgs84(p);
                _out.WriteLine($"   {p}: oracle {s.GetProperty("lon").GetDouble():F9},{s.GetProperty("lat").GetDouble():F9}  dgnkit {r.Lon:F9},{r.Lat:F9}");
                Near(s.GetProperty("lon").GetDouble(), r.Lon, 1e-7);   // ≈ 1 cm
                Near(s.GetProperty("lat").GetDouble(), r.Lat, 1e-7);
            }
        }
    }
}
