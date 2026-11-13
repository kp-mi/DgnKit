using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;

namespace DgnKit.Graphics
{
    /// <summary>One synthesised dimension graphic, in the dimension's own coordinates (UORs).</summary>
    public sealed class DimensionGraphic
    {
        public enum GraphicKind { Line, Polygon, Circle, Text }
        public GraphicKind Kind { get; internal set; }
        public List<DPoint3d> Points { get; } = new();
        public bool Filled { get; internal set; }
        public DPoint3d Center { get; internal set; }
        public double Radius { get; internal set; }
        // text: Points[0] = lower-left origin
        public string? Text { get; internal set; }
        public DPoint3d TextDirection { get; internal set; } = new(1, 0, 0);
        public DPoint3d TextUp { get; internal set; } = new(0, 1, 0);
        public double TextHeight { get; internal set; }
        public double TextWidth { get; internal set; }
        /// <summary>"dimension", "extension", "terminator", "leader" or "text".</summary>
        public string Part { get; internal set; } = "";
    }

    /// <summary>
    /// Regenerates what MicroStation draws for a dimension from its definition points and stored style values
    /// (rules checked against MicroStation's dropped graphics, see docs/FORMAT-V8.md §4.2):
    /// linear dims → extension lines (offset/extend), dimension line at <see cref="DimensionElement.Height"/>, arrow/stroke
    /// terminators (inside, or outside with an extended dimension line when the segment is shorter than two terminators plus
    /// the margin) and the segment text centred above the line, rotated to read left→right / bottom→top;
    /// notes → leader polyline with an arrow or dot at the first point (the note text is a separate cell).
    /// </summary>
    public static class DimensionGraphics
    {
        public static List<DimensionGraphic> Build(DimensionElement d) => Build(d, null);

        /// <param name="d">The dimension.</param>
        /// <param name="fontName">Name of the dimension's font (<see cref="DimensionElement.FontId"/> resolved through the file's
        /// font table); used for the text width estimate (<see cref="FontMetrics.WidthFactor"/>).</param>
        public static List<DimensionGraphic> Build(DimensionElement d, string? fontName)
        {
            var o = new List<DimensionGraphic>();
            if (d.Points.Count < 2) return o;
            double wk = FontMetrics.WidthFactor(fontName);
            try
            {
                if (d.Kind == DimensionKind.Note) BuildNote(d, o);
                else if (d.IsLinear) BuildLinear(d, o, wk);
                else BuildFallback(d, o, wk);
            }
            catch (ArgumentException) { }
            return o;
        }

        private static void BuildNote(DimensionElement d, List<DimensionGraphic> o)
        {
            var leader = new DimensionGraphic { Kind = DimensionGraphic.GraphicKind.Line, Part = "leader" };
            foreach (var p in d.Points)
                if (leader.Points.Count == 0 || leader.Points[leader.Points.Count - 1].DistanceTo(p) > 1e-9) leader.Points.Add(p);
            var tip = d.Points[0];
            var next = leader.Points.Count >= 2 ? leader.Points[1] : tip + d.XAxis;
            if (d.CurvedLeader && leader.Points.Count == 2)
            {
                // MicroStation's curved leader arrives horizontally (dimension X) at the note. Approximated by a quadratic
                // Bézier whose control point sits on that horizontal, min(0.3·chord, 2.5·text height) before the end
                // (fitted to 52 oracle arrows: start tangent within a few degrees).
                var p0 = leader.Points[0]; var p1 = leader.Points[1];
                var X = d.XAxis;
                double du = (p1 - p0).Dot(X);
                double k = Math.Min(0.3 * p0.DistanceTo(p1), 2.5 * Math.Max(d.TextHeight, 1e-12));
                var c = p1 - X * (Math.Sign(du == 0 ? 1 : du) * k);
                leader.Points.Clear();
                const int n = 16;
                for (int i = 0; i <= n; i++)
                {
                    double t = (double)i / n, a = (1 - t) * (1 - t), b = 2 * t * (1 - t), cc = t * t;
                    leader.Points.Add(p0 * a + c * b + p1 * cc);
                }
                next = c;
            }
            if (leader.Points.Count >= 2) o.Add(leader);
            AddTerminator(o, d, d.NoteTerminator, tip, (next - tip).Normalized());
        }

