using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Geometry;
using DgnKit.Model;

namespace DgnKit.Elements
{
    /// <summary>MicroStation element type numbers (MSElementTypes).</summary>
    public enum ElementType
    {
        CellLibraryHeader = 1, CellHeader = 2, Line = 3, LineString = 4, GroupData = 5, Shape = 6, TextNode = 7,
        DigitizerSetup = 8, DesignFileHeader = 9, LevelSymbology = 10, Curve = 11, ComplexString = 12, Conic = 13,
        ComplexShape = 14, Ellipse = 15, Arc = 16, Text = 17, Surface = 18, Solid = 19, BSplinePole = 21, PointString = 22,
        Cone = 23, BSplineSurface = 24, BSplineSurfaceBoundary = 25, BSplineKnot = 26, BSplineCurve = 27, BSplineWeight = 28,
        Dimension = 33, SharedCellDefinition = 34, SharedCell = 35, Multiline = 36, Attribute = 37, DgnStoreComponent = 38,
        DgnStoreHeader = 39, Type44 = 44, MicroStationApplication = 66, RasterHeader = 87, RasterComponent = 88,
        RasterReference = 90, RasterReferenceComponent = 91, RasterHierarchy = 92, RasterHierarchyComponent = 93,
        RasterFrame = 94, TableEntry = 95, Table = 96, ViewGroup = 97, View = 98, LevelMask = 99, ReferenceAttachment = 100,
        MatrixHeader = 101, MatrixIntData = 102, MatrixDoubleData = 103, MeshHeader = 105, Extended = 106, ExtendedNonGraphic = 107,
        ReferenceOverride = 108, NamedGroupHeader = 110, NamedGroupComponent = 111,
    }

    public enum ElementClass { Primary = 0, PatternComponent = 1, Construction = 2, Dimension = 3, PrimaryRule = 4, LinearPatterned = 5, ConstructionRule = 6 }

    /// <summary>Special symbology values.</summary>
    public static class Symbology
    {
        public const uint ColorByLevel = 0xFFFFFFFF;
        public const uint ColorByCell = 0xFFFFFFFE;
        public const uint WeightByLevel = 0xFFFFFFFF;
        public const uint WeightByCell = 0xFFFFFFFE;
        public const int StyleByLevel = 0x7FFFFFFF;
        public const int StyleByCell = 0x7FFFFFFE;
        /// <summary>V7 ByLevel/ByCell are not encoded in element symbology.</summary>
    }

    /// <summary>Base class of all decoded DGN elements (mirrors MicroStation's MSElement header + display header).</summary>
    public class Element
    {
        public int RawType { get; internal set; }
        public ElementType Type => (ElementType)RawType;
        public ulong Id { get; internal set; }
        public uint LevelId { get; internal set; }
        public bool IsGraphic { get; internal set; }
        public bool Is3d { get; internal set; }
        public bool IsComplexHeader { get; internal set; }
        public bool IsComponent { get; internal set; }
        public bool IsDeleted { get; internal set; }
        public bool IsNonModel { get; internal set; }
        public bool IsLocked { get; internal set; }
        public bool IsInvisible { get; internal set; }
        public bool IsSnappable { get; internal set; } = true;
        public bool IsPlanar { get; internal set; }
        public bool IsHole { get; internal set; }
        public ElementClass Class { get; internal set; }
        public int Priority { get; internal set; }
        public uint GraphicGroup { get; internal set; }
        public uint Color { get; internal set; }
        public uint Weight { get; internal set; }
        public int Style { get; internal set; }
        /// <summary>Last modification time (UTC) if stored.</summary>
        public DateTime? LastModified { get; internal set; }

        /// <summary>Fill (from shape fill linkage / area fill XAttribute).</summary>
        public FillInfo? Fill { get; internal set; }
        /// <summary>Line style modifiers (scale, width…), from the style linkage.</summary>
        public LineStyleParams? StyleParams { get; internal set; }

        public List<Linkage> Linkages { get; } = new();
        public List<XAttribute> XAttributes { get; } = new();
        /// <summary>Decoded business/feature metadata: tags, string linkages, EC instance properties, civil feature data, DB links.</summary>
        public List<PropertyGroup> PropertyGroups { get; } = new();

        /// <summary>Raw/diagnostic decoded values (not business metadata) — useful for reverse engineering.</summary>
        public Dictionary<string, object?> Extra => _extra ??= new Dictionary<string, object?>();
        private Dictionary<string, object?>? _extra;
        public bool HasExtra => _extra != null && _extra.Count > 0;

