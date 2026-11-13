using System;
using System.Collections.Generic;
using System.Linq;

namespace DgnKit.Model
{
    public enum LineStyleComponentKind { Unknown, Internal, StrokePattern, PointSymbol, Compound, LinePoint, RasterImage }

    /// <summary>Units of a line style definition (LSATTR_UNIT* in the name entry attributes).</summary>
    public enum LineStyleUnits { Master = 0, Uor = 1, Device = 2 }

    /// <summary>Phase of a stroke pattern (LsPhaseMode).</summary>
    public enum LineStylePhaseMode { None = 0, Fixed = 1, Fraction = 2, Center = 3 }

    /// <summary>Where a line-point symbol is placed.</summary>
    public enum LineStyleSymbolLocation { Stroke = 0, LineOrigin = 1, LineEnd = 2, LineVertex = 3, Unknown = 4 }

    /// <summary>One dash/gap of a stroke pattern (StrokeData). Lengths/widths in the definition's units.</summary>
    public sealed class LineStyleStroke
    {
        public double Length { get; set; }
        public bool IsDash { get; set; }
        public double StartWidth { get; set; }
        public double EndWidth { get; set; }
        /// <summary>Raw strokeMode bits: 1 dash, 2 ray (rigid: continue around corners), 4 scalable, 8 invert in first code, 16 invert in last code.</summary>
        public byte StrokeMode { get; set; }
        /// <summary>Raw widthMode bits: 1 left half, 2 right half (3 = full width), 4 taper, 8 end taper.</summary>
        public byte WidthMode { get; set; }
        /// <summary>Cap: 0 closed, 1 open, 2 extended, ≥3 arc with that many vectors.</summary>
        public byte CapMode { get; set; }
        public bool IsStretchable => (StrokeMode & 0x04) != 0;
        public bool BypassCorner => (StrokeMode & 0x02) != 0;
        public bool InvertStart => (StrokeMode & 0x08) != 0;
        public bool InvertEnd => (StrokeMode & 0x10) != 0;
        public bool HasLeftWidth => (WidthMode & 0x01) != 0;
        public bool HasRightWidth => (WidthMode & 0x02) != 0;
        public override string ToString() => (IsDash ? "dash " : "gap ") + Length.ToString("G6");
    }

    /// <summary>A symbol placed by a line-point component (PointSymInfo).</summary>
    public sealed class LineStylePointSymbol
    {
        public LineStyleComponent? Symbol { get; set; }
        public int StrokeNumber { get; set; }
        /// <summary>Raw mod1 flags (LCPOINT_*): bits 0-1 origin/end/center of stroke, 4 line origin, 8 line end, 0x10 vertex, 0x20 adjusted rotation, 0x40 absolute rotation, 0x100 no scale, 0x200 no clip, 0x400 no partial, 0x800 project partial, 0x4000 colour, 0x8000 weight from symbol.</summary>
        public ushort Flags { get; set; }
        public double XOffset { get; set; }
        public double YOffset { get; set; }
        /// <summary>Rotation about Z in radians (stored in degrees).</summary>
        public double Angle { get; set; }
        public LineStyleSymbolLocation Location =>
            (Flags & 0x0010) != 0 ? LineStyleSymbolLocation.LineVertex :
            (Flags & 0x0004) != 0 ? LineStyleSymbolLocation.LineOrigin :
            (Flags & 0x0008) != 0 ? LineStyleSymbolLocation.LineEnd :
            (Flags & 0x0003) != 0 ? LineStyleSymbolLocation.Stroke : LineStyleSymbolLocation.Unknown;
        /// <summary>1 origin, 2 end, 3 centre of <see cref="StrokeNumber"/> (when <see cref="Location"/> is Stroke).</summary>
        public int StrokeLocation => Flags & 0x0003;
    }

    public sealed class LineStyleComponent
    {
        public LineStyleComponentKind Kind { get; set; }
        /// <summary>Resource type four-character code (e.g. 'lsLC' = 0x6C734C43) or the LsElementType for name-entry roots.</summary>
        public uint RscType { get; set; }
        public uint RscId { get; set; }
        /// <summary>Id of the line style definition element (table level 8) this component was read from.</summary>
        public ulong ElementId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }

        // --- stroke pattern (line code)
        public List<LineStyleStroke> Strokes { get; } = new();
        public double Phase { get; set; }
        /// <summary>LCOPT_* bits: 1 auto (fraction) phase, 8 iteration limit, 0x10 single segment, 0x20 centre stretch.</summary>
        public uint Options { get; set; }
        public int MaxIterate { get; set; }
        public LineStylePhaseMode PhaseMode =>
            (Options & 0x20) != 0 ? LineStylePhaseMode.Center : (Options & 0x01) != 0 ? LineStylePhaseMode.Fraction :
            Phase != 0 ? LineStylePhaseMode.Fixed : LineStylePhaseMode.None;
        public bool HasIterationLimit => (Options & 0x08) != 0;
        public bool IsSingleSegment => (Options & 0x10) != 0;
        public double PatternLength => Strokes.Sum(s => s.Length);

