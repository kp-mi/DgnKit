using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V8
{
    /// <summary>
    /// Dimension element (type 33) body decoder and value formatter. Layout reverse-engineered against the MicroStation
    /// COM oracle (DimensionElement points/values + Drop() graphics); see docs/FORMAT-V8.md §4.2.
    /// </summary>
    internal static class V8Dimensions
    {
        private const int B = V8ElementDecoder.GraphicBody; // 104
        private const int PointRecordSize = 48;

        /// <summary>Linkage 0x56DE (LINKAGEID_DimExtensionLinkage) sub-types.</summary>
        private const ushort DimExtensionLinkage = 0x56DE;

        public static void Decode(DimensionElement d, LeReader r, int bodyEnd)
        {
            d.Scale = Finite(r.F64(B + 16), 1);
            d.Version = r.U8(B + 28);
            d.DimensionType = r.U8(B + 29);
            int np = r.U8(B + 31);

            // text parameters
            d.TextHeight = r.F64(B + 48);
            d.TextWidth = r.F64(B + 56);
            d.FontId = r.U32(B + 64);
            d.SecondaryAccuracy = r.U8(B + 80);
            d.PrimaryAccuracy = r.U8(B + 81);
            d.FormatFlags = r.U8(B + 82);
            ApplyFormatFlags(d);

            // geometry (9 doubles)
            var g = d.Geometry;
            g.WitnessOffset = r.F64(B + 88);
            g.WitnessExtend = r.F64(B + 96);
            g.TextLift = r.F64(B + 104);
            g.TextMargin = r.F64(B + 112);
            g.Margin = r.F64(B + 120);
            g.TerminatorWidth = r.F64(B + 128);
            g.TerminatorHeight = r.F64(B + 136);
            g.StackOffset = r.F64(B + 144);
            g.CenterSize = r.F64(B + 152);

            // orientation: 2D = 2×2 row-major matrix, 3D = quaternion (⚠️ 3D unverified: no 3D dimensions in the corpus)
            d.Rotation = d.Is3d ? QuatOrIdentity(r, B + 168) : Mat2(r, B + 168);

            int o = B + 200;
            for (int i = 0; i < np && o + PointRecordSize <= bodyEnd; i++, o += PointRecordSize)
            {
                var p = new DPoint3d(r.F64(o), r.F64(o + 8), d.Is3d ? r.F64(o + 16) : 0);
                d.Points.Add(p);
                d.PointData.Add(new DimensionPoint { Point = p, OffsetY = r.F64(o + 24), OffsetX = r.F64(o + 32), Flags = r.U32(o + 40) });
            }

            // optional blocks {u8 words, u8 type, …} until the string area
            while (o + 2 <= bodyEnd)
            {
                int words = r.U8(o), type = r.U8(o + 1);
                if (words < 2 || type == 0 || type > 0x20 || o + words * 2 > bodyEnd) break;
                var blk = r.Bytes(o, words * 2);
                d.Blocks.Add(new KeyValuePair<int, byte[]>(type, blk));
                switch (type)
                {
                    case 0x0C when words * 2 >= 40: // view rotation
                        d.ViewRotation = d.Is3d ? QuatOrIdentity(r, o + 8) : Mat2(r, o + 8);
                        break;
                    case 0x0E when words * 2 >= 6: // terminator selection: low nibble of +4 = note/first terminator
                    {
                        int t = r.U8(o + 4) & 0x0F;
                        if (t >= 1 && t <= 4) d.NoteTerminator = (DimensionTerminator)t;
                        break;
                    }
                    case 0x09 when words * 2 >= 56: // primary units: master num/den, sub num/den (units per metre = num/den)
                    {
                        double mn = r.F64(o + 24), md = r.F64(o + 32), sn = r.F64(o + 40), sd = r.F64(o + 48);
                        if (mn > 0 && md > 0) d.MasterUnitsPerMeter = mn / md;
                        if (sn > 0 && sd > 0) d.SubUnitsPerMeter = sn / sd;
                        break;
                    }
                }
                o += words * 2;
            }

            // string area: per-point override strings (low 16 bits of the point flags = byte offset)
            int strStart = o;
            foreach (var pd in d.PointData)
            {
                int off = pd.TextOffset;
                if (off < 0 || strStart + off >= bodyEnd) continue;
                pd.OverrideText = ReadString(r, strStart + off, bodyEnd);
            }

            d.MasterUnitLabel = d.GetStringLinkage(19);
            d.SubUnitLabel = d.GetStringLinkage(20);
            DecodeExtensionLinkages(d);
        }

        private static double Finite(double v, double def) => double.IsNaN(v) || double.IsInfinity(v) || v == 0 ? def : v;

        private static Matrix3 Mat2(LeReader r, int o)
        {
            double a = r.F64(o), b = r.F64(o + 8), c = r.F64(o + 16), e = r.F64(o + 24);
            if (!(Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(e) > 1e-12) || double.IsNaN(a)) return Matrix3.Identity;
            return new Matrix3(a, b, 0, c, e, 0, 0, 0, 1);
        }

        private static Matrix3 QuatOrIdentity(LeReader r, int o)
        {
            double w = r.F64(o), x = r.F64(o + 8), y = r.F64(o + 16), z = r.F64(o + 24);
            return Math.Abs(w) + Math.Abs(x) + Math.Abs(y) + Math.Abs(z) > 1e-12 ? Matrix3.FromQuaternion(w, x, y, z) : Matrix3.Identity;
        }

        /// <summary>NUL-terminated dimension string; "FF FE 01 00" prefix → 8-bit, "FF FD" → UTF-16LE.</summary>
        private static string ReadString(LeReader r, int o, int end)
        {
            if (r.U8(o) == 0xFF && r.U8(o + 1) == 0xFD)
            {
                var sb = new StringBuilder();
                for (int p = o + 2; p + 1 < end; p += 2) { char c = (char)r.U16(p); if (c == 0) break; sb.Append(c); }
                return V8ElementDecoder.MapRscFractions(sb.ToString());
            }
            if (r.U8(o) == 0xFF && r.U8(o + 1) == 0xFE) o += 4;
            int n = 0;
            while (o + n < end && r.U8(o + n) != 0) n++;
            var bytes = r.Bytes(o, n);
            return V8ElementDecoder.MapRscFractions(LinkageParser.Decode8(bytes, 0, bytes.Length));
        }

        /// <summary>
        /// Text-format flag byte (body+82): 0x20 = show sub-units, 0x80 = also show master units (with sub-units),
        /// 0x40 = unit labels. 0xE3/0xEB → 10'-6" (oracle + official PDF), 0x63 → 126" (official PDF), 0x01/0x03 → 10.5
        /// (style), 0x4B → 10.5' (⚠️ only overridden samples). 0x01 = leading zero (⚠️). Sub-unit decimals are shown
        /// without trailing zeros (PDF: 40'-0" at 3-decimal accuracy). The 10' 6" (format 5) delimiter bit is not identified.
        /// </summary>
        private static void ApplyFormatFlags(DimensionElement d)
        {
            int f = d.FormatFlags;
            bool sub = (f & 0x20) != 0, mu = (f & 0x80) != 0, label = (f & 0x40) != 0;
            d.LeadingZero = (f & 0x01) != 0;
            d.TrailingZeros = false;
            d.ValueFormat = sub
                ? (mu ? (label ? DimensionValueFormat.MULabelDashSULabel : DimensionValueFormat.MUDashSU)
                      : (label ? DimensionValueFormat.SULabel : DimensionValueFormat.SU))
                : (label ? DimensionValueFormat.MULabel : DimensionValueFormat.MU);
        }

        /// <summary>
        /// 0x56DE dimension extension linkage: u16 sub-type at +0. Type 0 → annotation scale (f64 @ +12);
        /// type 5 → one run of a rich text-block override (string after the run's position/size doubles).
        /// </summary>
        private static void DecodeExtensionLinkages(DimensionElement d)
        {
            var runs = new List<(string Text, bool LineBreak)>();
            foreach (var l in d.Linkages)
            {
                if (l.PrimaryId != DimExtensionLinkage || l.Data.Length < 20) continue;
                var b = l.Data;
                int sub = BitConverter.ToUInt16(b, 0);
                if (sub == 0)
                {
                    double s = BitConverter.ToDouble(b, 12);
                    if (s > 1e-9 && s < 1e9) d.AnnotationScale = s;
                }
                else if (sub == 6 && b.Length == 52)
                {
                    // note settings; byte 44 = note terminator override (0 = style default, 4 = dot) — oracle-verified on 2 files
                    int t = b[44] & 0x0F;
                    if (t >= 1 && t <= 4) d.NoteTerminator = (DimensionTerminator)t;
                    d.CurvedLeader = (b[45] & 0x80) != 0; // curve leaders on 2 files, straight on 1
                }
                else if (sub == 5 && b.Length > 20)
                {
                    // u32 field mask @8 (bit 2 = run offset x,y present), u16 @12, u16 font @14, then 4 (+2) doubles,
                    // then the run string (optional FF FE 01 00 / FF FD prefix) and run properties.
                    uint mask = BitConverter.ToUInt32(b, 8);
                    ushort kind = BitConverter.ToUInt16(b, 12);
                    int so = 18 + 8 * (4 + ((mask & 0x4) != 0 ? 2 : 0));
                    string? s = RunString(b, so, out int nul);
                    if (s == null) continue;
                    if (kind == 0x0007 && runs.Count == 0 && s.Trim().Length == 0) continue; // text-block header run
                    bool lineBreak = nul + 9 < b.Length && (b[nul + 9] & 0x80) != 0;
                    runs.Add((s, lineBreak));
                }
            }
            if (runs.Count == 0) return;
            // Each run is stored with one trailing separator space; property byte (NUL + 9) bit 0x80 ends the line.
            var cur = new StringBuilder();
            for (int i = 0; i < runs.Count; i++)
            {
                var (text, lineBreak) = runs[i];
                cur.Append(text.EndsWith(" ", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1) : text);
                if (lineBreak || i == runs.Count - 1) { d.TextBlockRuns.Add(cur.ToString()); cur.Clear(); }
            }
        }

        private static string? RunString(byte[] b, int so, out int nul)
        {
            nul = -1;
            if (so + 1 >= b.Length) return null;
            if (b[so] == 0xFF && b[so + 1] == 0xFD)
            {
                var sb = new StringBuilder();
                for (int p = so + 2; p + 1 < b.Length; p += 2)
                {
                    char c = (char)(b[p] | (b[p + 1] << 8));
                    if (c == 0) { nul = p + 1; return V8ElementDecoder.MapRscFractions(sb.ToString()); }
                    sb.Append(c);
                }
                return null;
            }
            if (so + 3 < b.Length && b[so] == 0xFF && b[so + 1] == 0xFE && b[so + 2] == 0x01 && b[so + 3] == 0x00) so += 4;
            int n = 0;
            while (so + n < b.Length && b[so + n] != 0) n++;
            if (so + n >= b.Length) return null;
            nul = so + n;
            // runs may end with a 0x18 separator instead of the usual trailing space: drop control characters
            var s = LinkageParser.Decode8(b, so, n);
            var clean = new StringBuilder(s.Length);
            foreach (char c in s) if (c >= ' ') clean.Append(c);
            return V8ElementDecoder.MapRscFractions(clean.ToString());
        }

        // ------------------------------------------------------------------ values & text (needs model units)

        /// <summary>Computes segment values and display texts for every dimension of the file (model units known).</summary>
        public static void Finish(DgnFile file)
        {
            foreach (var m in file.Models)
                foreach (var d in m.AllElements().OfType<DimensionElement>())
                {
                    try { BuildSegments(d, m); }
                    catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException) { }
                }
        }

        internal static void BuildSegments(DimensionElement d, DgnModel m)
        {
            d.Segments.Clear(); d.Texts.Clear();
            int n = d.Points.Count;
            if (n < 2 || d.Kind == DimensionKind.Note) return;
            var x = d.XAxis;
            double metersPerUor = m.Units.UorPerMaster > 0 ? m.Units.MetersPerMaster / m.Units.UorPerMaster : 0;
            double unitsPerMeter = d.MasterUnitsPerMeter > 0 ? d.MasterUnitsPerMeter : (m.Units.MetersPerMaster > 0 ? 1.0 / m.Units.MetersPerMaster : 1);
            double subPerMaster = d.SubUnitsPerMeter > 0 && d.MasterUnitsPerMeter > 0 ? d.SubUnitsPerMeter / d.MasterUnitsPerMeter : m.Units.SubPerMaster;
            bool ordinal = d.Kind is DimensionKind.LocateSingle or DimensionKind.LocateStacked;

            for (int i = 1; i < n; i++)
            {
                int a = ordinal ? 0 : i - 1;
                var seg = new DimensionSegment { Index = i - 1, StartPoint = a, EndPoint = i };
                if (d.IsLinear)
                {
                    double uor = Math.Abs((d.Points[i] - d.Points[a]).Dot(x));
                    seg.Value = uor * metersPerUor * unitsPerMeter * d.Scale;
                    seg.FormattedValue = FormatLinear(seg.Value, d, subPerMaster);
                }
                seg.OverrideText = d.PointData[i].OverrideText;
                if (d.TextBlockRuns.Count > 0 && n == 2)
                {
                    seg.FromTextBlock = true;
                    foreach (var line in d.TextBlockRuns) seg.Lines.Add(Substitute(line, seg.FormattedValue));
                }
                else if (seg.OverrideText != null) seg.Lines.Add(Substitute(seg.OverrideText, seg.FormattedValue));
                else if (seg.FormattedValue != null) seg.Lines.Add(seg.FormattedValue);
                d.Segments.Add(seg);
                d.Texts.Add(seg.Text);
            }
        }

        private static string Substitute(string s, string? value) => value == null ? s.Replace("*", "") : s.Replace("*", value);

        /// <summary>Formats a linear value (dimension master units) per <see cref="DimensionElement.ValueFormat"/> and accuracy.</summary>
        internal static string FormatLinear(double v, DimensionElement d, double subPerMaster)
        {
            v = Math.Abs(v);
            string ml = d.MasterUnitLabel ?? "'", sl = d.SubUnitLabel ?? "\"";
            int acc = d.PrimaryAccuracy;
            switch (d.ValueFormat)
            {
                case DimensionValueFormat.MU: return Number(v, acc, d);
                case DimensionValueFormat.MULabel: return Number(v, acc, d) + ml;
                case DimensionValueFormat.SU: return Number(v * subPerMaster, acc, d);
                case DimensionValueFormat.SULabel: return Number(v * subPerMaster, acc, d) + sl;
            }
            // master + sub units: whole master units, remainder in sub units rounded to the accuracy
            double total = Round(v * subPerMaster, acc);
            double mu = Math.Floor(total / subPerMaster + 1e-9);
            double su = total - mu * subPerMaster;
            if (su < 0) su = 0;
            string sus = Number(su, acc, d, forceLeadingZero: true);
            string mus = mu.ToString("0", CultureInfo.InvariantCulture);
            return d.ValueFormat switch
            {
                DimensionValueFormat.MUDashSU => mus + "-" + sus,
                DimensionValueFormat.MULabelSULabel => mus + ml + " " + sus + sl,
                _ => mus + ml + "-" + sus + sl,
            };
        }

        private static bool IsDecimal(int acc) => acc == 0 || (acc & 0x80) != 0;

        private static int Decimals(int acc) => acc switch
        {
            0x81 => 1, 0x82 => 2, 0x84 => 3, 0x88 => 4, 0x90 => 5, 0xA0 => 6, 0xC0 => 7, 0x80 => 8, _ => 0,
        };

        private static int Denominator(int acc) => acc switch { 0x01 => 2, 0x02 => 4, 0x04 => 8, 0x08 => 16, 0x10 => 32, 0x20 => 64, _ => 1 };

        private static double Round(double v, int acc)
        {
            if (IsDecimal(acc)) { double s = Math.Pow(10, Decimals(acc)); return Math.Round(v * s, MidpointRounding.AwayFromZero) / s; }
            int den = Denominator(acc);
            return Math.Round(v * den, MidpointRounding.AwayFromZero) / den;
        }

        /// <summary>Number with decimal places or a reduced fraction ("6 1/2").</summary>
        private static string Number(double v, int acc, DimensionElement d, bool forceLeadingZero = false)
        {
            v = Round(v, acc);
            if (IsDecimal(acc))
            {
                int dec = Decimals(acc);
                string fmt = dec == 0 ? "0" : "0." + new string(d.TrailingZeros ? '0' : '#', dec);
                string s = v.ToString(fmt, CultureInfo.InvariantCulture);
                if (!d.LeadingZero && !forceLeadingZero && s.StartsWith("0.", StringComparison.Ordinal)) s = s.Substring(1);
                return s;
            }
            int den = Denominator(acc);
            long ticks = (long)Math.Round(v * den);
            long whole = ticks / den, num = ticks % den;
            if (num == 0) return whole.ToString(CultureInfo.InvariantCulture);
            long g = Gcd(num, den); long rn = num / g, rd = den / g;
            string frac = rn.ToString(CultureInfo.InvariantCulture) + "/" + rd.ToString(CultureInfo.InvariantCulture);
            return whole == 0 ? frac : whole.ToString(CultureInfo.InvariantCulture) + " " + frac;
        }

        private static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return Math.Max(1, a); }
    }
}
