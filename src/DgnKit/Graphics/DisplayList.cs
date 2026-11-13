using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.Model;

namespace DgnKit.Graphics
{
    public enum PrimitiveKind { Polyline, Arc, Text, Point, Mesh }

    /// <summary>Fully resolved symbology (ByLevel/ByCell applied) for a primitive.</summary>
    public sealed class ResolvedSymbology
    {
        public uint ColorIndex { get; set; }
        public RgbColor Color { get; set; }
        public int WeightIndex { get; set; }
        /// <summary>Plot width in millimetres derived from the weight index.</summary>
        public double WeightMm => WeightTable.ToMm(WeightIndex);
        public int StyleNumber { get; set; }
        public string StyleName { get; set; } = "Continuous";
        /// <summary>
        /// Effective custom line style scale: the element's scale modifier (1 when absent) × the model's line style
        /// scale for non-physical styles.
        /// </summary>
        public double LineStyleScale { get; set; } = 1;
        /// <summary>The custom line style definition (from the element's own file), when the file holds one.</summary>
        public LineStyleDefinition? LineStyle { get; set; }
        /// <summary>
        /// Dash pattern of a custom line style in <b>root master units</b> (alternating dash, gap … starting with a dash),
        /// with definition units, element scale and reference scaling applied. Null = continuous / not a custom style
        /// (built-in styles 1..7 are device patterns: see <see cref="LineStyleDefinition.BuiltInPatternMm"/>).
        /// </summary>
        public double[]? DashPattern { get; set; }
        /// <summary>Distance into <see cref="DashPattern"/> where the element starts (root master units).</summary>
        public double DashPhase { get; set; }
        /// <summary>Visible stroke width of a custom style's dashes in root master units (0 = no width: use the weight).</summary>
        public double LineStyleWidth { get; set; }
        /// <summary>Factor from <see cref="LineStyle"/> definition units to root master units (0 when unknown).</summary>
        public double LineStyleToMaster { get; set; }

        internal ResolvedSymbology Clone() => (ResolvedSymbology)MemberwiseClone();
        public bool Filled { get; set; }
        public RgbColor? FillColor { get; set; }
        public double Transparency { get; set; }
        public int Priority { get; set; }
        public ElementClass Class { get; set; }
    }

    public static class WeightTable
    {
        /// <summary>Default MicroStation weight (0..31) → plotted line width (mm). Configurable.</summary>
        public static double[] Millimetres { get; set; } =
        {
            0.13, 0.18, 0.25, 0.30, 0.35, 0.40, 0.45, 0.50, 0.53, 0.60, 0.65, 0.70, 0.80, 0.90, 1.00, 1.06,
            1.20, 1.30, 1.40, 1.58, 1.70, 1.80, 2.00, 2.11, 2.20, 2.40, 2.60, 2.80, 3.00, 3.20, 3.40, 3.60,
        };
        public static double ToMm(int w) => Millimetres[Math.Max(0, Math.Min(Millimetres.Length - 1, w))];
    }

    /// <summary>
    /// A drawable primitive in <b>master units of the root model</b>, with resolved symbology and a back-pointer to its
    /// source element (for layer, type, and metadata). This is the neutral "engineering data" every exporter consumes.
    /// </summary>
    public sealed class Primitive
    {
        public PrimitiveKind Kind { get; set; }
        /// <summary>Top-level element (cell/complex header or the element itself) — carries the business metadata.</summary>
        public Element Source { get; set; } = null!;
        /// <summary>The leaf element that produced this primitive.</summary>
        public Element Leaf { get; set; } = null!;
        public uint LevelId { get; set; }
        public string LevelName { get; set; } = "";
        public ResolvedSymbology Symbology { get; set; } = new();
        /// <summary>Polyline / polygon vertices, point location (Point), mesh vertices.</summary>
        public List<DPoint3d> Points { get; set; } = new();
        public bool Closed { get; set; }
        /// <summary>For polylines that came from curves (arcs, splines) — true if Points is a tessellation.</summary>
        public bool IsStroked { get; set; }

        // Arc / ellipse (Kind == Arc): Center + AxisX/AxisY are full-length vectors (radius incl.).
        public DPoint3d Center { get; set; }
        public DPoint3d AxisX { get; set; }
        public DPoint3d AxisY { get; set; }
        public double StartAngle { get; set; }
        public double SweepAngle { get; set; }

        // Text (Kind == Text): Points[0] = origin (justification point as stored).
        public string? Text { get; set; }
        public double TextHeight { get; set; }
        public double TextWidth { get; set; }
        public Matrix3 TextRotation { get; set; } = Matrix3.Identity;
        public TextJustification Justification { get; set; }
        public string? FontName { get; set; }

        // Mesh (Kind == Mesh)
        public List<int[]>? Faces { get; set; }

        /// <summary>Cell/shared-cell name path (outermost first) when the primitive is inside cells.</summary>
        public string? CellName { get; set; }
        /// <summary>Reference attachment path (logical names) when drawn from a reference.</summary>
        public string? ReferencePath { get; set; }
        /// <summary>Model the primitive was drawn from.</summary>
        public string? ModelName { get; set; }

        public Range3d Range()
        {
            var r = Range3d.Empty;
            if (Kind == PrimitiveKind.Arc)
            {
                foreach (var p in ArcPoints(16)) r.Extend(p);
            }
            else r.Extend(Points);
            return r;
        }

        public DPoint3d ArcPoint(double angle) => Center + AxisX * Math.Cos(angle) + AxisY * Math.Sin(angle);

        /// <summary>Strokes the arc into points (minimum <paramref name="minSegments"/>, chord-height ≈ tolerance).</summary>
        public IEnumerable<DPoint3d> ArcPoints(int minSegments = 8, double chordTol = 0)
        {
            double r = Math.Max(AxisX.Length, AxisY.Length);
            int n = minSegments;
            if (chordTol > 0 && r > 0)
            {
                double dTheta = 2 * Math.Acos(Math.Max(-1, Math.Min(1, 1 - chordTol / r)));
                if (dTheta > 1e-6) n = Math.Max(n, (int)Math.Ceiling(Math.Abs(SweepAngle) / dTheta));
            }
            n = Math.Min(n, 2048);
            for (int i = 0; i <= n; i++) yield return ArcPoint(StartAngle + SweepAngle * i / n);
        }
    }

    public sealed class DisplayListOptions
    {
        public string? ModelName { get; set; }
        public bool IncludeReferences { get; set; } = true;
        public bool IncludeInvisible { get; set; }
        public bool IncludeConstruction { get; set; } = true;
        /// <summary>Respect level "displayed" flags (when decoded).</summary>
        public bool RespectLevelDisplay { get; set; } = true;
        /// <summary>Chord tolerance for stroking curves, in master units (0 = automatic).</summary>
        public double ChordTolerance { get; set; }
        /// <summary>Stroke arcs inside complex chains/shapes into the parent polyline (true) or emit separate arcs.</summary>
        public bool MergeComplexChains { get; set; } = true;
        public int MaxReferenceDepth { get; set; } = 4;
    }
}
