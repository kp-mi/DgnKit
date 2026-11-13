using System;
using System.Collections.Generic;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.IO;

namespace DgnKit.Formats.V8
{
    /// <summary>
    /// Decodes a single V8 element record (MSElement layout from DgnPlatform/DgnFileIO/DgnElements.h).
    /// Elm_hdr (32 bytes) + Disp_hdr (72 bytes, graphics only) + type-specific body; linkages from attrOffset.
    /// </summary>
    internal static class V8ElementDecoder
    {
        public const int ElmHdrSize = 32;
        public const int DispHdrSize = 72;
        public const int GraphicBody = ElmHdrSize + DispHdrSize; // 104

        public static Element Decode(byte[] data, int offset, int length, bool keepRaw)
        {
            var r = new LeReader(data, offset, length);
            int type = r.U16(0);
            ushort flags = r.U16(2);
            uint sizeWords = r.U32(4);
            uint attrWords = r.U32(8);
            bool isGraphics = (flags & 0x1000) != 0;

            Element e = Create(type, isGraphics, r);
            e.RawType = type;
            e.IsNonModel = (flags & 0x0400) != 0;
            e.IsLocked = (flags & 0x0800) != 0;
            e.IsGraphic = isGraphics;
            e.IsComplexHeader = (flags & 0x2000) != 0;
            e.IsComponent = (flags & 0x4000) != 0;
            e.IsDeleted = (flags & 0x8000) != 0;
            e.LevelId = r.U32(12);
            e.Id = r.U64(16);
            double lastMod = r.F64(24);
            if (lastMod > 1e11 && lastMod < 1e14)
            {
                try { e.LastModified = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(lastMod); } catch { }
            }

            if (isGraphics && r.Has(0, GraphicBody))
            {
                e.GraphicGroup = r.U32(32);
                e.Priority = r.I32(36);
                ushort props = r.U16(40);
                e.Class = (ElementClass)(props & 0xF);
                e.IsInvisible = (props & 0x0080) != 0;
                e.Is3d = (props & 0x0800) != 0;
                e.IsPlanar = (props & 0x2000) != 0;
                e.IsSnappable = (props & 0x4000) == 0;
                e.IsHole = (props & 0x8000) != 0;
                e.Style = r.I32(44);
                e.Weight = r.U32(48);
                e.Color = r.U32(52);
                var rg = Range3d.Empty;
                rg.Extend(new DPoint3d(r.I64(56), r.I64(64), r.I64(72)));
                rg.Extend(new DPoint3d(r.I64(80), r.I64(88), r.I64(96)));
                e.StoredRange = rg;
            }

            int attrStart = (int)attrWords * 2;
            int end = (int)Math.Min(sizeWords * 2L, length);
            if (attrWords > 0 && attrStart < end && attrStart >= ElmHdrSize)
            {
                e.Linkages.AddRange(LinkageParser.ParseV8(r, attrStart, end));
                foreach (var l in e.Linkages)
                    if (l.IsUser && l.PrimaryId == LinkageIds.LineStyleParams) { e.StyleParams = LineStyleParams.FromV8Linkage(l.Data); break; }
            }

            try { DecodeBody(e, r, attrStart > 0 ? Math.Min(attrStart, end) : end); }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                // Leave partially decoded; the caller records diagnostics via element type counts.
            }

            if (keepRaw) e.RawBytes = r.Bytes(0, length);
            return e;
        }

        private static Element Create(int type, bool isGraphics, LeReader r) => (ElementType)type switch
        {
            ElementType.Line => new LineElement(),
            ElementType.LineString or ElementType.Shape or ElementType.Curve or ElementType.PointString or ElementType.Conic => new LineStringElement(),
            ElementType.Ellipse or ElementType.Arc => new ArcElement(),
            ElementType.Text => new TextElement(),
            ElementType.TextNode => new TextNodeElement(),
            ElementType.ComplexString or ElementType.ComplexShape => new ComplexElement(),
            ElementType.CellHeader => new CellElement(),
            ElementType.SharedCellDefinition => new SharedCellDefinition(),
            ElementType.SharedCell => new SharedCellElement(),
            ElementType.BSplineCurve => new BSplineCurveElement(),
            ElementType.Cone => new ConeElement(),
            ElementType.MeshHeader => new MeshElement(),
            ElementType.Dimension => new DimensionElement(),
            ElementType.Multiline => new MultilineElement(),
            ElementType.ReferenceAttachment => new ReferenceElement(),
            ElementType.Attribute => new TagElement(),
            ElementType.TableEntry or ElementType.Table => new TableEntryElement(),
            _ => new GenericElement(),
        };

