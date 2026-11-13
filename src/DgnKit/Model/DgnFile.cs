using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;

namespace DgnKit.Model
{
    public enum DgnFormat { Unknown, V7, V8 }

    public sealed class DgnReadOptions
    {
        /// <summary>Keep each element's raw bytes (useful for diagnostics / reverse engineering).</summary>
        public bool KeepRawBytes { get; set; }
        /// <summary>Decode XAttributes (EC instances, civil feature data). Disable for faster geometry-only reads.</summary>
        public bool ReadXAttributes { get; set; } = true;
        /// <summary>Include elements flagged deleted.</summary>
        public bool IncludeDeleted { get; set; }
        /// <summary>Load reference attachments recursively (looks next to the master file and in <see cref="ReferenceSearchPaths"/>).</summary>
        public bool LoadReferences { get; set; }
        public List<string> ReferenceSearchPaths { get; } = new();
        public int MaxReferenceDepth { get; set; } = 4;
    }

    /// <summary>A parsed DGN file: models, symbology tables and file-level dictionaries.</summary>
    public sealed class DgnFile
    {
        public string? Path { get; internal set; }
        public DgnFormat Format { get; internal set; }
        public int MajorVersion { get; internal set; }
        public int MinorVersion { get; internal set; }

        public List<DgnModel> Models { get; } = new();
        public LevelTable Levels { get; } = new();
        public LineStyleTable LineStyles { get; } = new();
        public FontTable Fonts { get; } = new();
        public ColorTable Colors { get; internal set; } = ColorTable.CreateDefault();
        /// <summary>Extended (true/book) colours: element colour 256 + i ↦ ExtendedColors[i] (from the TCB XAttribute).</summary>
        public List<RgbColor> ExtendedColors { get; } = new();
        public List<TextStyle> TextStyles { get; } = new();
        public List<NamedEntry> DimensionStyles { get; } = new();
        public List<NamedEntry> MultilineStyles { get; } = new();

        /// <summary>Shared cell definitions keyed by element id.</summary>
        public Dictionary<ulong, SharedCellDefinition> SharedCellDefinitions { get; } = new();
        /// <summary>Tag set definitions (type 39 DgnStore) by name.</summary>
        public Dictionary<string, TagSetDefinition> TagSets { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tag set definitions keyed by their DgnStore element id.</summary>
        public Dictionary<ulong, TagSetDefinition> TagSetsById { get; } = new();
        /// <summary>File-level (non-model) elements (tables, styles…), mostly for diagnostics.</summary>
        public List<Element> NonModelElements { get; } = new();
        /// <summary>
        /// Geographic coordinate system, when one could be decoded from the file: the default model's GCS, else the first
        /// model GCS found, else the spatial reference of imported GIS feature definitions (see <see cref="GeoCoordinateInfo.Source"/>).
        /// Per-model systems are in <see cref="DgnModel.GeoCoordinateSystem"/>.
        /// </summary>
        public GeoCoordinateInfo? GeoCoordinateSystem { get; internal set; }

        public List<string> Diagnostics { get; } = new();
        public Dictionary<int, int> ElementTypeCounts { get; } = new();

        /// <summary>Default (first design) model.</summary>
        public DgnModel? DefaultModel => Models.FirstOrDefault(m => m.Type == DgnModelType.Normal) ?? Models.FirstOrDefault();

        /// <summary>
        /// The model most likely intended for export: the default model when it has graphics, otherwise the
        /// non-definition model with the most graphic elements (e.g. the sheet model of a sheet file).
        /// </summary>
        public DgnModel? PrimaryModel
        {
            get
            {
                var d = DefaultModel;
                if (d != null && d.Elements.Any(e => e.IsGraphic && !e.IsInvisible)) return d;
                return Models.Where(m => !m.IsDictionary).OrderByDescending(m => m.Elements.Count(e => e.IsGraphic)).FirstOrDefault() ?? d;
            }
        }

        public DgnModel? FindModel(string name) => Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Opens a DGN file, auto-detecting V7 (ISFF) vs V8 (compound file).</summary>
        public static DgnFile Open(string path, DgnReadOptions? options = null)
        {
            var bytes = File.ReadAllBytes(path);
            var f = Read(bytes, options);
            f.Path = System.IO.Path.GetFullPath(path);
            if (options?.LoadReferences == true) ReferenceLoader.LoadAll(f, options, 0);
            return f;
        }

        public static DgnFile Read(byte[] bytes, DgnReadOptions? options = null)
        {
            options ??= new DgnReadOptions();
            var fmt = Detect(bytes);
            return fmt switch
            {
                DgnFormat.V8 => Formats.V8.V8Reader.Read(bytes, options),
                DgnFormat.V7 => Formats.V7.V7Reader.Read(bytes, options),
                _ => throw new InvalidDataException("Unrecognized DGN format (neither V7 ISFF nor V8 compound file)."),
            };
        }

        public static DgnFormat Detect(byte[] head)
        {
            if (Cfb.CompoundFile.IsCompoundFile(head)) return DgnFormat.V8;
            // V7: first element is TCB (type 9, level 8): 0x08 0x09 (level/complex byte, type byte) then words-to-follow.
            if (head.Length >= 4 && (head[1] & 0x7F) == 9 && (head[0] & 0x3F) == 8) return DgnFormat.V7;
            // V7 cell libraries begin with a type 1 (cell library header) element.
            if (head.Length >= 4 && (head[1] & 0x7F) == 1 && head[0] == 0x08) return DgnFormat.V7;
            return DgnFormat.Unknown;
        }

        internal void Warn(string msg) { if (Diagnostics.Count < 5000) Diagnostics.Add(msg); }
    }

