using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ACadSharp.IO;
using DgnKit.Export;
using DgnKit.Model;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace DgnKit.Tests;

/// <summary>Round-trip checks for every export format (requires the downloaded corpus; skipped otherwise).</summary>
public class ExporterTests
{
    private readonly ITestOutputHelper _out;
    public ExporterTests(ITestOutputHelper o) { _out = o; }

    private static string? Corpus(string rel)
    {
        var p = Path.Combine(TestPaths.Corpus, rel);
        return File.Exists(p) ? p : null;
    }

    private static string OutDir => Directory.CreateDirectory(Path.Combine(TestPaths.Reports, "exports")).FullName;

    public static IEnumerable<object[]> SampleFiles() => new[]
    {
        new object[] { "txdot-tcp-1-4-26/tcp(1-4)-26.dgn" },          // MicroStation 2025 sheet
        new object[] { "txdot-mb96/MB96.DGN" },                       // V7
        new object[] { "njdot-traffic/Traffic Construction Details DGN file set__104_TCD-2ConstructionBarrier.dgn" }, // V8 2004
        new object[] { "wsdot-XE3736/07_XL7089_PS_RS2.dgn" },        // ORD sheet
        new object[] { "gdal-dgnv8/test_dgnv8.dgn" },                 // ODA-written V8
    };

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Dxf_RoundTrips(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var f = DgnFile.Open(src);
        var outPath = Path.Combine(OutDir, Path.GetFileNameWithoutExtension(src) + ".dxf");
        var rep = Exporters.Get("dxf").Export(f, outPath, new ExportOptions());
        _out.WriteLine(rep.ToString());
        using var reader = new DxfReader(outPath);
        var doc = reader.Read();
        Assert.True(doc.Entities.Count > 0, "no entities");
        Assert.Equal(rep.Written, doc.Entities.Count);
        var usedLevels = f.PrimaryModel!.AllElements().Where(e => e.IsGraphic && e.LevelId != 0).Select(e => f.Levels.NameOf(e.LevelId)).ToHashSet();
        Assert.Contains(doc.Layers, l => usedLevels.Contains(l.Name));
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Dwg_RoundTrips(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var f = DgnFile.Open(src);
        var outPath = Path.Combine(OutDir, Path.GetFileNameWithoutExtension(src) + ".dwg");
        var rep = Exporters.Get("dwg").Export(f, outPath, new ExportOptions());
        _out.WriteLine(rep.ToString());
        using var reader = new DwgReader(outPath);
        var doc = reader.Read();
        Assert.True(doc.Entities.Count > 0, "no entities read back from DWG");
        Assert.Equal(rep.Written, doc.Entities.Count);
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Pdf_HasPageAndText(string rel)
    {
        var src = Corpus(rel); if (src == null) return;
        var f = DgnFile.Open(src);
        var outPath = Path.Combine(OutDir, Path.GetFileNameWithoutExtension(src) + ".pdf");
        var rep = Exporters.Get("pdf").Export(f, outPath, new ExportOptions());
        using var pdf = PdfDocument.Open(outPath);
        Assert.Equal(1, pdf.NumberOfPages);
        int textPrims = rep.ByKind.TryGetValue("Text", out var n) ? n : 0;
        if (textPrims > 0) Assert.NotEmpty(pdf.GetPage(1).GetWords());
    }

    [Fact]
    public void GeoJson_LocalAndProjected()
    {
        var src = Corpus("wsdot-XE3691/XL7087_PS_VM.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        // design model with GIS features in WA State Plane South (ftUS, NAD83 HARN) — verified from coordinate magnitudes
        var model = f.Models.OrderByDescending(m => m.Elements.Count).First();
        var local = Path.Combine(OutDir, "vm-local.geojson");
        Exporters.Get("geojson").Export(f, local, new ExportOptions { ModelName = model.Name, Crs = "local" });
        using (var doc = JsonDocument.Parse(File.ReadAllText(local)))
            Assert.True(doc.RootElement.GetProperty("features").GetArrayLength() > 0);

        var geo = Path.Combine(OutDir, "vm-wgs84.geojson");
        var rep = Exporters.Get("geojson").Export(f, geo, new ExportOptions { ModelName = model.Name, Crs = "EPSG:2927" });
        _out.WriteLine(rep.ToString());
        using var g = JsonDocument.Parse(File.ReadAllText(geo));
        int inWa = 0, total = 0;
        foreach (var feat in g.RootElement.GetProperty("features").EnumerateArray())
        {
            var geom = feat.GetProperty("geometry");
            if (geom.GetProperty("type").GetString() != "Point") continue;
            var c = geom.GetProperty("coordinates");
            double lon = c[0].GetDouble(), lat = c[1].GetDouble();
            total++;
            if (lat > 45.5 && lat < 49.1 && lon > -124.9 && lon < -116.8) inWa++;
        }
        _out.WriteLine($"{inWa}/{total} points inside Washington");
        Assert.True(total > 0);
        Assert.True(inWa >= total * 0.9, $"only {inWa}/{total} projected points fall inside Washington State");
        // metadata survives
        Assert.Contains(g.RootElement.GetProperty("features").EnumerateArray(), x => x.GetProperty("properties").EnumerateObject().Any(p => p.Name.Contains('.')));
    }

    [Fact]
    public void Kml_And_Kmz()
    {
        var src = Corpus("wsdot-XE3691/XL7087_PS_VM.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        var model = f.Models.OrderByDescending(m => m.Elements.Count).First();
        var kml = Path.Combine(OutDir, "vm.kml");
        Exporters.Get("kml").Export(f, kml, new ExportOptions { ModelName = model.Name, Crs = "EPSG:2927" });
        var x = XDocument.Load(kml);
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var placemarks = x.Descendants(k + "Placemark").ToList();
        Assert.NotEmpty(placemarks);
        Assert.NotEmpty(x.Descendants(k + "Folder"));
        var firstCoord = x.Descendants(k + "coordinates").First().Value.Trim().Split(' ')[0].Split(',');
        double lon = double.Parse(firstCoord[0], System.Globalization.CultureInfo.InvariantCulture), lat = double.Parse(firstCoord[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(lat, 45.5, 49.1);
        Assert.InRange(lon, -124.9, -116.8);

        var kmz = Path.Combine(OutDir, "vm.kmz");
        Exporters.Get("kmz").Export(f, kmz, new ExportOptions { ModelName = model.Name, Crs = "EPSG:2927" });
        using var zip = ZipFile.OpenRead(kmz);
        Assert.Contains(zip.Entries, e => e.FullName == "doc.kml");
    }

    [Fact]
    public void Kml_RequiresCrs()
    {
        var src = Corpus("txdot-tcp-1-4-26/tcp(1-4)-26.dgn"); if (src == null) return;
        var f = DgnFile.Open(src);
        Assert.Throws<InvalidOperationException>(() => Exporters.Get("kml").Export(f, Path.Combine(OutDir, "x.kml"), new ExportOptions()));
    }
}