        private static DPoint3d P2(LeReader r, int o) => new(r.F64(o), r.F64(o + 8), 0);
        private static DPoint3d P3(LeReader r, int o) => new(r.F64(o), r.F64(o + 8), r.F64(o + 16));

        private static Matrix3 Quat(LeReader r, int o) => Matrix3.FromQuaternion(r.F64(o), r.F64(o + 8), r.F64(o + 16), r.F64(o + 24));

        private static Matrix3 Mat3(LeReader r, int o) => new(
            r.F64(o), r.F64(o + 8), r.F64(o + 16),
            r.F64(o + 24), r.F64(o + 32), r.F64(o + 40),
            r.F64(o + 48), r.F64(o + 56), r.F64(o + 64));

        private static Matrix3 Mat2(LeReader r, int o) => new(
            r.F64(o), r.F64(o + 8), 0,
            r.F64(o + 16), r.F64(o + 24), 0,
            0, 0, 1);

        private static void DecodeBody(Element e, LeReader r, int bodyEnd)
        {
            const int B = GraphicBody;
            bool d3 = e.Is3d;
            switch (e)
            {
                case LineElement l:
                    if (d3) { l.Start = P3(r, B); l.End = P3(r, B + 24); }
                    else { l.Start = P2(r, B); l.End = P2(r, B + 16); }
                    break;

                case LineStringElement ls:
                {
                    uint n = r.U32(B);
                    int stride = d3 ? 24 : 16;
                    int max = (bodyEnd - (B + 8)) / stride;
                    if (n > max) n = (uint)Math.Max(0, max);
                    for (int i = 0; i < n; i++)
                        ls.Points.Add(d3 ? P3(r, B + 8 + i * 24) : P2(r, B + 8 + i * 16));
                    break;
                }

                case ArcElement a:
                    if (e.RawType == (int)ElementType.Arc)
                    {
                        a.StartAngle = r.F64(B);
                        a.SweepAngle = r.F64(B + 8);
                        a.PrimaryAxis = r.F64(B + 16);
                        a.SecondaryAxis = r.F64(B + 24);
                        if (d3) { a.Rotation = Quat(r, B + 32); a.Origin = P3(r, B + 64); }
                        else { a.Rotation = Matrix3.RotationZ(r.F64(B + 32)); a.Origin = P2(r, B + 40); }
                    }
                    else
                    {
                        a.PrimaryAxis = r.F64(B);
                        a.SecondaryAxis = r.F64(B + 8);
                        a.StartAngle = 0; a.SweepAngle = 2 * Math.PI;
                        if (d3) { a.Rotation = Quat(r, B + 16); a.Origin = P3(r, B + 48); }
                        else { a.Rotation = Matrix3.RotationZ(r.F64(B + 16)); a.Origin = P2(r, B + 24); }
                    }
                    break;

                case TextElement t:
                {
                    t.FontId = r.U32(B);
                    t.Justification = (TextJustification)r.U16(B + 4);
                    int nchars = r.U16(B + 6);
                    double lengthMult = r.F64(B + 8), heightMult = r.F64(B + 16);
                    t.StoredLength = r.F64(B + 24);
                    t.StoredHeight = r.F64(B + 32);
                    int strOff;
                    if (d3) { t.Rotation = Quat(r, B + 40); t.Origin = P3(r, B + 72); strOff = B + 96 + 2; }
                    else { t.Rotation = Matrix3.RotationZ(r.F64(B + 40)); t.Origin = P2(r, B + 48); strOff = B + 64 + 2; }
                    // V7/V8: stored multipliers are (size * 1000 / 6).
                    t.Width = lengthMult * 6.0 / 1000.0;
                    t.Height = heightMult * 6.0 / 1000.0;
                    int maxBytes = Math.Max(0, Math.Min(nchars, bodyEnd - strOff));
                    var bytes = r.Bytes(strOff, maxBytes);
                    t.Text = DecodeTextBytes(bytes, out bool uni);
                    t.IsUnicode = uni;
                    break;
                }

                case TextNodeElement tn:
                {
                    tn.NodeNumber = r.U32(B + 4);
                    tn.FontId = r.U32(B + 8);
                    tn.Justification = (TextJustification)r.U16(B + 14);
                    tn.LineSpacing = r.F64(B + 16);
                    tn.Width = r.F64(B + 24) * 6.0 / 1000.0;
                    tn.Height = r.F64(B + 32) * 6.0 / 1000.0;
                    if (d3) { tn.Rotation = Quat(r, B + 40); tn.Origin = P3(r, B + 72); }
                    else { tn.Rotation = Matrix3.RotationZ(r.F64(B + 40)); tn.Origin = P2(r, B + 48); }
                    break;
                }

                case CellElement c:
                {
                    ushort cflags = r.U16(B + 6);
                    c.IsAnnotation = (cflags & 1) != 0;
                    if (d3)
                    {
                        var lr = Range3d.Empty; lr.Extend(P3(r, B + 8)); lr.Extend(P3(r, B + 32)); c.LocalRange = lr;
                        c.Transform = Mat3(r, B + 56); c.Origin = P3(r, B + 128);
                    }
                    else
                    {
                        var lr = Range3d.Empty; lr.Extend(P2(r, B + 8)); lr.Extend(P2(r, B + 24)); c.LocalRange = lr;
                        c.Transform = Mat2(r, B + 40); c.Origin = P2(r, B + 72);
                    }
                    c.Name = e.GetStringLinkage(1);
                    c.Description = e.GetStringLinkage(2);
                    break;
                }

                case SharedCellDefinition scd:
                {
                    ushort cflags = r.U16(B + 6);
                    scd.IsAnnotation = (cflags & 1) != 0;
                    scd.Origin = d3 ? P3(r, B + 128) : P2(r, B + 72);
                    scd.Name = e.GetStringLinkage(1);
                    scd.Description = e.GetStringLinkage(2);
                    scd.IsPointCell = !e.IsSnappable;
                    break;
                }

                case SharedCellElement sc:
                {
                    if (d3) { sc.Transform = Mat3(r, B + 56); sc.Origin = P3(r, B + 128); }
                    else { sc.Transform = Mat2(r, B + 40); sc.Origin = P2(r, B + 72); }
                    sc.OverrideFlags = r.U16(B + 4);
                    sc.Name = e.GetStringLinkage(1);
                    break;
                }

                case BSplineCurveElement bc:
                {
                    uint f = r.U32(B + 4);
                    bc.Order = (int)(f & 0x1F) + 2;
                    bc.Rational = (f & 0x80) != 0;
                    bc.Closed = (f & 0x100) != 0;
                    bc.CurveType = (int)((f >> 9) & 0xFF);
                    bc.InteriorKnotsOnly = (f & (1u << 27)) == 0; // Bspline_flags.storeFullKnots
                    break;
                }

                case ConeElement cone:
                    cone.Rotation = Quat(r, B + 8);
                    cone.Center1 = P3(r, B + 40); cone.Radius1 = r.F64(B + 64);
                    cone.Center2 = P3(r, B + 72); cone.Radius2 = r.F64(B + 96);
                    break;

                case ReferenceElement re:
                    DecodeReference(re, r, bodyEnd);
                    break;

                case DimensionElement dim:
                    V8Dimensions.Decode(dim, r, bodyEnd);
                    break;
            }
        }