    public enum DgnModelType { Normal = 0, Sheet = 1, Schematic = 2, Drawing = 3, Extraction = 4, Redline = 5, Unknown = -1 }

    public sealed class DgnModel
    {
        public int Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public string? Description { get; internal set; }
        public DgnModelType Type { get; internal set; }
        public bool Is3d { get; internal set; }
        public bool IsDictionary { get; internal set; }
        public UnitInfo Units { get; internal set; } = UnitInfo.Default;
        /// <summary>Global origin in UORs (subtracted before scaling to master units).</summary>
        public DPoint3d GlobalOrigin { get; internal set; }
        /// <summary>Annotation scale factor for annotation elements (1 = none; V8: model info linkage 0x56EE).</summary>
        public double AnnotationScale { get; internal set; } = 1.0;
        /// <summary>
        /// Model line style scale applied to non-physical custom line styles (1 = none). V8 files store MicroStation's
        /// "annotation scale" mode, where it equals <see cref="AnnotationScale"/> (⚠️ a separate global scale is not decoded).
        /// </summary>
        public double LineStyleScale { get; internal set; } = 1.0;
        /// <summary>The model's own geographic coordinate system (MicroStation stores one per model), if any.</summary>
        public GeoCoordinateInfo? GeoCoordinateSystem { get; internal set; }

        /// <summary>Top-level graphic elements in file order (complex components are in <see cref="Element.Children"/>).</summary>
        public List<Element> Elements { get; } = new();
        /// <summary>Top-level control (non-graphic) elements.</summary>
        public List<Element> ControlElements { get; } = new();
        public List<ReferenceAttachment> References { get; } = new();
        public DgnFile File { get; internal set; } = null!;
        /// <summary>Raw/diagnostic header values (reverse-engineering aids).</summary>
        public Dictionary<string, object?> Properties { get; } = new();

        /// <summary>Converts a point in UORs to master units (applies global origin).</summary>
        public DPoint3d ToMaster(DPoint3d uor) => (uor - GlobalOrigin) / Units.UorPerMaster;
        public double ToMaster(double uorDistance) => uorDistance / Units.UorPerMaster;