        // --- compound
        public List<LineStyleComponent> Children { get; } = new();
        /// <summary>Perpendicular offset of each compound child (definition units).</summary>
        public List<double> ChildOffsets { get; } = new();

        // --- line point
        public LineStyleComponent? LineCode { get; set; }
        public List<LineStylePointSymbol> Symbols { get; } = new();

        // --- point symbol
        /// <summary>Symbol scale ("unit scale" — always applied to the symbol geometry).</summary>
        public double SymbolScale { get; set; } = 1;
        public Geometry.Range3d SymbolRange { get; set; } = Geometry.Range3d.Empty;
        /// <summary>LSSYM_*: 1 3D, 2 no scale.</summary>
        public uint SymbolFlags { get; set; }
        public ulong SymbolElementId { get; set; }
        /// <summary>Resolved symbol geometry for point-symbol components (the "Linestyle Definition Cell" in the dictionary).</summary>
        public List<Elements.Element> SymbolElements { get; } = new();

        public IEnumerable<LineStyleComponent> SelfAndDescendants()
        {
            yield return this;
            var subs = Children.AsEnumerable();
            if (LineCode != null) subs = subs.Append(LineCode);
            subs = subs.Concat(Symbols.Where(s => s.Symbol != null).Select(s => s.Symbol!));
            foreach (var c in subs)
                foreach (var d in c.SelfAndDescendants()) yield return d;
        }

