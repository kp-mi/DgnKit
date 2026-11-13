using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V7
{
    /// <summary>
    /// Reader for MicroStation V7 / IGDS (ISFF) design files: a flat sequence of 16-bit-word elements,
    /// integer UOR coordinates in VAX (middle-endian) order and VAX D-float doubles.
    /// </summary>
    public static class V7Reader
    {
        public static DgnFile Read(byte[] d, DgnReadOptions opt)
        {
            var file = new DgnFile { Format = DgnFormat.V7, MajorVersion = 7 };
            var model = new DgnModel { Id = 0, Name = "Default", File = file };
            file.Models.Add(model);
            var stack = new Stack<(Element H, int RemainingWords)>();
            int o = 0;
            while (o + 4 <= d.Length)
            {
                if (d[o] == 0xFF && d[o + 1] == 0xFF) break; // end of design marker
                int wtf = d[o + 2] | (d[o + 3] << 8);
                int len = (wtf + 2) * 2;
                if (len < 4 || o + len > d.Length) { file.Warn($"V7: truncated element at {o}"); break; }
                Element e;
                try { e = DecodeElement(d, o, len, model, file); }
                catch (Exception ex) { file.Warn($"V7: decode error at {o}: {ex.Message}"); o += len; continue; }
                file.ElementTypeCounts[e.RawType] = file.ElementTypeCounts.TryGetValue(e.RawType, out int n) ? n + 1 : 1;
                if (opt.KeepRawBytes) e.RawBytes = new LeReader(d, o, len).Bytes(0, len);

                if (e.RawType == (int)ElementType.DesignFileHeader && !model.Properties.ContainsKey("v7.masterUnits")) ReadTcb(d, o, len, file, model);
                else if (e.RawType == (int)ElementType.GroupData && e.LevelId == 1 && len >= 38 + 768) file.Colors = ColorTable.FromDgnBytes(d, o + 38);

                // complex assembly: headers carry total words of their components
                if (stack.Count > 0 && e.IsComponent)
                {
                    var h = stack.Peek().H;
                    h.Children.Add(e); e.Parent = h;
                    // every open ancestor's word budget includes this component
                    var arr = stack.ToArray(); stack.Clear();
                    for (int i = arr.Length - 1; i >= 0; i--) stack.Push((arr[i].H, arr[i].RemainingWords - len / 2));
                }
                else
                {
                    stack.Clear();
                    if (!e.IsDeleted || opt.IncludeDeleted)
                    {
                        // Shared cell definitions are dictionary content, never drawn in place.
                        if (e.RawType == (int)ElementType.SharedCellDefinition) file.NonModelElements.Add(e);
                        else if (e.IsGraphic) model.Elements.Add(e); else model.ControlElements.Add(e);
                    }
                }
                if (e.IsComplexHeader && e.Extra.TryGetValue("v7.totlength", out var tl) && tl is int totWords)
                {
                    int remaining = totWords - (len / 2 - 19); // totlength counts words after the first 19 header words
                    if (remaining > 0) stack.Push((e, remaining));
                }
                while (stack.Count > 0 && stack.Peek().RemainingWords <= 0) stack.Pop();
                o += len;
            }
            foreach (var e in model.Elements) FinishComplex(e);
            model.Is3d = file.Models[0].Is3d;
            DgnKit.Formats.V8.PostProcess.Run(file, opt);
            return file;
        }

        // ------------------------------------------------------------------ VAX helpers
        internal static int I32(byte[] p, int o) => p[o + 2] | (p[o + 3] << 8) | (p[o] << 16) | (p[o + 1] << 24);
        internal static ushort U16(byte[] p, int o) => (ushort)(p[o] | (p[o + 1] << 8));

        /// <summary>VAX D-float (as stored in DGN, 16-bit little-endian words) â†’ IEEE double.</summary>
        internal static double VaxD(byte[] p, int o)
        {
            uint hi = (uint)(p[o + 2] | (p[o + 3] << 8) | (p[o] << 16) | (p[o + 1] << 24));
            uint lo = (uint)(p[o + 6] | (p[o + 7] << 8) | (p[o + 4] << 16) | (p[o + 5] << 24));
            uint sign = hi & 0x80000000u;
            uint exponent = (hi >> 23) & 0xFF;
            if (exponent != 0) exponent = exponent - 129 + 1023;
            uint rnd = lo & 0x7;
            lo = (lo >> 3) | (hi << 29);
            if (rnd != 0) lo |= 1;
            hi = (hi >> 3) & 0x000FFFFF;
            hi |= (exponent << 20) | sign;
            if (exponent == 0) return 0.0;
            ulong bits = ((ulong)hi << 32) | lo;
            return BitConverter.Int64BitsToDouble((long)bits);
        }

        private static DPoint3d Pt(byte[] d, int o, bool is3d) =>
            is3d ? new DPoint3d(I32(d, o), I32(d, o + 4), I32(d, o + 8)) : new DPoint3d(I32(d, o), I32(d, o + 4), 0);

        private static Matrix3 Quat(byte[] d, int o)
        {
            const double s = 2147483648.0;
            return Matrix3.FromQuaternion(I32(d, o) / s, I32(d, o + 4) / s, I32(d, o + 8) / s, I32(d, o + 12) / s);
        }

        private static double Deg(int v) => v / 360000.0 * Math.PI / 180.0;

        private static void ReadTcb(byte[] d, int o, int len, DgnFile file, DgnModel model)
        {
            if (len < 1264) return;
            bool is3d = (d[o + 1214] & 0x40) != 0;
            model.Is3d = is3d;
            int subPerMaster = I32(d, o + 1112);
            int uorPerSub = I32(d, o + 1116);
            string mu = Encoding.ASCII.GetString(d, o + 1120, 2).TrimEnd('\0', ' ');
            string su = Encoding.ASCII.GetString(d, o + 1122, 2).TrimEnd('\0', ' ');
            if (subPerMaster <= 0) subPerMaster = 1;
            if (uorPerSub <= 0) uorPerSub = 1;
            // V7 has no physical unit definition; express master as "1 unit" and use UOR/master directly.
            model.Units = new UnitInfo
            {
                Master = new UnitDefinition(0, 1, 1, mu),
                Sub = new UnitDefinition(0, subPerMaster, 1, su),
                Storage = new UnitDefinition(0, 1, 1, mu),
                UorPerStorage = (double)uorPerSub * subPerMaster,
            };
            // TCB global origin (UOR, signed space) — equals MicroStation's ModelReference.GlobalOrigin (oracle-verified on MB96).
            var tgo = new DPoint3d(VaxD(d, o + 1240), VaxD(d, o + 1248), VaxD(d, o + 1256));
            model.GlobalOrigin = tgo;
            model.Properties["v7.masterUnits"] = mu;
            model.Properties["v7.subUnits"] = su;
        }

        private static Element DecodeElement(byte[] d, int o, int len, DgnModel model, DgnFile file)
        {
            int level = d[o] & 0x3F;
            bool complex = (d[o] & 0x80) != 0;
            int type = d[o + 1] & 0x7F;
            bool deleted = (d[o + 1] & 0x80) != 0;
            bool is3d = model.Is3d;
            bool graphic = IsGraphicType(type) && len >= 36;

            Element e = type switch
            {
                3 => new LineElement(),
                4 or 6 or 11 or 22 => new LineStringElement(),
                15 or 16 => new ArcElement(),
                17 => new TextElement(),
                7 => new TextNodeElement(),
                12 or 14 => new ComplexElement(),
                2 => new CellElement(),
                27 => new BSplineCurveElement(),
                23 => new ConeElement(),
                37 => new TagElement(),
                _ => new GenericElement(),
            };
            e.RawType = type; e.LevelId = (uint)level; e.IsComponent = complex; e.IsDeleted = deleted; e.Model = model;
            e.Id = (ulong)o; // V7 has no element ids: use file offset
            e.IsGraphic = graphic; e.Is3d = is3d;
            if (graphic)
            {
                var rg = Range3d.Empty;
                rg.Extend(new DPoint3d(UnsignedRange(d, o + 4), UnsignedRange(d, o + 8), UnsignedRange(d, o + 12)));
                rg.Extend(new DPoint3d(UnsignedRange(d, o + 16), UnsignedRange(d, o + 20), UnsignedRange(d, o + 24)));
                e.StoredRange = rg;
                e.GraphicGroup = U16(d, o + 28);
                int attIndx = U16(d, o + 30);
                ushort props = U16(d, o + 32);
                e.Class = (ElementClass)(props & 0xF);
                e.IsHole = (props & 0x8000) != 0;
                e.IsSnappable = (props & 0x4000) == 0;
                e.IsPlanar = (props & 0x2000) != 0;
                e.Style = d[o + 34] & 0x7;
                e.Weight = (uint)((d[o + 34] & 0xF8) >> 3);
                e.Color = d[o + 35];
                int attrStart = 32 + attIndx * 2;
                if (attIndx > 0 && attrStart < len) ParseV7Linkages(e, d, o + attrStart, o + len);
                DecodeBody(e, d, o, len, is3d);
            }
            else if (type == (int)ElementType.CellLibraryHeader || type == 9 || type == 10 || type == 8)
            {
                // control
            }
            if (e.IsComplexHeader == false && (type is 2 or 7 or 12 or 14 or 18 or 19 or 24 or 27 or 34)) e.IsComplexHeader = true;
            return e;
        }

        private static double UnsignedRange(byte[] d, int o) => (double)((long)(uint)I32(d, o) - 0x80000000L);

        private static bool IsGraphicType(int t) => t switch
        {
            2 or 3 or 4 or 6 or 7 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 21 or 22 or 23 or 24 or 25 or 27 or 33 or 34 or 35 or 36 or 37 => true,
            _ => false,
        };

        private static void DecodeBody(Element e, byte[] d, int o, int len, bool is3d)
        {
            int ps = is3d ? 12 : 8;
            switch (e)
            {
                case LineElement l:
                    l.Start = Pt(d, o + 36, is3d); l.End = Pt(d, o + 36 + ps, is3d);
                    break;
                case LineStringElement ls:
                {
                    int n = U16(d, o + 36);
                    for (int i = 0; i < n && o + 38 + (i + 1) * ps <= o + len; i++) ls.Points.Add(Pt(d, o + 38 + i * ps, is3d));
                    break;
                }
                case ArcElement a when e.RawType == 15:
                    a.PrimaryAxis = VaxD(d, o + 36); a.SecondaryAxis = VaxD(d, o + 44);
                    if (is3d) { a.Rotation = Quat(d, o + 52); a.Origin = new DPoint3d(VaxD(d, o + 68), VaxD(d, o + 76), VaxD(d, o + 84)); }
                    else { a.Rotation = Matrix3.RotationZ(Deg(I32(d, o + 52))); a.Origin = new DPoint3d(VaxD(d, o + 56), VaxD(d, o + 64), 0); }
                    break;
                case ArcElement a:
                {
                    a.StartAngle = Deg(I32(d, o + 36));
                    int sw = I32(d, o + 40);
                    a.SweepAngle = (sw & unchecked((int)0x80000000)) != 0 ? -Deg(sw & 0x7FFFFFFF) : Deg(sw);
                    if (sw == 0) a.SweepAngle = 2 * Math.PI;
                    a.PrimaryAxis = VaxD(d, o + 44); a.SecondaryAxis = VaxD(d, o + 52);
                    if (is3d) { a.Rotation = Quat(d, o + 60); a.Origin = new DPoint3d(VaxD(d, o + 76), VaxD(d, o + 84), VaxD(d, o + 92)); }
                    else { a.Rotation = Matrix3.RotationZ(Deg(I32(d, o + 60))); a.Origin = new DPoint3d(VaxD(d, o + 64), VaxD(d, o + 72), 0); }
                    break;
                }
                case TextElement t:
                {
                    t.FontId = d[o + 36];
                    t.Justification = (TextJustification)d[o + 37];
                    t.Width = I32(d, o + 38) * 6.0 / 1000.0;
                    t.Height = I32(d, o + 42) * 6.0 / 1000.0;
                    int nOff, sOff;
                    if (is3d) { t.Rotation = Quat(d, o + 46); t.Origin = Pt(d, o + 62, true); nOff = 74; sOff = 76; }
                    else { t.Rotation = Matrix3.RotationZ(Deg(I32(d, o + 46))); t.Origin = Pt(d, o + 50, false); nOff = 58; sOff = 60; }
                    int nchars = d[o + nOff];
                    int avail = Math.Max(0, Math.Min(nchars, len - sOff));
                    if (avail >= 2 && d[o + sOff] == 0xFF && d[o + sOff + 1] == 0xFD)
                    {
                        // 16-bit text: FF FD then 16-bit chars (count includes marker)
                        var sb = new StringBuilder();
                        for (int i = 2; i + 1 < avail; i += 2) sb.Append((char)U16(d, o + sOff + i));
                        t.Text = sb.ToString(); t.IsUnicode = true;
                    }
                    else t.Text = DgnKit.Formats.V8.V8ElementDecoder.MapRscFractions(LeReader.Latin1.GetString(d, o + sOff, avail).TrimEnd('\0'));
                    // V7 stores no text extents: estimate them (used for the justification point)
                    t.StoredHeight = t.Height; t.StoredLength = t.Width * Math.Max(1, t.Text.Length);
                    break;
                }
                case TextNodeElement tn:
                    tn.NodeNumber = U16(d, o + 40);
                    tn.FontId = d[o + 44];
                    tn.Justification = (TextJustification)d[o + 45];
                    tn.LineSpacing = I32(d, o + 46);
                    tn.Width = I32(d, o + 50) * 6.0 / 1000.0;
                    tn.Height = I32(d, o + 54) * 6.0 / 1000.0;
                    if (is3d) { tn.Rotation = Quat(d, o + 58); tn.Origin = Pt(d, o + 74, true); }
                    else { tn.Rotation = Matrix3.RotationZ(Deg(I32(d, o + 58))); tn.Origin = Pt(d, o + 62, false); }
                    e.Extra["v7.totlength"] = (int)U16(d, o + 36);
                    break;
                case ComplexElement:
                    e.Extra["v7.totlength"] = (int)U16(d, o + 36);
                    break;
                case CellElement c:
                    e.Extra["v7.totlength"] = (int)U16(d, o + 36);
                    c.Name = Rad50(U16(d, o + 38)) + Rad50(U16(d, o + 40));
                    c.Name = c.Name.TrimEnd();
                    const double s = 2147483648.0;
                    if (is3d)
                    {
                        c.Transform = new Matrix3(I32(d, o + 76) / s, I32(d, o + 80) / s, I32(d, o + 84) / s,
                                                  I32(d, o + 88) / s, I32(d, o + 92) / s, I32(d, o + 96) / s,
                                                  I32(d, o + 100) / s, I32(d, o + 104) / s, I32(d, o + 108) / s);
                        c.Origin = Pt(d, o + 112, true);
                    }
                    else
                    {
                        c.Transform = new Matrix3(I32(d, o + 68) / s, I32(d, o + 72) / s, 0, I32(d, o + 76) / s, I32(d, o + 80) / s, 0, 0, 0, 1);
                        c.Origin = Pt(d, o + 84, false);
                    }
                    break;
                case BSplineCurveElement bc:
                    e.Extra["v7.totlength"] = (int)U16(d, o + 36);
                    bc.Order = d[o + 40] + 1;
                    bc.Closed = (d[o + 41] & 0x80) != 0;
                    bc.Rational = (d[o + 41] & 0x20) != 0;
                    break;
                case GenericElement when e.RawType is 18 or 19 or 24:
                    e.Extra["v7.totlength"] = (int)U16(d, o + 36);
                    break;
                case GenericElement when e.RawType == 21:
                {
                    int n = U16(d, o + 36);
                    var pts = new List<DPoint3d>();
                    for (int i = 0; i < n && o + 38 + (i + 1) * ps <= o + len; i++) pts.Add(Pt(d, o + 38 + i * ps, is3d));
                    e.Extra["v7.poles"] = pts;
                    break;
                }
            }
        }

        private static void FinishComplex(Element e)
        {
            foreach (var c in e.Children) FinishComplex(c);
            if (e is BSplineCurveElement bc)
                foreach (var c in bc.Children)
                    if (c.Extra.TryGetValue("v7.poles", out var p) && p is List<DPoint3d> pts) bc.Poles.AddRange(pts);
        }

        private const string Rad50Chars = " ABCDEFGHIJKLMNOPQRSTUVWXYZ$.?0123456789";
        private static string Rad50(ushort v)
        {
            var c = new char[3];
            c[2] = Rad50Chars[Math.Min(39, v % 40)]; v /= 40;
            c[1] = Rad50Chars[Math.Min(39, v % 40)]; v /= 40;
            c[0] = Rad50Chars[Math.Min(39, (int)v)];
            return new string(c);
        }

        private static void ParseV7Linkages(Element e, byte[] d, int start, int end)
        {
            int p = start; int guard = 0;
            while (p + 4 <= end && guard++ < 64)
            {
                ushort w0 = U16(d, p);
                if (w0 == 0) break;
                if ((d[p + 1] & 0x10) != 0)
                {
                    int words = (d[p] + 1);
                    int bytes = words * 2;
                    if (bytes < 4 || p + bytes > end) bytes = end - p;
                    ushort id = U16(d, p + 2);
                    var data = new LeReader(d).Bytes(p + 4, bytes - 4);
                    var l = LinkageParser.Create(id, true, (d[p + 1] & 0x80) != 0, (d[p + 1] & 0x40) != 0, data);
                    if (id == LinkageIds.ShapeFill && data.Length >= 6) e.Fill = new FillInfo { Color = data[4] };
                    e.Linkages.Add(l);
                    p += bytes;
                }
                else
                {
                    // DMRS: 4 words: [entity 12 bits][...] [mslink 24-bit]
                    var dl = new DatabaseLinkage
                    {
                        PrimaryId = LinkageIds.Dmrs,
                        EntityNumber = (uint)(d[p] | ((d[p + 1] & 0x0F) << 8)),
                        MsLink = (uint)(d[p + 2] | (d[p + 3] << 8) | (d[p + 6] << 16)),
                        Data = new LeReader(d).Bytes(p, 8),
                    };
                    e.Linkages.Add(dl);
                    p += 8;
                }
            }
        }
    }
}