        private static void BuildLinear(DimensionElement d, List<DimensionGraphic> o, double wk)
        {
            var g = d.Geometry;
            var X = d.XAxis; var Y = d.YAxis;
            if (X.Length < 1e-12 || Y.Length < 1e-12) return;
            var p0 = d.Points[0];
            double h = d.Height;
            int n = d.Points.Count;
            var u = new double[n]; var v = new double[n];
            for (int i = 0; i < n; i++) { var dp = d.Points[i] - p0; u[i] = dp.Dot(X); v[i] = dp.Dot(Y); }
            DPoint3d At(double uu, double vv) => p0 + X * uu + Y * vv;

            // extension lines
            for (int i = 0; i < n; i++)
            {
                double sgn = h - v[i] >= 0 ? 1 : -1;
                var a = At(u[i], v[i] + sgn * g.WitnessOffset);
                var b = At(u[i], h + sgn * g.WitnessExtend);
                if ((b - a).Dot(Y) * sgn <= 1e-12) continue;
                o.Add(Line("extension", a, b));
            }

            var term = d.Kind == DimensionKind.SizeStroke ? DimensionTerminator.Stroke : DimensionTerminator.Arrow;
            foreach (var seg in d.Segments.Count > 0 ? d.Segments : DefaultSegments(d))
            {
                double ua = u[seg.StartPoint], ub = u[seg.EndPoint];
                double len = Math.Abs(ub - ua);
                if (len < 1e-12) continue;
                double dir = ub >= ua ? 1 : -1;
                var A = At(ua, h); var Bp = At(ub, h);
                // Terminators go outside when the segment cannot hold both terminators, their margins and the text;
                // MicroStation then draws only the two outer stubs (2 × terminator width) of the dimension line.
                double textW = wk * seg.Lines.Where(l => l.Trim().Length > 0).Select(l => EstimateWidth(l, d.TextWidth > 0 ? d.TextWidth : d.TextHeight, d.FontId)).DefaultIfEmpty(0).Max();
                bool outside = term == DimensionTerminator.Arrow && len < 2 * (g.TerminatorWidth + g.Margin) + textW;
                if (outside)
                {
                    double ext = 2 * g.TerminatorWidth;
                    o.Add(Line("dimension", At(ua - dir * ext, h), A));
                    o.Add(Line("dimension", Bp, At(ub + dir * ext, h)));
                    AddTerminator(o, d, term, A, X * -dir);
                    AddTerminator(o, d, term, Bp, X * dir);
                }
                else
                {
                    o.Add(Line("dimension", A, Bp));
                    AddTerminator(o, d, term, A, X * dir);
                    AddTerminator(o, d, term, Bp, X * -dir);
                }
                AddSegmentText(o, d, seg, At((ua + ub) / 2, h), X, wk);
            }
        }

        private static IEnumerable<DimensionSegment> DefaultSegments(DimensionElement d) =>
            Enumerable.Range(1, d.Points.Count - 1).Select(i => new DimensionSegment { Index = i - 1, StartPoint = i - 1, EndPoint = i });

        /// <summary>Angular/radial/other kinds: dimension line through the points plus any text at the last point.</summary>
        private static void BuildFallback(DimensionElement d, List<DimensionGraphic> o, double wk)
        {
            var l = new DimensionGraphic { Kind = DimensionGraphic.GraphicKind.Line, Part = "dimension" };
            l.Points.AddRange(d.Points);
            o.Add(l);
            foreach (var seg in d.Segments) AddSegmentText(o, d, seg, d.Points[Math.Min(seg.EndPoint, d.Points.Count - 1)], d.XAxis, wk);
        }

        private static DimensionGraphic Line(string part, DPoint3d a, DPoint3d b)
        {
            var l = new DimensionGraphic { Kind = DimensionGraphic.GraphicKind.Line, Part = part };
            l.Points.Add(a); l.Points.Add(b);
            return l;
        }