        public override string ToString() => $"{Kind} '{Name}'" + (Strokes.Count > 0 ? " [" + string.Join(", ", Strokes) + "]" : "");
    }

    /// <summary>A line style definition. Numbers 0..7 are MicroStation's built-in cosmetic styles; custom styles use negative/large ids and names.</summary>
    public sealed class LineStyleDefinition
    {
        public int Number { get; internal set; }
        public string Name { get; internal set; } = "";
        public ulong ElementId { get; internal set; }
        public LineStyleComponent? Root { get; internal set; }
        public bool IsBuiltIn => Number >= 0 && Number <= 7;
        /// <summary>Raw name-entry attributes (LSATTR_*).</summary>
        public uint Attributes { get; internal set; }
        public LineStyleUnits Units { get; internal set; }
        /// <summary>LSATTR_PHYSICAL: the style is sized in real-world units (dgnlib styles).</summary>
        public bool UnitsArePhysical { get; internal set; }
        /// <summary>LSATTR_CONTINUOUS (DWG-imported continuous styles).</summary>
        public bool IsContinuous => (Attributes & 0x08) != 0;
        /// <summary>
        /// Definition units → master units of the file's default model (Master 1, Uor 1/uorPerMaster). 0 for device
        /// (pixel) units, which have no world size.
        /// </summary>
        public double UnitScale { get; internal set; } = 1.0;
        /// <summary>True when the definition was found and decoded (otherwise the style lives in an external resource/dgnlib).</summary>
        public bool HasDefinition => Root != null && Root.ElementId != 0;

        /// <summary>
        /// Flattened dash pattern of the main stroke pattern (alternating dash, gap, dash, gap … lengths, starting with a
        /// dash) in <b>definition units</b>; multiply by <see cref="UnitScale"/> for master units. Null for continuous
        /// lines or when no definition is available. See <see cref="FlattenDashes"/>.
        /// </summary>
        public double[]? DashPattern { get; internal set; }
        /// <summary>Distance (definition units) into <see cref="DashPattern"/> at which the line starts (fixed phase / leading gaps).</summary>
        public double DashPhase { get; internal set; }

        /// <summary>Factor from definition units to UORs of a model with <paramref name="uorPerMaster"/> (0 for device units).</summary>
        public static double DefinitionToUor(LineStyleUnits units, double uorPerMaster) => units switch
        {
            LineStyleUnits.Master => uorPerMaster,
            LineStyleUnits.Uor => 1.0,
            _ => 0,
        };

        /// <summary><see cref="DashPattern"/> in master units of the default model, or null.</summary>
        public double[]? DashPatternMaster => DashPattern == null || UnitScale <= 0 ? null : DashPattern.Select(d => d * UnitScale).ToArray();

        public override string ToString() => $"{Number}: {Name}";

        /// <summary>
        /// The stroke pattern that best represents the style as a simple dashed line: the root line code; for a line
        /// point its line code; for a compound the child closest to the centre line that has gaps (else the first).
        /// </summary>
        public static LineStyleComponent? MainStrokePattern(LineStyleComponent? c)
        {
            switch (c?.Kind)
            {
                case LineStyleComponentKind.StrokePattern:
                case LineStyleComponentKind.Internal:
                    return c.Strokes.Count > 0 ? c : null;
                case LineStyleComponentKind.LinePoint:
                    return MainStrokePattern(c.LineCode);
                case LineStyleComponentKind.Compound:
                {
                    var cands = c.Children.Select((k, i) => (pat: MainStrokePattern(k), off: i < c.ChildOffsets.Count ? Math.Abs(c.ChildOffsets[i]) : 0))
                        .Where(t => t.pat != null).ToList();
                    var dashed = cands.Where(t => t.pat!.Strokes.Any(s => !s.IsDash && s.Length > 0)).OrderBy(t => t.off).ToList();
                    return dashed.Count > 0 ? dashed[0].pat : cands.OrderBy(t => t.off).Select(t => t.pat).FirstOrDefault();
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// Visible width (definition units) shared by every dash of a stroke pattern: full-width strokes count their
        /// width, half-width (left or right only) strokes half of it. 0 when dashes have no width or differ.
        /// </summary>
        public static double UniformWidth(LineStyleComponent? pattern)
        {
            if (pattern == null) return 0;
            double? w = null;
            foreach (var s in pattern.Strokes.Where(s => s.IsDash && s.Length > 0))
            {
                int sides = (s.HasLeftWidth ? 1 : 0) + (s.HasRightWidth ? 1 : 0);
                double v = sides == 0 ? 0 : Math.Max(s.StartWidth, s.EndWidth) * sides / 2.0;
                if (w == null) w = v;
                else if (Math.Abs(w.Value - v) > 1e-9 * Math.Max(1, v)) return 0;
            }
            return w ?? 0;
        }

        /// <summary>
        /// Alternating dash/gap lengths of a stroke pattern (adjacent strokes of the same kind merged, rotated to start
        /// with a dash). Returns null when the pattern has no gaps (continuous) or no dashes (invisible).
        /// <paramref name="phase"/> receives the distance into the returned pattern where drawing starts.
        /// </summary>
        public static double[]? FlattenDashes(LineStyleComponent? pattern, out double phase)
        {
            phase = 0;
            if (pattern == null || pattern.Strokes.Count == 0) return null;
            var runs = new List<(bool dash, double len)>();
            foreach (var s in pattern.Strokes)
            {
                if (s.Length <= 0) continue;
                if (runs.Count > 0 && runs[runs.Count - 1].dash == s.IsDash) runs[runs.Count - 1] = (s.IsDash, runs[runs.Count - 1].len + s.Length);
                else runs.Add((s.IsDash, s.Length));
            }
            if (!runs.Any(r => !r.dash) || !runs.Any(r => r.dash)) return null;
            double total = runs.Sum(r => r.len);
            // origin = position in the stored pattern where the returned pattern starts
            double origin = 0;
            if (runs.Count > 1 && runs[0].dash == runs[runs.Count - 1].dash)
            {
                // the pattern repeats: fold the last run into the first
                var last = runs[runs.Count - 1];
                runs.RemoveAt(runs.Count - 1);
                runs[0] = (last.dash, runs[0].len + last.len);
                origin -= last.len;
            }
            if (!runs[0].dash)
            {
                // start with a dash: rotate the leading gap to the end
                origin += runs[0].len;
                runs.Add(runs[0]);
                runs.RemoveAt(0);
            }
            double start = pattern.PhaseMode switch
            {
                LineStylePhaseMode.Fixed => pattern.Phase,
                LineStylePhaseMode.Fraction => pattern.Phase * total,
                _ => 0,
            };
            phase = start - origin;
            phase = ((phase % total) + total) % total;
            return runs.Select(r => r.len).ToArray();
        }

        /// <summary>
        /// MicroStation built-in styles 0..7 rendered as dash patterns in output-device millimetres (approximation of the default
        /// cosmetic patterns; actual on-screen sizes are pixel based).
        /// </summary>
        public static double[]? BuiltInPatternMm(int style) => style switch
        {
            1 => new[] { 0.6, 0.6 },                       // dotted
            2 => new[] { 3.0, 1.5 },                       // medium dashed
            3 => new[] { 6.0, 1.5 },                       // long dashed
            4 => new[] { 6.0, 1.5, 1.0, 1.5 },             // dot-dash
            5 => new[] { 1.5, 1.5 },                       // short dashed
            6 => new[] { 6.0, 1.5, 1.0, 1.5, 1.0, 1.5 },   // dash-dot-dot
            7 => new[] { 6.0, 1.5, 3.0, 1.5 },             // long dash-short dash
            _ => null,
        };

        public static string BuiltInName(int style) => style switch
        {
            0 => "Continuous", 1 => "Dotted", 2 => "MediumDash", 3 => "LongDash", 4 => "DotDash",
            5 => "ShortDash", 6 => "DashDotDot", 7 => "LongDashShortDash", _ => "Style" + style,
        };
    }

    public sealed class LineStyleTable
    {
        private readonly Dictionary<int, LineStyleDefinition> _byNumber = new();
        public IReadOnlyCollection<LineStyleDefinition> All => _byNumber.Values;

        internal void Add(LineStyleDefinition d) => _byNumber[d.Number] = d;

        public LineStyleDefinition? Find(int number) => _byNumber.TryGetValue(number, out var d) ? d : null;
        public LineStyleDefinition? FindByName(string name) => _byNumber.Values.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

        public string NameOf(int number)
        {
            if (_byNumber.TryGetValue(number, out var d) && !string.IsNullOrEmpty(d.Name)) return d.Name;
            if (number >= 0 && number <= 7) return LineStyleDefinition.BuiltInName(number);
            return "LineStyle" + number;
        }
    }
}
