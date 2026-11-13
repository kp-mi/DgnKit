using DgnKit.Graphics;
using DgnKit.Model;

namespace DgnKit.Export;

public sealed class ExportOptions
{
    /// <summary>Model to export (default: first design model).</summary>
    public string? ModelName { get; set; }
    /// <summary>
    /// Source CRS for geographic outputs (KML/KMZ/GeoJSON): "EPSG:2927", a CS-MAP key, a WKT string or a path to a .prj/.wkt
    /// file. When null, the GCS stored in the DGN model/file is used; "local" keeps master-unit coordinates (GeoJSON only).
    /// </summary>
    public string? Crs { get; set; }
    public bool IncludeReferences { get; set; }
    /// <summary>CAD exports: explode cells instead of writing blocks.</summary>
    public bool ExplodeCells { get; set; } = true;
    public bool IncludeConstruction { get; set; } = true;
    public bool IncludeInvisible { get; set; }
    /// <summary>PDF paper: "A1", "ANSI-D", "22x34in", "841x594mm" or null = fit to drawing extents at 1:1 mm.</summary>
    public string? Paper { get; set; }
    /// <summary>PDF: draw white (color 0 on dark background) as black, as MicroStation printing does by default.</summary>
    public bool PrintWhiteAsBlack { get; set; } = true;
    /// <summary>PDF: fit the page to every element (true) or to the main drawing, ignoring far-away stray clusters (false).</summary>
    public bool FitAllGeometry { get; set; }
    /// <summary>DWG/DXF target version.</summary>
    public string CadVersion { get; set; } = "AC1032";
    public double ChordTolerance { get; set; }

    public DisplayListOptions ToDisplayListOptions() => new()
    {
        ModelName = ModelName, IncludeReferences = IncludeReferences, IncludeConstruction = IncludeConstruction,
        IncludeInvisible = IncludeInvisible, ChordTolerance = ChordTolerance,
    };
}

public sealed class ExportReport
{
    public int Primitives { get; set; }
    public int Written { get; set; }
    public int Skipped { get; set; }
    public Dictionary<string, int> ByKind { get; } = new();
    public List<string> Warnings { get; } = new();
    public override string ToString() =>
        $"{Written}/{Primitives} primitives written ({string.Join(", ", ByKind.Select(k => $"{k.Key}={k.Value}"))})" +
        (Warnings.Count > 0 ? $", {Warnings.Count} warning(s): {string.Join("; ", Warnings.Take(3))}" : "");
    internal void Count(string kind) { ByKind[kind] = ByKind.TryGetValue(kind, out var n) ? n + 1 : 1; Written++; }
}

public interface IExporter
{
    string Format { get; }
    ExportReport Export(DgnFile file, string outputPath, ExportOptions options);
}

public static class Exporters
{
    public static IReadOnlyList<string> Formats { get; } = new[] { "dxf", "dwg", "geojson", "kml", "kmz", "pdf", "json" };

    public static IExporter Get(string format) => format.ToLowerInvariant() switch
    {
        "dxf" => new CadExporter(binaryDxf: false, dwg: false),
        "dwg" => new CadExporter(binaryDxf: false, dwg: true),
        "geojson" or "json-geo" => new GeoJsonExporter(),
        "kml" => new KmlExporter(kmz: false),
        "kmz" or "kmxz" => new KmlExporter(kmz: true),
        "pdf" => new PdfExporter(),
        "json" => new JsonExporter(),
        _ => throw new ArgumentException($"Unknown export format '{format}'. Supported: {string.Join(", ", Formats)}"),
    };

    /// <summary>Convenience: open a DGN and export it in one call.</summary>
    public static ExportReport Convert(string dgnPath, string outputPath, string format, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var f = DgnFile.Open(dgnPath, new DgnReadOptions { LoadReferences = options.IncludeReferences });
        return Get(format).Export(f, outputPath, options);
    }
}

internal sealed class JsonExporter : IExporter
{
    public string Format => "json";
    public ExportReport Export(DgnFile file, string outputPath, ExportOptions options)
    {
        using var fs = File.Create(outputPath);
        JsonDump.Write(file, fs, new JsonDumpOptions { ModelName = options.ModelName, IncludePrimitives = true, DisplayOptions = options.ToDisplayListOptions() });
        return new ExportReport();
    }
}