        /// <summary>Text bytes: 0xFF 0xFE prefix → UTF-16LE; otherwise 8-bit (Latin-1 / font codepage).</summary>
        internal static string DecodeTextBytes(byte[] b, out bool unicode)
        {
            unicode = b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFD;
            return MapRscFractions(LinkageParser.DecodeDgnString(b, 0, b.Length));
        }

        /// <summary>
        /// MicroStation RSC fonts encode stacked fractions in the 0x81–0x9F range (C1 control codes in Latin-1, so the
        /// mapping is unambiguous). Verified against MicroStation: 0x81 ½, 0x82 ¼, 0x83 ¾, 0x86 ⅝.
        /// </summary>
        internal static string MapRscFractions(string s)
        {
            bool any = false;
            foreach (char c in s) if (c >= '\u0081' && c <= '\u009F') { any = true; break; }
            if (!any) return s;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c < '\u0081' || c > '\u009F') { sb.Append(c); continue; }
                sb.Append(Fractions[c - 0x81]);
            }
            return sb.ToString();
        }

        private static readonly string[] Fractions =
        {
            "½", "¼", "¾", "⅛", "⅜", "⅝", "⅞",                                   // 0x81–0x87
            "1/16", "3/16", "5/16", "7/16", "9/16", "11/16", "13/16", "15/16",     // 0x88–0x8F
            "1/32", "3/32", "5/32", "7/32", "9/32", "11/32", "13/32", "15/32",     // 0x90–0x97
            "17/32", "19/32", "21/32", "23/32", "25/32", "27/32", "29/32", "31/32", // 0x98–0x9F
        };

        private static void DecodeReference(ReferenceElement re, LeReader r, int bodyEnd)
        {
            const int B = ElmHdrSize; // reference attachments are non-graphic (no Disp_hdr)
            var a = re.Attachment;
            a.ElementId = re.Id;
            a.FileNumber = r.U32(B + 12);
            uint fd = r.U32(B + 28);
            a.Displayed = (fd & 0x2) != 0;
            // priority u32 @+32, then 8 view-flag blocks of 12 bytes (+36..+132), u16 baseNestDepth, u16 → origins @+136
            a.ReferenceOrigin = P3(r, B + 136);
            a.MasterOrigin = P3(r, B + 160);
            a.Rotation = Mat3(r, B + 184);
            double scale = r.F64(B + 256);
            a.Scale = scale == 0 || double.IsNaN(scale) ? 1 : scale;
            a.NestDepth = r.U16(B + 328);
            int nclip = r.U16(B + 334);
            for (int i = 0; i < nclip && B + 336 + i * 16 + 16 <= bodyEnd; i++) a.ClipPolygon.Add(P2(r, B + 336 + i * 16));
            a.FileName = re.GetStringLinkage(3);
            a.ModelName = re.GetStringLinkage(21);
            a.LogicalName = re.GetStringLinkage(4);
            a.Description = re.GetStringLinkage(2);
            a.FullPath = re.GetStringLinkage(31);
        }

        /// <summary>Decodes component payloads that only make sense together with the header (bspline poles/knots/weights).</summary>
        public static void DecodeBSplineComponents(BSplineCurveElement bc, List<(Element E, byte[] Data, int Off, int Len)> comps)
        {
            foreach (var (ce, data, off, len) in comps)
            {
                var r = new LeReader(data, off, len);
                int end = (int)Math.Min(r.U32(4) * 2L, len);
                int attr = (int)r.U32(8) * 2; if (attr > 0 && attr < end) end = attr;
                switch (ce.RawType)
                {
                    case (int)ElementType.BSplinePole:
                    {
                        bool d3 = ce.Is3d;
                        uint n = r.U32(GraphicBody);
                        int stride = d3 ? 24 : 16;
                        int max = (end - (GraphicBody + 8)) / stride;
                        if (n > max) n = (uint)Math.Max(0, max);
                        for (int i = 0; i < n; i++) bc.Poles.Add(d3 ? P3(r, GraphicBody + 8 + i * 24) : P2(r, GraphicBody + 8 + i * 16));
                        break;
                    }
                    case (int)ElementType.BSplineKnot:
                        for (int o = ElmHdrSize; o + 8 <= end; o += 8) bc.Knots.Add(r.F64(o));
                        break;
                    case (int)ElementType.BSplineWeight:
                        for (int o = ElmHdrSize; o + 8 <= end; o += 8) bc.Weights.Add(r.F64(o));
                        break;
                }
            }
        }
    }
}
