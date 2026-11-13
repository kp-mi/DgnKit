using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V8
{
    internal static class V8Fonts
    {
        /// <summary>Font table entry (table level 2). Each entry maps a font number to a name; type inferred from name.</summary>
        /// <summary>
        /// Font table entry (table level 2): +32 u32 ?, +36 u32 ?, +40 u32 font number (TrueType ≥ 1024),
        /// +44 u16 name byte length, +46 UTF-16LE name.
        /// </summary>
        public static void DecodeFontTableEntry(DgnFile file, Element e, LeReader r)
        {
            uint number = r.U32(40);
            int len = r.U16(44);
            string name = len > 0 && len < 512 ? r.Utf16(46, len / 2) : (e.GetStringLinkage(1) ?? "");
            if (string.IsNullOrEmpty(name)) return;
            var kind = number >= 1024 ? FontKind.TrueType : name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ? FontKind.Shx : FontKind.Rsc;
            file.Fonts.ById[number] = new FontInfo { Id = number, Name = name, Kind = kind };
        }

    }

    internal static class V8LineStyles
    {
        /// <summary>
        /// Line style name table (level 7) entries:
        /// +64 u32 attribute flags (LSATTR_*: 0x80 physical units, low bits unit kind), +80 u64 id of the definition
        /// table entry (level 8), +88 u32 root component type (1 point symbol, 2 compound, 3 line code, 4 line point,
        /// 6 internal), +92 i32 style number (custom styles are negative), +96 UTF-16LE name (NUL terminated).
        /// </summary>
        public static void Resolve(DgnFile file, List<Element> nonModel)
        {
            var all = nonModel.SelectMany(x => x.SelfAndDescendants()).ToList();
            var defs = all
                .Where(x => x.RawType == (int)ElementType.TableEntry && x.LevelId == V8Tables.LineStyleDefTable && x.RawBytes != null)
                .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            var byId = new Dictionary<ulong, Element>();
            foreach (var x in all) if (!byId.ContainsKey(x.Id)) byId[x.Id] = x;
            var decoder = new V8LineStyleDefs(defs, byId);
            foreach (var e in all.Where(x => x.RawType == (int)ElementType.TableEntry))
            {
                if (e.LevelId != V8Tables.LineStyleNameTable || e.RawBytes == null) continue;
                var r = new LeReader(e.RawBytes);
                int number = r.I32(92);
                int end = (int)Math.Min(r.U32(8) * 2L, e.RawBytes.Length);
                string name = r.Utf16(96, Math.Max(0, (end - 96) / 2));
                if (string.IsNullOrEmpty(name)) name = e.GetStringLinkage(1) ?? "";
                uint flags = r.U32(64);
                var def = new LineStyleDefinition
                {
                    Number = number, Name = name, ElementId = e.Id, Attributes = flags,
                    Units = (LineStyleUnits)(flags & 0x3),
                    UnitsArePhysical = (flags & 0x80) != 0,
                };
                ulong defId = r.U64(80);
                uint compType = r.U32(88);
                def.Root = decoder.Decode(defId, 0)
                    ?? new LineStyleComponent { Kind = KindOf(compType), RscType = compType, Name = name, Description = name };
                if (!def.IsContinuous)
                {
                    def.DashPattern = LineStyleDefinition.FlattenDashes(LineStyleDefinition.MainStrokePattern(def.Root), out double phase);
                    def.DashPhase = phase;
                }
                file.LineStyles.Add(def);
            }
        }

        /// <summary>Definition units → master units of the default model (needs the model headers, so runs after they are read).</summary>
        public static void ApplyUnits(DgnFile file)
        {
            double uorPerMaster = file.DefaultModel?.Units.UorPerMaster ?? 0;
            foreach (var d in file.LineStyles.All)
                d.UnitScale = d.Units switch
                {
                    LineStyleUnits.Master => 1.0,
                    LineStyleUnits.Uor => uorPerMaster > 0 ? 1.0 / uorPerMaster : 0,
                    _ => 0,
                };
        }

        internal static LineStyleComponentKind KindOf(uint t) => t switch
        {
            1 => LineStyleComponentKind.PointSymbol, 2 => LineStyleComponentKind.Compound, 3 => LineStyleComponentKind.StrokePattern,
            4 => LineStyleComponentKind.LinePoint, 6 => LineStyleComponentKind.Internal, 7 => LineStyleComponentKind.RasterImage,
            _ => LineStyleComponentKind.Unknown,
        };
    }

    /// <summary>
    /// Line style definition entries (table level 8). Each entry is one component:
    /// <code>
    ///  +0x42 u16  LsElementType (1 point symbol, 2 compound, 3 line code, 4 line point, 6 internal, 7 raster)
    ///  +0x44 wchar[64] component name
    ///  +0xC4 u16  dependency count, +0xC6 u16 dependency byte size
    ///  +0xC8 u32  resource size, +0xCC resource bytes (LineStyleResource.r.h: LineCodeRsc / LineStyleRsc / LinePointRsc / PointSymRsc)
    ///  +0xCC+size u64[count] dependency element ids (sub-components in order; a point symbol's geometry cell)
    /// </code>
    /// Every resource starts with <c>char descr[128]; u32 lHdr1, lHdr2, auxType, auxID</c> (descr at +0xCC, body at +0x15C).
    /// Reserved fields often contain uninitialised memory and are ignored.
    /// </summary>
    internal sealed class V8LineStyleDefs
    {
        internal const int OffType = 0x42, OffName = 0x44, OffDepCount = 0xC4, OffRscSize = 0xC8, OffRsc = 0xCC;
        private const int DescrLen = 128, Body = OffRsc + DescrLen + 16; // 0x15C
        internal const uint RtLineStyle = 0x6C734C53, RtLineCode = 0x6C734C43, RtLinePoint = 0x6C734C50, RtPointSym = 0x6C385053, RtPointSymV7 = 0x6C735053;

        private readonly Dictionary<ulong, Element> _defs;
        private readonly Dictionary<ulong, Element> _nonModel;
        private readonly Dictionary<ulong, LineStyleComponent> _cache = new();

        public V8LineStyleDefs(Dictionary<ulong, Element> defs, Dictionary<ulong, Element> nonModelById)
        {
            _defs = defs;
            _nonModel = nonModelById;
        }

        public LineStyleComponent? Decode(ulong id, int depth)
        {
            if (id == 0 || depth > 8) return null;
            if (_cache.TryGetValue(id, out var cached)) return cached;
            if (!_defs.TryGetValue(id, out var e) || e.RawBytes == null || e.RawBytes.Length < OffRsc) return null;
            var r = new LeReader(e.RawBytes);
            uint type = r.U16(OffType);
            var c = new LineStyleComponent
            {
                Kind = V8LineStyles.KindOf(type), RscType = type, ElementId = id,
                Name = r.Utf16(OffName, 64),
                Description = r.Ansi(OffRsc, DescrLen),
            };
            _cache[id] = c;
            int nDeps = r.U16(OffDepCount);
            int rscSize = (int)Math.Min(r.U32(OffRscSize), (uint)Math.Max(0, r.Length - OffRsc));
            var rsc = r.Slice(OffRsc, rscSize);
            var deps = new List<ulong>();
            for (int i = 0; i < nDeps && r.Has(OffRsc + rscSize + i * 8, 8); i++) deps.Add(r.U64(OffRsc + rscSize + i * 8));
            if (rscSize >= DescrLen + 16)
            {
                c.RscType = rsc.U32(DescrLen + 8) is uint aux && aux != 0 ? aux : c.RscType;
                try
                {
                    switch (c.Kind)
                    {
                        case LineStyleComponentKind.StrokePattern:
                        case LineStyleComponentKind.Internal:
                            ReadLineCode(c, rsc);
                            break;
                        case LineStyleComponentKind.Compound:
                            ReadCompound(c, rsc, deps, depth);
                            break;
                        case LineStyleComponentKind.LinePoint:
                            ReadLinePoint(c, rsc, deps, depth);
                            break;
                        case LineStyleComponentKind.PointSymbol:
                            ReadPointSymbol(c, rsc, deps);
                            break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException) { /* keep what was read */ }
            }
            return c;
        }

        /// <summary>LineCodeRsc: +0x90 phase, +0xC0 options, +0xC4 maxIterate, +0xCC nStrokes, +0xD0 StrokeData[48 bytes].</summary>
        private static void ReadLineCode(LineStyleComponent c, LeReader rsc)
        {
            c.RscType = RtLineCode;
            c.Phase = rsc.F64(0x90);
            c.Options = rsc.U32(0xC0);
            c.MaxIterate = (int)rsc.U32(0xC4);
            int n = (int)Math.Min(rsc.U32(0xCC), 32u);
            for (int i = 0; i < n; i++)
            {
                int o = 0xD0 + i * 48;
                if (!rsc.Has(o, 48)) break;
                byte mode = rsc.U8(o + 44);
                c.Strokes.Add(new LineStyleStroke
                {
                    Length = rsc.F64(o), StartWidth = rsc.F64(o + 8), EndWidth = rsc.F64(o + 16),
                    StrokeMode = mode, IsDash = (mode & 1) != 0, WidthMode = rsc.U8(o + 45), CapMode = rsc.U8(o + 46),
                });
            }
        }

        /// <summary>LineStyleRsc: +0xAC nComp, +0xB0 ComponentInfo[56 bytes] {u32 type, u32 id, 4×u32, f64 offset, 3×f64}.</summary>
        private void ReadCompound(LineStyleComponent c, LeReader rsc, List<ulong> deps, int depth)
        {
            c.RscType = RtLineStyle;
            int n = (int)Math.Min(rsc.U32(0xAC), 64u);
            for (int i = 0; i < n; i++)
            {
                int o = 0xB0 + i * 56;
                if (!rsc.Has(o, 56)) break;
                var sub = i < deps.Count ? Decode(deps[i], depth + 1) : null;
                sub ??= new LineStyleComponent { Kind = KindOfRtype(rsc.U32(o)), RscType = rsc.U32(o), RscId = rsc.U32(o + 4) };
                if (sub.RscId == 0) sub.RscId = rsc.U32(o + 4);
                c.Children.Add(sub);
                c.ChildOffsets.Add(rsc.F64(o + 24));
            }
        }

        /// <summary>LinePointRsc: +0x98 lcType, +0x9C lcID, +0xA4 nSym, +0xA8 PointSymInfo[88 bytes]. Dependencies: line code, then symbols.</summary>
        private void ReadLinePoint(LineStyleComponent c, LeReader rsc, List<ulong> deps, int depth)
        {
            c.RscType = RtLinePoint;
            c.LineCode = deps.Count > 0 ? Decode(deps[0], depth + 1) : null;
            int n = (int)Math.Min(rsc.U32(0xA4), 64u);
            var symbolDeps = deps.Skip(1).ToList();
            var bySymId = new Dictionary<uint, LineStyleComponent?>();
            for (int i = 0; i < n; i++)
            {
                int o = 0xA8 + i * 88;
                if (!rsc.Has(o, 88)) break;
                uint symId = rsc.U32(o + 4);
                if (!bySymId.TryGetValue(symId, out var sym))
                {
                    // dependencies list each distinct symbol once, in order of first use
                    int k = bySymId.Count;
                    sym = k < symbolDeps.Count ? Decode(symbolDeps[k], depth + 1) : null;
                    if (sym != null && sym.RscId == 0) sym.RscId = symId;
                    bySymId[symId] = sym;
                }
                c.Symbols.Add(new LineStylePointSymbol
                {
                    Symbol = sym, StrokeNumber = rsc.U16(o + 8), Flags = rsc.U16(o + 10),
                    XOffset = rsc.F64(o + 16), YOffset = rsc.F64(o + 24), Angle = rsc.F64(o + 56) * Math.PI / 180.0,
                });
            }
        }

        /// <summary>PointSymRsc: +0x90 range (2 × DPoint3d), +0xD8 scale, +0x140 symFlags; geometry = dependency cell element.</summary>
        private void ReadPointSymbol(LineStyleComponent c, LeReader rsc, List<ulong> deps)
        {
            var rg = Geometry.Range3d.Empty;
            rg.Extend(new DPoint3d(rsc.F64(0x90), rsc.F64(0x98), rsc.F64(0xA0)));
            rg.Extend(new DPoint3d(rsc.F64(0xA8), rsc.F64(0xB0), rsc.F64(0xB8)));
            c.SymbolRange = rg;
            double scale = rsc.F64(0xD8);
            c.SymbolScale = scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale) ? scale : 1;
            c.SymbolFlags = rsc.U32(0x140);
            if (deps.Count > 0)
            {
                c.SymbolElementId = deps[0];
                if (_nonModel.TryGetValue(deps[0], out var cell)) c.SymbolElements.Add(cell);
            }
        }

        private static LineStyleComponentKind KindOfRtype(uint rt) => rt switch
        {
            RtLineCode => LineStyleComponentKind.StrokePattern,
            RtLineStyle => LineStyleComponentKind.Compound,
            RtLinePoint => LineStyleComponentKind.LinePoint,
            RtPointSym or RtPointSymV7 => LineStyleComponentKind.PointSymbol,
            0 => LineStyleComponentKind.Internal,
            _ => LineStyleComponentKind.Unknown,
        };
    }

    internal static class V8Mesh
    {
        /// <summary>Polyface mesh header (105) with matrix components (101 header, 102 int data, 103 double data).</summary>
        public static void Decode(MeshElement me)
        {
            // Layout reverse-engineering pending: keep components raw. Vertices are recovered heuristically from
            // double-data blocks (triplets) when the int-data block holds 1-based, 0-terminated face loops.
            try
            {
                var doubles = new List<double>();
                var ints = new List<int>();
                foreach (var m in me.Children)
                {
                    foreach (var c in m.SelfAndDescendants())
                    {
                        if (c.RawBytes == null) continue;
                        var r = new LeReader(c.RawBytes);
                        int end = (int)Math.Min(r.U32(4) * 2L, c.RawBytes.Length);
                        int attr = (int)r.U32(8) * 2; if (attr > 32 && attr < end) end = attr;
                        if (c.RawType == (int)ElementType.MatrixDoubleData)
                            for (int o = 32 + 8; o + 8 <= end; o += 8) doubles.Add(r.F64(o));
                        else if (c.RawType == (int)ElementType.MatrixIntData)
                            for (int o = 32 + 8; o + 4 <= end; o += 4) ints.Add(r.I32(o));
                    }
                }
                me.Extra["raw.doubleCount"] = doubles.Count;
                me.Extra["raw.intCount"] = ints.Count;
            }
            catch { /* best effort */ }
        }
    }

    internal static class PostProcess
    {
        public static void Run(DgnFile file, DgnReadOptions opt)
        {
            // Shared cell instances → definitions (matched by name, case-insensitive).
            var byName = new Dictionary<string, SharedCellDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in file.SharedCellDefinitions.Values)
                if (!string.IsNullOrEmpty(d.Name) && !byName.ContainsKey(d.Name!)) byName[d.Name!] = d;
            foreach (var m in file.Models)
                foreach (var sc in m.AllElements().OfType<SharedCellElement>())
                    if (sc.Name != null && byName.TryGetValue(sc.Name, out var def)) { sc.Definition = def; sc.DefinitionId = def.Id; }

            foreach (var m in file.Models)
                foreach (var e in m.AllElements())
                    Properties.MetadataDecoder.Decode(e, file);
            if (file.Format == DgnFormat.V8) V8Tags.AttachToTargets(file);
            if (file.Format == DgnFormat.V8) V8LineStyles.ApplyUnits(file);
            if (file.Format == DgnFormat.V8) V8Dimensions.Finish(file); // dimension values/texts need model units
            foreach (var e in file.NonModelElements.SelectMany(x => x.SelfAndDescendants()))
                if (e.XAttributes.Count > 0) Properties.MetadataDecoder.Decode(e, file);
        }
    }
}