        public List<Element> Children { get; } = new();
        public Element? Parent { get; internal set; }
        public DgnModel? Model { get; internal set; }
        public byte[]? RawBytes { get; internal set; }
        /// <summary>Element range in UORs as stored in the display header (may be stale for some handlers).</summary>
        public Range3d StoredRange { get; internal set; } = Range3d.Empty;

        public bool IsByLevelColor => Color == Symbology.ColorByLevel;
        public bool IsByLevelWeight => Weight == Symbology.WeightByLevel;
        public bool IsByLevelStyle => Style == Symbology.StyleByLevel;

        public string TypeName => Enum.IsDefined(typeof(ElementType), RawType) ? Type.ToString() : "Type" + RawType;

        public IEnumerable<Element> SelfAndDescendants()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.SelfAndDescendants()) yield return d;
        }

        /// <summary>Looks up a string linkage by key (1 = name for cells/models/levels; 2 = description …).</summary>
        public string? GetStringLinkage(uint key) => Linkages.OfType<StringLinkage>().FirstOrDefault(s => s.Key == key)?.Value;

        public IEnumerable<KeyValuePair<string, object?>> AllProperties() =>
            PropertyGroups.SelectMany(g => g.Properties.Select(p => new KeyValuePair<string, object?>(g.Name + "." + p.Key, p.Value)));

        public override string ToString() => $"{TypeName} id={Id} level={LevelId}";
    }

    public sealed class FillInfo
    {
        public uint? Color { get; internal set; }
        public bool IsGradient { get; internal set; }
        public uint[]? GradientColors { get; internal set; }
        public double Transparency { get; internal set; }
    }

    /// <summary>
    /// Element line style modifiers (V8 linkage 0x79F9: <c>u32 modifiers</c> followed by packed doubles for each
    /// STYLEMOD bit present, in bit order: scale 0x01, dash scale 0x02, gap scale 0x04, start width 0x08, end width 0x10,
    /// distance phase 0x20, fraction phase 0x40; 0x80 = centre phase (no value), 0x2000 = true (UOR) width).
    /// </summary>
    public sealed class LineStyleParams
    {
        public uint Modifiers { get; internal set; }
        public double Scale { get; internal set; } = 1;
        public double DashScale { get; internal set; } = 1;
        public double GapScale { get; internal set; } = 1;
        public double StartWidth { get; internal set; }
        public double EndWidth { get; internal set; }
        /// <summary>Distance phase (STYLEMOD_DISTPHASE), definition units.</summary>
        public double Phase { get; internal set; }
        /// <summary>Fraction phase (STYLEMOD_FRACTPHASE).</summary>
        public double FractionPhase { get; internal set; }
        public bool CenterPhase => (Modifiers & 0x80) != 0;
        public bool TrueWidth { get; internal set; }
        public bool HasScale => (Modifiers & 0x01) != 0;

        internal static LineStyleParams? FromV8Linkage(byte[] d)
        {
            if (d.Length < 4) return null;
            uint mods = BitConverter.ToUInt32(d, 0);
            var p = new LineStyleParams { Modifiers = mods, TrueWidth = (mods & 0x2000) != 0 };
            int o = 4;
            double Next() { double v = o + 8 <= d.Length ? BitConverter.ToDouble(d, o) : 0; o += 8; return v; }
            if ((mods & 0x01) != 0) p.Scale = Next();
            if ((mods & 0x02) != 0) p.DashScale = Next();
            if ((mods & 0x04) != 0) p.GapScale = Next();
            if ((mods & 0x08) != 0) p.StartWidth = Next();
            if ((mods & 0x10) != 0) p.EndWidth = Next();
            if ((mods & 0x20) != 0) p.Phase = Next();
            if ((mods & 0x40) != 0) p.FractionPhase = Next();
            if (double.IsNaN(p.Scale) || double.IsInfinity(p.Scale) || p.Scale <= 0) p.Scale = 1;
            return p;
        }
    }

    /// <summary>A named group of business properties attached to an element (one EC instance, a tag set, a DB link …).</summary>
    public sealed class PropertyGroup
    {
        public string Name { get; set; } = "";
        /// <summary>Origin: "EC", "Tag", "StringLinkage", "Database", "Civil", "ItemType" …</summary>
        public string Source { get; set; } = "";
        public string? Schema { get; set; }
        public string? ClassName { get; set; }
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);
        public override string ToString() => $"{Source}:{Name} ({Properties.Count} props)";
    }

    // ---------------------------------------------------------------- concrete element types

    public sealed class LineElement : Element
    {
        public DPoint3d Start { get; internal set; }
        public DPoint3d End { get; internal set; }
    }

    /// <summary>Line string (4), shape (6), curve (11), point string (22).</summary>
    public sealed class LineStringElement : Element
    {
        public List<DPoint3d> Points { get; } = new();
        public bool IsClosed => RawType == (int)ElementType.Shape;
        public bool IsCurve => RawType == (int)ElementType.Curve;
        public bool IsPointString => RawType == (int)ElementType.PointString;
    }

    /// <summary>Ellipse (15) and arc (16). Angles in radians; axes in UORs.</summary>
    public sealed class ArcElement : Element
    {
        public DPoint3d Origin { get; internal set; }
        public double PrimaryAxis { get; internal set; }
        public double SecondaryAxis { get; internal set; }
        public Matrix3 Rotation { get; internal set; } = Matrix3.Identity;
        public double StartAngle { get; internal set; }
        public double SweepAngle { get; internal set; } = 2 * Math.PI;
        public bool IsFullEllipse => RawType == (int)ElementType.Ellipse;
        public bool IsCircle => Math.Abs(PrimaryAxis - SecondaryAxis) <= 1e-9 * Math.Max(1, Math.Abs(PrimaryAxis));

        public DPoint3d PointAt(double angle) =>
            Origin + Rotation.Multiply(new DPoint3d(PrimaryAxis * Math.Cos(angle), SecondaryAxis * Math.Sin(angle), 0));
    }

    public sealed class TextElement : Element
    {
        public string Text { get; internal set; } = "";
        public DPoint3d Origin { get; internal set; }
        /// <summary>Character height in UORs.</summary>
        public double Height { get; internal set; }
        /// <summary>Character width in UORs.</summary>
        public double Width { get; internal set; }
        public Matrix3 Rotation { get; internal set; } = Matrix3.Identity;
        public uint FontId { get; internal set; }
        public string? FontName { get; internal set; }
        public TextJustification Justification { get; internal set; }
        /// <summary>Stored total extents (UORs), when available.</summary>
        public double StoredLength { get; internal set; }
        public double StoredHeight { get; internal set; }
        public bool IsUnicode { get; internal set; }
        /// <summary>Angle of the baseline in the XY plane, radians.</summary>
        public double RotationAngle => Math.Atan2(Rotation.M10, Rotation.M00);

        /// <summary>
        /// The user (justification) point: <see cref="Origin"/> is the stored lower-left corner; MicroStation reports
        /// the point implied by <see cref="Justification"/> over the stored text extents (length × height).
        /// </summary>
        public DPoint3d JustificationPoint
        {
            get
            {
                int j = (int)Justification;
                // left (incl. left-margin, left-descender) / center / right (incl. right-margin)
                double fx = (j <= 5 || j == 16) ? 0.0 : ((j >= 6 && j <= 8) || j == 17) ? 0.5 : 1.0;
                double fy = j switch { 0 or 3 or 6 or 9 or 12 => 1.0, 1 or 4 or 7 or 10 or 13 => 0.5, _ => 0.0 };
                return Origin + Rotation.Column(0) * (StoredLength * fx) + Rotation.Column(1) * (StoredHeight * fy);
            }
        }
    }

    public sealed class TextNodeElement : Element
    {
        public DPoint3d Origin { get; internal set; }
        public double LineSpacing { get; internal set; }
        public double Height { get; internal set; }
        public double Width { get; internal set; }
        public Matrix3 Rotation { get; internal set; } = Matrix3.Identity;
        public uint FontId { get; internal set; }
        public TextJustification Justification { get; internal set; }
        public uint NodeNumber { get; internal set; }
        public string Text => string.Join("\n", Children.OfType<TextElement>().Select(t => t.Text));
    }

    /// <summary>MicroStation text justification codes.</summary>
    public enum TextJustification
    {
        LeftTop = 0, LeftCenter = 1, LeftBottom = 2, LeftMarginTop = 3, LeftMarginCenter = 4, LeftMarginBottom = 5,
        CenterTop = 6, CenterCenter = 7, CenterBottom = 8, RightMarginTop = 9, RightMarginCenter = 10, RightMarginBottom = 11,
        RightTop = 12, RightCenter = 13, RightBottom = 14, LeftDescender = 16, CenterDescender = 17, RightDescender = 18,
    }

    /// <summary>Complex chain (12) / complex shape (14).</summary>
    public sealed class ComplexElement : Element
    {
        public bool IsClosed => RawType == (int)ElementType.ComplexShape;
    }

    /// <summary>Named (type 2) cell. Children are in UORs already placed in model space.</summary>
    public sealed class CellElement : Element
    {
        public string? Name { get; internal set; }
        public string? Description { get; internal set; }
        public DPoint3d Origin { get; internal set; }
        public Matrix3 Transform { get; internal set; } = Matrix3.Identity;
        public bool IsAnnotation { get; internal set; }
        public bool IsPointCell => !IsSnappable; // V7 heuristic; refined by readers
        public Range3d LocalRange { get; internal set; } = Range3d.Empty;
    }

    /// <summary>Shared cell definition (34). Children are in definition-local UORs.</summary>
    public sealed class SharedCellDefinition : Element
    {
        public string? Name { get; internal set; }
        public string? Description { get; internal set; }
        public DPoint3d Origin { get; internal set; }
        public bool IsPointCell { get; internal set; }
        public bool IsAnnotation { get; internal set; }
    }

    /// <summary>Shared cell instance (35) referencing a <see cref="SharedCellDefinition"/>.</summary>
    public sealed class SharedCellElement : Element
    {
        public string? Name { get; internal set; }
        public ulong DefinitionId { get; internal set; }
        public SharedCellDefinition? Definition { get; internal set; }
        public DPoint3d Origin { get; internal set; }
        /// <summary>Rotation × scale matrix applied to definition geometry.</summary>
        public Matrix3 Transform { get; internal set; } = Matrix3.Identity;
        public uint OverrideFlags { get; internal set; }
    }

    public sealed class BSplineCurveElement : Element
    {
        public int Order { get; internal set; }
        public bool Closed { get; internal set; }
        public bool Rational { get; internal set; }
        public int CurveType { get; internal set; }
        public List<DPoint3d> Poles { get; } = new();
        public List<double> Knots { get; } = new();
        public List<double> Weights { get; } = new();
        /// <summary>True when the stored knot vector is only the interior knots (MicroStation default).</summary>
        public bool InteriorKnotsOnly { get; internal set; } = true;
    }

    public sealed class ConeElement : Element
    {
        public DPoint3d Center1 { get; internal set; }
        public DPoint3d Center2 { get; internal set; }
        public double Radius1 { get; internal set; }
        public double Radius2 { get; internal set; }
        public Matrix3 Rotation { get; internal set; } = Matrix3.Identity;
    }

    /// <summary>Polyface mesh (105 with matrix 101/102/103 components).</summary>
    public sealed class MeshElement : Element
    {
        public int MeshStyle { get; internal set; }
        public List<DPoint3d> Vertices { get; } = new();
        /// <summary>Faces as 0-based vertex index lists.</summary>
        public List<int[]> Faces { get; } = new();
    }

    // DimensionElement (33): see Dimension.cs.

    public sealed class MultilineElement : Element
    {
        public List<DPoint3d> Points { get; } = new();
        public List<double> LineOffsets { get; } = new();
        public bool Closed { get; internal set; }
        public DPoint3d Normal { get; internal set; } = new(0, 0, 1);
    }

    /// <summary>Tag value (type 37) attached to a target element.</summary>
    public sealed class TagElement : Element
    {
        public ulong TargetId { get; internal set; }
        public ulong TagSetId { get; internal set; }
        public int TagIndex { get; internal set; }
        public string? TagSetName { get; internal set; }
        public string? TagName { get; internal set; }
        public object? Value { get; internal set; }
        public DPoint3d Origin { get; internal set; }
        public bool Displayed { get; internal set; }
        public double TextHeight { get; internal set; }
        public double TextWidth { get; internal set; }
        public uint FontId { get; internal set; }
    }

    public sealed class ReferenceElement : Element
    {
        public ReferenceAttachment Attachment { get; } = new();
    }

    /// <summary>Table entry (95/96) — level, font, line style, text style … records.</summary>
    public sealed class TableEntryElement : Element { }

    /// <summary>Raw element without a dedicated decoder.</summary>
    public sealed class GenericElement : Element { }
}