        public IEnumerable<Element> AllElements() => Elements.SelectMany(e => e.SelfAndDescendants());

        public override string ToString() => $"Model {Id} '{Name}' ({Type}, {(Is3d ? "3D" : "2D")}, {Elements.Count} elements)";
    }

    public enum UnitBase { None = 0, Meter = 1, Degree = 2 }
    public enum UnitSystem { Undefined = 0, English = 1, Metric = 2, USSurvey = 3 }

    /// <summary>A unit definition: (numerator/denominator) units per base unit (meter).</summary>
    public readonly struct UnitDefinition
    {
        public readonly uint Flags;
        public readonly double Numerator, Denominator;
        public readonly string Label;
        public UnitDefinition(uint flags, double num, double den, string label) { Flags = flags; Numerator = num; Denominator = den; Label = label; }
        /// <summary>Length of one unit in meters (den/num).</summary>
        public double MetersPerUnit => Numerator != 0 ? Denominator / Numerator : 1.0;
        public UnitBase Base => (UnitBase)(Flags & 0x7);
        public UnitSystem System => (UnitSystem)((Flags >> 3) & 0x7);
        public override string ToString() => $"{Label} ({MetersPerUnit:G6} m)";
    }

    public sealed class UnitInfo
    {
        public UnitDefinition Master { get; internal set; }
        public UnitDefinition Sub { get; internal set; }
        public UnitDefinition Storage { get; internal set; }
        public double UorPerStorage { get; internal set; } = 1.0;

        /// <summary>UORs per master unit.</summary>
        public double UorPerMaster => UorPerStorage * Storage.MetersPerUnit > 0 && Master.MetersPerUnit > 0
            ? UorPerStorage * (Master.MetersPerUnit / Storage.MetersPerUnit) : UorPerStorage;
        public double SubPerMaster => Sub.MetersPerUnit > 0 ? Master.MetersPerUnit / Sub.MetersPerUnit : 1;
        public double MetersPerMaster => Master.MetersPerUnit;

        public static UnitInfo Default => new()
        {
            Master = new UnitDefinition(0x11, 1, 1, "m"),
            Sub = new UnitDefinition(0x11, 1000, 1, "mm"),
            Storage = new UnitDefinition(0x11, 1, 1, "m"),
            UorPerStorage = 10000,
        };

        public override string ToString() => $"master={Master} sub={Sub} storage={Storage} uor/storage={UorPerStorage} uor/master={UorPerMaster}";
    }

    /// <summary>
    /// A geographic coordinate system as stored in a DGN file. <see cref="Name"/> is MicroStation's GCS key name
    /// (CS-MAP library key such as "HARN.WA-NF", or "EPSG:2927"); <see cref="Epsg"/>/<see cref="Wkt"/> are filled when derivable.
    /// </summary>
    public sealed class GeoCoordinateInfo
    {
        /// <summary>GCS key name ("EPSG:2927", "HARN.WA-NF") or, for an ESRI WKT source, the PROJCS/GEOGCS name.</summary>
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int? Epsg { get; set; }
        /// <summary>OGC/ESRI WKT: as stored, or built from the stored projection parameters.</summary>
        public string? Wkt { get; set; }
        /// <summary>Where the definition came from (element/store id and model).</summary>
        public string Source { get; set; } = "";

