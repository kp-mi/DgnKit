using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V8
{
    /// <summary>
    /// Tags (type 37) and tag set definitions (type 39 DgnStore headers). Verified against MicroStation oracle values.
    /// <code>
    /// Tag set (39, non-graphic): +0x34 u32 byte length of tag list, list from +0x3C; set name in string linkage key 1.
    ///   per tag: name\0, u16 id, prompt\0, u16 type (1 char, 2 short, 3 long, 4 double, 5 binary), u16 value length,
    ///            3 bytes flags, default value (char: NUL-terminated; numeric: fixed size).
    /// Tag (37, graphic): +0x98 u16 font, +0x9A u16 justification, +0xA0 origin (3 doubles),
    ///   +0xD0 u16 tag id, +0xF0/+0xF8 length/height multipliers, +0x138 u32 value byte length, +0x140 value.
    ///   linkage 0x56D0 key 10007 (+8 u64) → tag set element id; linkage 0x56D0 key 10000 (+16 u64) → target element id.
    /// </code>
    /// </summary>
    internal static class V8Tags
    {
        public static void ReadTagSets(DgnFile file, IEnumerable<Element> nonModel)
        {
            foreach (var e in nonModel.Where(x => x.RawType == (int)ElementType.DgnStoreHeader && x.RawBytes != null))
            {
                var r = new LeReader(e.RawBytes!);
                if (r.Ansi(0x28, 4) != "teSt") continue;
                var set = new TagSetDefinition { Name = e.GetStringLinkage(1) ?? ("TagSet" + e.Id), ElementId = e.Id };
                int len = (int)r.U32(0x34);
                int p = 0x3C, end = Math.Min(r.Length, p + len);
                int guard = 0;
                while (p < end && guard++ < 1000)
                {
                    string name = CStr(r, ref p, end);
                    if (p + 2 > end) break;
                    int id = r.U16(p); p += 2;
                    string prompt = CStr(r, ref p, end);
                    if (p + 7 > end) break;
                    int type = r.U16(p); int vlen = r.U16(p + 2); p += 7;
                    object? def = ReadValue(r, ref p, end, type, vlen);
                    set.Tags.Add(new TagDefinition { Id = id, Name = name, Prompt = prompt, Type = type, DefaultValue = def });
                }
                file.TagSets[set.Name] = set;
                file.TagSetsById[e.Id] = set;
            }
        }

        private static string CStr(LeReader r, ref int p, int end)
        {
            int s = p;
            while (p < end && r.U8(p) != 0) p++;
            string v = LinkageParser.Decode8(r.Buffer, r.BaseOffset + s, p - s);
            if (p < end) p++; // NUL
            return v;
        }

        private static object? ReadValue(LeReader r, ref int p, int end, int type, int vlen)
        {
            switch (type)
            {
                case 2: { short v = r.I16(p); p += 2; return (int)v; }
                case 3: { int v = r.I32(p); p += 4; return v; }
                case 4: { double v = r.F64(p); p += 8; return v; }
                case 5: { var b = r.Bytes(p, vlen); p += vlen; return Convert.ToBase64String(b); }
                default: return CStr(r, ref p, end);
            }
        }

        /// <summary>Decodes the tag element body and resolves set/definition names and the target element.</summary>
        public static void DecodeTag(TagElement t, DgnFile file)
        {
            var raw = t.RawBytes;
            if (raw == null) return;
            var r = new LeReader(raw);
            t.TagIndex = r.U16(0xD0);
            // +0xA0 origin (3 doubles, UOR); +0xF0/+0xF8 length/height multipliers (size × 1000/6)
            t.Origin = new DPoint3d(r.F64(0xA0), r.F64(0xA8), r.F64(0xB0));
            t.TextWidth = r.F64(0xF0) * 6.0 / 1000.0;
            t.TextHeight = r.F64(0xF8) * 6.0 / 1000.0;
            t.FontId = r.U16(0x98);
            foreach (var l in t.Linkages.Where(x => x.PrimaryId == 0x56D0 && x.Data.Length >= 16))
            {
                var d = new LeReader(l.Data);
                ushort key = d.U16(0);
                if (key == 10007) t.TagSetId = d.U64(8);
                else if (key == 10000 && l.Data.Length >= 24) t.TargetId = d.U64(16);
            }
            TagSetDefinition? set = null;
            if (t.TagSetId != 0) file.TagSetsById.TryGetValue(t.TagSetId, out set);
            var def = set?.Tags.FirstOrDefault(x => x.Id == t.TagIndex);
            t.TagSetName = set?.Name;
            t.TagName = def?.Name ?? ("Tag" + t.TagIndex);
            int vlen = (int)r.U32(0x138);
            int type = def?.Type ?? 1;
            int p = 0x140;
            int end = Math.Min(raw.Length, (int)r.U32(8) * 2 > 0x140 ? (int)r.U32(8) * 2 : raw.Length);
            t.Value = type switch
            {
                2 => (object)(int)r.I16(p),
                3 => r.I32(p),
                4 => r.F64(p),
                5 => Convert.ToBase64String(r.Bytes(p, vlen)),
                _ => LinkageParser.TrimNul(LinkageParser.Decode8(raw, p, Math.Max(0, Math.Min(vlen, end - p)))),
            };
            t.Displayed = !t.IsInvisible;
        }

        /// <summary>Attaches tag values to their target elements as a property group per tag set.</summary>
        public static void AttachToTargets(DgnFile file)
        {
            foreach (var m in file.Models)
            {
                var byId = new Dictionary<ulong, Element>();
                foreach (var e in m.Elements) if (!byId.ContainsKey(e.Id)) byId[e.Id] = e;
                foreach (var t in m.AllElements().OfType<TagElement>())
                {
                    DecodeTag(t, file);
                    if (t.TargetId == 0 || !byId.TryGetValue(t.TargetId, out var target)) continue;
                    string gname = t.TagSetName ?? "Tags";
                    var g = target.PropertyGroups.FirstOrDefault(x => x.Source == "Tag" && x.Name == gname);
                    if (g == null) { g = new PropertyGroup { Name = gname, Source = "Tag" }; target.PropertyGroups.Add(g); }
                    g.Properties[t.TagName ?? ("Tag" + t.TagIndex)] = t.Value;
                }
            }
        }
    }
}
