using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Geometry;

namespace DgnKit.Elements
{
    /// <summary>MicroStation dimension types (DgnPlatform.r.h <c>DimensionType</c>).</summary>
    public enum DimensionKind
    {
        None = 0, SizeArrow = 1, SizeStroke = 2, LocateSingle = 3, LocateStacked = 4, AngleSize = 5, ArcSize = 6,
        AngleLocation = 7, ArcLocation = 8, AngleLines = 9, AngleAxis = 10, Radius = 11, Diameter = 12, DiameterParallel = 13,
        DiameterPerpendicular = 14, CustomLinear = 15, Ordinate = 16, RadiusExtended = 17, DiameterExtended = 18, Center = 19,
        AngleAxisX = 50, AngleAxisY = 51, LabelLine = 52, Note = 53,
    }

    /// <summary>Linear value format (DimensionStyleProps.r.h <c>DimStyleProp_Value_Format</c>).</summary>
    public enum DimensionValueFormat
    {
        /// <summary>10'6" → 10.5</summary>
        MU = 0,
        /// <summary>10'6" → 10.5'</summary>
        MULabel = 1,
        /// <summary>10'6" → 126</summary>
        SU = 2,
        /// <summary>10'6" → 126"</summary>
        SULabel = 3,
        /// <summary>10'6" → 10-6</summary>
        MUDashSU = 4,
        /// <summary>10'6" → 10' 6"</summary>
        MULabelSULabel = 5,
        /// <summary>10'6" → 10'-6"</summary>
        MULabelDashSULabel = 6,
    }

    /// <summary>Terminator symbols MicroStation draws at dimension/extension line ends.</summary>
    public enum DimensionTerminator { None = 0, Arrow = 1, Stroke = 2, Origin = 3, Dot = 4, Note = 5 }

    /// <summary>One stored definition point of a dimension (UORs).</summary>
    public sealed class DimensionPoint
    {
        public DPoint3d Point { get; internal set; }
        /// <summary>Dimension-line offset from this point along the dimension's Y axis (the "height"; first point only for linear dims).</summary>
        public double OffsetY { get; internal set; }
        /// <summary>Text offset along the dimension line (⚠️ meaning not fully verified).</summary>
        public double OffsetX { get; internal set; }
        /// <summary>Raw per-point flags (u32): low 16 bits = byte offset of the override string, 0xFFFF = none.</summary>
        public uint Flags { get; internal set; }
        /// <summary>Byte offset of this point's text override in the string area, or -1.</summary>
        public int TextOffset => (Flags & 0xFFFF) == 0xFFFF ? -1 : (int)(Flags & 0xFFFF);
        /// <summary>Raw string stored for the segment ending at this point ('*' = computed value), or null.</summary>
        public string? OverrideText { get; internal set; }
    }

    /// <summary>A measured segment of a dimension, with its final display text.</summary>
    public sealed class DimensionSegment
    {
        public int Index { get; internal set; }
        /// <summary>Index into <see cref="DimensionElement.Points"/> of the segment's start and end definition points.</summary>
        public int StartPoint { get; internal set; }
        public int EndPoint { get; internal set; }
        /// <summary>Measured value in the dimension's primary master units (NaN when not a linear measure).</summary>
        public double Value { get; internal set; } = double.NaN;
        /// <summary>Formatted value only (no override).</summary>
        public string? FormattedValue { get; internal set; }
        /// <summary>Raw override ('*' replaced by <see cref="FormattedValue"/> in <see cref="Lines"/>).</summary>
        public string? OverrideText { get; internal set; }
        /// <summary>Text lines MicroStation displays for this segment (empty = nothing drawn).</summary>
        public List<string> Lines { get; } = new();
        public string Text => string.Join("\n", Lines);
        /// <summary>True when the text came from a rich text-block override (0x56DE type-5 runs).</summary>
        public bool FromTextBlock { get; internal set; }
    }

    /// <summary>Dimension geometry parameters (UORs, already multiplied by the annotation scale).</summary>
    public sealed class DimensionGeometry
    {
        public double WitnessOffset { get; internal set; }
        public double WitnessExtend { get; internal set; }
        public double TextLift { get; internal set; }
        public double TextMargin { get; internal set; }
        public double Margin { get; internal set; }
        public double TerminatorWidth { get; internal set; }
        public double TerminatorHeight { get; internal set; }
        public double StackOffset { get; internal set; }
        public double CenterSize { get; internal set; }
    }

