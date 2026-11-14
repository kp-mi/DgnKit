using DgnKit.Geometry;

namespace DgnKit.IModel.Imx;

// The iModel interchange ("IMX"): newline-delimited JSON, one record per line, written in dependency order
// (header → fonts → line styles → categories → models → parts → elements). Every record that maps to iModel content
// carries a stable source identity (Source) used for ExternalSourceAspect provenance and change detection, and a
// checksum of its own content. The format is BIS-shaped but source-agnostic: a DWG, IFC, LandXML or GIS reader emits
// the same records and the same TypeScript loader turns them into an iModel. Spec: docs/IMODEL-INTERCHANGE.md.

/// <summary>Stable identity of a record in its source repository (→ bis:ExternalSourceAspect Kind/Identifier).</summary>
public sealed record ImxSource(string Kind, string Id)
{
    /// <summary>Source-specific extras kept with the provenance (element type, class, graphic group …).</summary>
    public Dictionary<string, object?>? Info { get; init; }
}

public sealed class ImxHeader
{
    public const string FormatName = "dgnkit-imx";
    public const int FormatVersion = 1;
    public string Generator { get; init; } = "DgnKit";
    /// <summary>Absolute path of the source document (→ bis:RepositoryLink.Url).</summary>
    public string SourcePath { get; init; } = "";
    public string SourceName { get; init; } = "";
    /// <summary>"DGN V8", "DGN V7", "DWG", "IFC" …</summary>
    public string SourceFormat { get; init; } = "";
    public string? SourceFormatVersion { get; init; }
    public long SourceSize { get; init; }
    public DateTime? SourceModifiedUtc { get; init; }
    /// <summary>SHA-256 of the source bytes (document-level change detection).</summary>
    public string? SourceSha256 { get; init; }
    /// <summary>Union of all spatial geometry, metres.</summary>
    public Range3d SpatialExtents { get; init; } = Range3d.Empty;
    public ImxGcs? Gcs { get; init; }
}

/// <summary>Geographic coordinate system of the spatial coordinates (which are always metres in the iModel).</summary>
public sealed class ImxGcs
{
    public int? Epsg { get; init; }
    /// <summary>CS-MAP key name (MicroStation/iTwin GCS library), e.g. "HARN.WA-NF".</summary>
    public string? CsMapKey { get; init; }
    public string? Wkt { get; init; }
    public string? Description { get; init; }
    /// <summary>Length of one CRS linear unit in metres (US survey foot = 1200/3937).</summary>
    public double? UnitMeters { get; init; }
    public string? Source { get; init; }
}

public sealed class ImxFont
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"truetype", "rsc" or "shx".</summary>
    public string Type { get; init; } = "truetype";
}

/// <summary>
/// A line style. Cosmetic styles map to iTwin.js LinePixels (<see cref="LinePixels"/> 0..7); custom stroke patterns carry
/// their dashes in metres (at style scale 1).
/// </summary>
public sealed class ImxLineStyle
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Cosmetic pixel pattern 1..7 (MicroStation built-in styles = iTwin.js LinePixels.Code1..Code7).</summary>
    public int? LinePixels { get; init; }
    /// <summary>Custom stroke pattern: alternating dash/gap lengths in metres (first is a dash).</summary>
    public List<ImxStroke>? Strokes { get; init; }
    public double Phase { get; init; }
    /// <summary>True when the style scales with annotation scale (non-physical).</summary>
    public bool Annotation { get; init; }
    /// <summary>
    /// Why this record only approximates the source style (e.g. point symbols not converted, definition external), or
    /// null when it is exact. Loaders report it as-is.
    /// </summary>
    public string? Approximation { get; init; }
    public ImxSource Source { get; init; } = new("LineStyle", "");
}

public sealed record ImxStroke(double Length, bool Dash, double Width = 0);

/// <summary>Appearance of a (sub-)category.</summary>
public sealed class ImxAppearance
{
    public uint Color { get; init; }
    public int Weight { get; init; }
    public string? Style { get; init; }
    public int Priority { get; init; }
    public double Transparency { get; init; }
    public bool Invisible { get; init; }
    public bool DontPlot { get; init; }
    public bool DontLocate { get; init; }
}

/// <summary>A category (DGN level / DWG layer). The loader creates a SpatialCategory and/or DrawingCategory per <see cref="Usage"/>.</summary>
public sealed class ImxCategory
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ImxAppearance Appearance { get; init; } = new();
    /// <summary>"spatial" and/or "drawing".</summary>
    public SortedSet<string> Usage { get; } = new(StringComparer.Ordinal);
    public ImxSource Source { get; init; } = new("Level", "");
}

/// <summary>Sub-category (used when one source element spans several layers that must stay one element).</summary>
public sealed class ImxSubCategory
{
    public string Key { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public ImxAppearance Appearance { get; init; } = new();
}

/// <summary>
/// A source model. <see cref="Type"/>: "physical" (3D design → bis:PhysicalModel), "drawing" (2D design →
/// bis:Drawing + bis:DrawingModel), "sheet" (→ bis:Sheet + bis:SheetModel).
/// </summary>
public sealed class ImxModel
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Type { get; init; } = "physical";
    public bool Is3d { get; init; }
    public string? MasterUnit { get; init; }
    public double MetersPerMaster { get; init; } = 1;
    /// <summary>Extents of the model's geometry, metres.</summary>
    public Range3d Extents { get; init; } = Range3d.Empty;
    /// <summary>Sheet size in metres, when known.</summary>
    public (double Width, double Height)? SheetSize { get; init; }
    public ImxSource Source { get; init; } = new("Model", "");
}

/// <summary>Reusable geometry (→ bis:GeometryPart), geometry in part-local metres.</summary>
public sealed class ImxPart
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public GeometryStream Geometry { get; init; } = new(true);
    public ImxSource Source { get; init; } = new("SharedCellDefinition", "");
}

/// <summary>A named group of business properties (EC instance, tag set, item type, string/database linkage …).</summary>
public sealed class ImxPropertyGroup
{
    /// <summary>"EC", "Tag", "ItemType", "StringLinkage", "Database", "Civil" …</summary>
    public string Source { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Schema { get; init; }
    public string? ClassName { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = new();
}

/// <summary>
/// A geometric element. <see cref="Role"/> lets the loader pick the BIS class per model type: "geometry" (physical model →
/// generic:PhysicalObject, drawing/sheet → bis:DrawingGraphic) or "annotation" (physical model → generic:Graphic3d).
/// </summary>
public sealed class ImxElement
{
    public string Key { get; init; } = "";
    public string Model { get; init; } = "";
    public string Category { get; init; } = "";
    public string Role { get; init; } = "geometry";
    /// <summary>Key of the parent element (assemblies: a source element split over several categories).</summary>
    public string? Parent { get; init; }
    public string? UserLabel { get; init; }
    /// <summary>Placement origin, world metres; geometry is written relative to it.</summary>
    public DPoint3d Origin { get; init; }
    public GeometryStream Geometry { get; init; } = new(true);
    public List<ImxPropertyGroup> Properties { get; } = new();
    public ImxSource Source { get; init; } = new("Element", "");
    /// <summary>Source modification time, if the source tracks it (→ ExternalSourceAspect.Version).</summary>
    public DateTime? SourceModifiedUtc { get; init; }
}