        /// <summary>Terminator with its tip at <paramref name="tip"/>, opening along <paramref name="into"/> (unit).</summary>
        private static void AddTerminator(List<DimensionGraphic> o, DimensionElement d, DimensionTerminator t, DPoint3d tip, DPoint3d into)
        {
            var g = d.Geometry;
            double w = g.TerminatorWidth, ht = g.TerminatorHeight;
            if (into.Length < 1e-12) return;
            var perp = new DPoint3d(-into.Y, into.X, 0);
            switch (t)
            {
                case DimensionTerminator.Arrow:
                {
                    if (w <= 0 || ht <= 0) return;
                    var bc = tip + into * w;
                    var p = new DimensionGraphic { Kind = DimensionGraphic.GraphicKind.Polygon, Part = "terminator", Filled = true };
                    p.Points.Add(tip); p.Points.Add(bc + perp * (ht / 2)); p.Points.Add(bc - perp * (ht / 2)); p.Points.Add(tip);
                    o.Add(p);
                    break;
                }
                case DimensionTerminator.Stroke:
                {
                    var s = (into + perp).Normalized() * (w / 2);
                    o.Add(Line("terminator", tip - s, tip + s));
                    break;
                }
                case DimensionTerminator.Dot:
                    // MicroStation's dot terminator: filled circle of radius TerminatorHeight / 8 (oracle, 2 styles).
                    o.Add(new DimensionGraphic { Kind = DimensionGraphic.GraphicKind.Circle, Part = "terminator", Filled = true, Center = tip, Radius = ht / 8 });
                    break;
                case DimensionTerminator.Origin:
                    o.Add(new DimensionGraphic { Kind = DimensionGraphic.GraphicKind.Circle, Part = "terminator", Center = tip, Radius = ht / 2 });
                    break;
            }
        }

        /// <summary>
        /// Text block centred on <paramref name="mid"/> (a point on the dimension line), bottom line lifted by TextLift on the
        /// reading-up side; lines left-aligned within the block and stacked upward at 1.3 × text height.
        /// </summary>
        private static void AddSegmentText(List<DimensionGraphic> o, DimensionElement d, DimensionSegment seg, DPoint3d mid, DPoint3d X, double wk)
        {
            var lines = seg.Lines.Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return;
            double th = d.TextHeight > 0 ? d.TextHeight : d.Geometry.TerminatorWidth;
            double tw = d.TextWidth > 0 ? d.TextWidth : th;
            if (th <= 0) return;
            // reading direction: left→right, or bottom→top for vertical dimensions
            var xt = X;
            if (xt.X < -1e-9 || (Math.Abs(xt.X) <= 1e-9 && xt.Y < 0)) xt = xt * -1;
            var up = new DPoint3d(-xt.Y, xt.X, 0);
            double blockW = wk * lines.Max(l => EstimateWidth(l, tw, d.FontId));
            double pitch = th * 1.3;
            for (int j = 0; j < lines.Count; j++)
            {
                double lift = d.Geometry.TextLift + (lines.Count - 1 - j) * pitch;
                var org = mid - xt * (blockW / 2) + up * lift;
                var t = new DimensionGraphic
                {
                    Kind = DimensionGraphic.GraphicKind.Text, Part = "text", Text = lines[j], TextDirection = xt, TextUp = up,
                    TextHeight = th, TextWidth = tw,
                };
                t.Points.Add(org);
                o.Add(t);
            }
        }

        /// <summary>
        /// Approximate advance width of <paramref name="s"/> for character width <paramref name="w"/> (Arial-like proportions).
        /// MicroStation scales TrueType fonts (font numbers ≥ 1024) so the cap height equals the text size, i.e. the em is
        /// ≈ 1.4 × size (Arial cap height 0.716 em; matches MicroStation's centred dimension texts within ~2%). This is the
        /// same Helvetica-at-cap-height size the PDF exporter draws; wide fonts additionally scale by
        /// <see cref="FontMetrics.WidthFactor"/> (applied by the callers in <see cref="Build(DimensionElement, string)"/>).
        /// </summary>
        public static double EstimateWidth(string s, double w, uint fontId) => EstimateWidth(s, w) * (fontId >= 1024 ? 1.397 : 1.0);

        /// <summary>Approximate advance width in units of the character width (RSC-font cell proportions).</summary>
        public static double EstimateWidth(string s, double w)
        {
            double sum = 0;
            foreach (char c in s)
            {
                double f = c switch
                {
                    ' ' => 0.28, '\'' or '.' or ',' or ':' or ';' or '|' or '!' or 'i' or 'l' or 'j' => 0.25,
                    '"' or '-' or '(' or ')' or '/' or 'f' or 't' or 'r' or 'I' => 0.34,
                    'm' or 'w' => 0.80, 'M' or 'W' => 0.85,
                    _ when char.IsDigit(c) => 0.56,
                    _ when char.IsUpper(c) => 0.68,
                    _ => 0.53,
                };
                sum += f * w;
            }
            return sum;
        }
    }
}