    /// <summary>
    /// Dimension (33). MicroStation stores definition points + style values and regenerates the graphics; DgnKit decodes
    /// those and synthesises dimension/extension lines, terminators and the formatted text (see
    /// <see cref="Graphics.DimensionGraphics"/>). Layout: docs/FORMAT-V8.md §4.2.
    /// </summary>
    public sealed class DimensionElement : Element
    {
        /// <summary>Raw dimension type (see <see cref="Kind"/>).</summary>
        public int DimensionType { get; internal set; }
        public DimensionKind Kind => (DimensionKind)DimensionType;
        /// <summary>Element format version byte (11 in V8i/CONNECT files).</summary>
        public int Version { get; internal set; }
        /// <summary>Definition points (UORs).</summary>
        public List<DPoint3d> Points { get; } = new();
        public List<DimensionPoint> PointData { get; } = new();
        /// <summary>Display text per segment (one entry per segment, lines joined by '\n').</summary>
        public List<string> Texts { get; } = new();
        public List<DimensionSegment> Segments { get; } = new();
        /// <summary>Text height / width in UORs.</summary>
        public double TextHeight { get; internal set; }
        public double TextWidth { get; internal set; }
        public uint FontId { get; internal set; }
        /// <summary>Dimension orientation: column 0 = dimension (measure) axis, column 1 = height axis.</summary>
        public Matrix3 Rotation { get; internal set; } = Matrix3.Identity;
        /// <summary>View rotation stored with the dimension (block type 12), identity when absent.</summary>
        public Matrix3 ViewRotation { get; internal set; } = Matrix3.Identity;
        public DimensionGeometry Geometry { get; } = new();
        /// <summary>Primary/secondary accuracy codes (DIMACC_*: 0x81.. decimals, 0x01..0x20 fractions).</summary>
        public int PrimaryAccuracy { get; internal set; }
        public int SecondaryAccuracy { get; internal set; }
        /// <summary>Raw primary text-format flag byte (body+82).</summary>
        public int FormatFlags { get; internal set; }
        public DimensionValueFormat ValueFormat { get; internal set; }
        public bool LeadingZero { get; internal set; } = true;
        public bool TrailingZeros { get; internal set; }
        /// <summary>Dimension primary units: master/sub units per metre (from the units block); 0 = model units.</summary>
        public double MasterUnitsPerMeter { get; internal set; }
        public double SubUnitsPerMeter { get; internal set; }
        public string? MasterUnitLabel { get; internal set; }
        public string? SubUnitLabel { get; internal set; }
        /// <summary>Distance scale factor stored with the dimension (body+16).</summary>
        public double Scale { get; internal set; } = 1;
        /// <summary>Annotation scale from the 0x56DE type-0 linkage (1 when absent).</summary>
        public double AnnotationScale { get; internal set; } = 1;
        /// <summary>Terminator drawn at the first point of a note leader (0x56DE type-6 byte 44, else block 0x0E low nibble).</summary>
        public DimensionTerminator NoteTerminator { get; internal set; } = DimensionTerminator.Arrow;
        /// <summary>Note leader drawn as a curve (0x56DE type-6 byte 45 bit 0x80; ⚠️ curve shape approximated).</summary>
        public bool CurvedLeader { get; internal set; }
        /// <summary>Text lines of a rich text-block override (0x56DE type-5 runs grouped into lines).</summary>
        public List<string> TextBlockRuns { get; } = new();
        /// <summary>Raw post-point blocks (type → bytes incl. header).</summary>
        public List<KeyValuePair<int, byte[]>> Blocks { get; } = new();

        /// <summary>Height of the dimension line above the first point along the Y axis (UORs).</summary>
        public double Height => PointData.Count > 0 ? PointData[0].OffsetY : 0;
        public DPoint3d XAxis => Rotation.Column(0).Normalized();
        public DPoint3d YAxis => Rotation.Column(1).Normalized();

        public bool IsLinear => Kind is DimensionKind.SizeArrow or DimensionKind.SizeStroke or DimensionKind.LocateSingle
            or DimensionKind.LocateStacked or DimensionKind.CustomLinear;

        public override string ToString() => $"Dimension {Kind} id={Id} points={Points.Count} texts=[{string.Join(" | ", Texts)}]";
    }
}