        /// <summary>CS-MAP group ("EPSGPRJ", "SPCSHPF" …).</summary>
        public string? Group { get; set; }
        /// <summary>CS-MAP projection key ("LM" Lambert conformal conic 2SP, "TM", "LL" …) and its description.</summary>
        public string? ProjectionKey { get; set; }
        public string? ProjectionName { get; set; }
        public string? DatumKey { get; set; }
        public string? DatumName { get; set; }
        public string? EllipsoidName { get; set; }
        /// <summary>Source/authority text of the definition (e.g. "EPSG V6 […]").</summary>
        public string? SourceReference { get; set; }
        /// <summary>CS linear unit name ("Foot" = US survey foot, "Meter", "IFoot") and its length in metres.</summary>
        public string? Units { get; set; }
        public double? UnitMeters { get; set; }
        public double? SemiMajorAxis { get; set; }
        public double? SemiMinorAxis { get; set; }
        public double? CentralMeridian { get; set; }
        public double? OriginLatitude { get; set; }
        public double? StandardParallel1 { get; set; }
        public double? StandardParallel2 { get; set; }
        public double? ScaleReduction { get; set; }
        /// <summary>False origin in CS units.</summary>
        public double? FalseEasting { get; set; }
        public double? FalseNorthing { get; set; }
        /// <summary>Useful range in degrees, when stored.</summary>
        public double? MinLongitude { get; set; }
        public double? MinLatitude { get; set; }
        public double? MaxLongitude { get; set; }
        public double? MaxLatitude { get; set; }

        public override string ToString() => $"{Name}{(Epsg.HasValue ? " (EPSG:" + Epsg + ")" : "")}{(Description != null ? " " + Description : "")}";
    }

    public sealed class ReferenceAttachment
    {
        public ulong ElementId { get; internal set; }
        public string? FileName { get; internal set; }
        public string? FullPath { get; internal set; }
        public string? ModelName { get; internal set; }
        public string? LogicalName { get; internal set; }
        public string? Description { get; internal set; }
        /// <summary>Origin in the referenced model (UORs of that model).</summary>
        public DPoint3d ReferenceOrigin { get; internal set; }
        /// <summary>Origin in the master (UORs of master model).</summary>
        public DPoint3d MasterOrigin { get; internal set; }
        public Matrix3 Rotation { get; internal set; } = Matrix3.Identity;
        /// <summary>Scale (master UOR per reference UOR, including unit conversion).</summary>
        public double Scale { get; internal set; } = 1;
        public bool Displayed { get; internal set; } = true;
        public List<DPoint3d> ClipPolygon { get; } = new();
        public int NestDepth { get; internal set; }
        public uint FileNumber { get; internal set; }
        public DgnFile? Loaded { get; internal set; }
        public DgnModel? LoadedModel { get; internal set; }

        /// <summary>Transform from reference-model UORs to master-model UORs.</summary>
        public Transform ToMasterUors
        {
            get
            {
                var m = Rotation * Matrix3.Scale(Scale);
                return new Transform(m, MasterOrigin - m.Multiply(ReferenceOrigin));
            }
        }

        public override string ToString() => $"Ref '{FileName}' model='{ModelName}' logical='{LogicalName}' scale={Scale}";
    }

    public sealed class NamedEntry
    {
        public ulong ElementId { get; internal set; }
        public uint Number { get; internal set; }
        public string Name { get; internal set; } = "";
        public string? Description { get; internal set; }
        public override string ToString() => $"{Number}: {Name}";
    }

    public sealed class TextStyle
    {
        public ulong ElementId { get; internal set; }
        public uint Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public override string ToString() => Name;
    }

    public sealed class FontTable
    {
        public Dictionary<uint, FontInfo> ById { get; } = new();
        public string NameOf(uint id) => ById.TryGetValue(id, out var f) ? f.Name : (id == 0 ? "STANDARD" : "Font" + id);
    }

    public enum FontKind { Unknown = 0, Rsc = 1, TrueType = 2, Shx = 3 }

    public sealed class FontInfo
    {
        public uint Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public FontKind Kind { get; internal set; }
        public override string ToString() => $"{Id}: {Name} ({Kind})";
    }

    public sealed class TagSetDefinition
    {
        public string Name { get; internal set; } = "";
        public ulong ElementId { get; internal set; }
        public List<TagDefinition> Tags { get; } = new();
    }

    public sealed class TagDefinition
    {
        public int Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public string? Prompt { get; internal set; }
        public int Type { get; internal set; }
        public object? DefaultValue { get; internal set; }
    }
}
