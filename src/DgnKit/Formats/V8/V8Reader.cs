using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DgnKit.Cfb;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V8
{
    /// <summary>Reads DGN V8 / V8i / CONNECT Edition files (compound file container).</summary>
    public static class V8Reader
    {
        public static DgnFile Read(byte[] bytes, DgnReadOptions opt)
        {
            var c = new V8Container(bytes);
            var file = new DgnFile { Format = DgnFormat.V8, MajorVersion = 8 };
            ReadFileHeader(c, file);

            // ---- non-model (file level) elements: tables, shared cell definitions, tag sets …
            var nm = c.Cfb.Find("Dgn^Nm");
            var nmElements = ReadElements(c, nm, file, null, opt);
            AttachXAttributes(c, c.Cfb.Find("Dgn^NmA"), nmElements, file, opt);
            file.NonModelElements.AddRange(nmElements);
            V8Tables.Process(file, nmElements);

            // ---- models
            ReadModelGroup(c, "Dgn-Md", file, opt, dictionary: false);
            ReadModelGroup(c, "Dgn-Nd", file, opt, dictionary: true);

            foreach (var w in c.Warnings) file.Warn(w);
            V8GeoCoord.Read(file);
            PostProcess.Run(file, opt);
            return file;
        }

        private static void ReadFileHeader(V8Container c, DgnFile file)
        {
            var h = c.ReadZlibStream("Dgn~H");
            if (h != null && h.Length >= 0x11C)
            {
                var r = new LeReader(h);
                int major = (int)r.U32(0x114);
                if (major >= 8 && major < 100) file.MajorVersion = major;
                file.MinorVersion = (int)r.U32(0x118);
            }
        }

        private static void ReadModelGroup(V8Container c, string groupName, DgnFile file, DgnReadOptions opt, bool dictionary)
        {
            var group = c.Cfb.Find(groupName);
            if (group == null) return;
            foreach (var stg in c.Cfb.Children(group).Where(x => x.Type == CfbEntryType.Storage && x.Name.StartsWith("#", StringComparison.Ordinal))
                         .OrderBy(x => ParseHex(x.Name.Substring(1))))
            {
                var model = new DgnModel { Id = ParseHex(stg.Name.Substring(1)), File = file, IsDictionary = dictionary };
                ReadModelHeader(c, stg, model);

                var ctrl = ReadElements(c, c.Cfb.Child(stg, "Dgn^C"), file, model, opt);
                var gr = ReadElements(c, c.Cfb.Child(stg, "Dgn^G"), file, model, opt);
                AttachXAttributes(c, c.Cfb.Child(stg, "Dgn^CA"), ctrl, file, opt);
                AttachXAttributes(c, c.Cfb.Child(stg, "Dgn^GA"), gr, file, opt);
                model.ControlElements.AddRange(ctrl);
                model.Elements.AddRange(gr);
                foreach (var re in ctrl.OfType<ReferenceElement>()) model.References.Add(re.Attachment);

                if (!model.Is3d) model.Is3d = gr.Take(200).Any(e => e.IsGraphic && e.Is3d);
                if (string.IsNullOrEmpty(model.Name)) model.Name = dictionary ? $"Definition {model.Id}" : $"Model {model.Id}";
                file.Models.Add(model);
            }
        }

        private static int ParseHex(string s) =>
            int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : int.MaxValue;

        /// <summary>Decodes the model header stream (Dgn~Mh): model element (type 66, level 32) with units, name, flags.</summary>
        private static void ReadModelHeader(V8Container c, CfbEntry stg, DgnModel model)
        {
            var mhEntry = c.Cfb.Child(stg, "Dgn~Mh");
            if (mhEntry == null) return;
            var raw = c.Cfb.ReadStream(mhEntry);
            byte[] data;
            try { data = Zlib.Inflate(raw, 0, raw.Length); } catch { return; }
            // locate the model element: type 66 level 32 (search from 4096, then anywhere)
            int eo = -1;
            for (int o = data.Length > 0x1004 ? 0x1004 : 4; o + 32 <= data.Length; o += 4)
            {
                if (BitConverter.ToUInt16(data, o) == 66 && BitConverter.ToUInt32(data, o + 12) == 32) { eo = o; break; }
            }
            if (eo < 0) return;
            uint words = BitConverter.ToUInt32(data, eo + 4);
            int len = (int)Math.Min(words * 2L, data.Length - eo);
            var r = new LeReader(data, eo, len);
            var links = LinkageParser.ParseV8(r, (int)r.U32(8) * 2, len);
            string? Str(uint key) => links.OfType<StringLinkage>().FirstOrDefault(s => s.Key == key)?.Value;

            model.Name = Str(1) ?? "";
            model.Description = Str(2);
            // Model info linkage 0x56EE: u32, u32, f64, f64, f64 annotation scale, f64. MicroStation's line style scale
            // equals the annotation scale in these files (ModelInfo.GetLineStyleScale, mode 1); verified by the oracle.
            foreach (var l in links)
                if (l.IsUser && l.PrimaryId == LinkageIds.ModelInfo && l.Data.Length >= 32)
                {
                    double a = BitConverter.ToDouble(l.Data, 24);
                    if (a > 1e-9 && a < 1e9) model.AnnotationScale = model.LineStyleScale = a;
                    break;
                }
            // Unit block (relative to model element start): +0x40 u32 master flags, +0x48 u32 sub flags,
            // +0x4C u32 storage flags, +0x50/+0x58 master num/den, +0x60/+0x68 sub num/den (units per meter = num/den).
            uint mFlags = r.U32(0x40), sFlags = r.U32(0x48), stFlags = r.U32(0x4C);
            var master = new UnitDefinition(mFlags, r.F64(0x50), r.F64(0x58), Str(19) ?? "");
            var sub = new UnitDefinition(sFlags, r.F64(0x60), r.F64(0x68), Str(20) ?? "");
            // +0x70..0x87 global origin (3 doubles, UOR), +0x88 last modified, +0x90..0xBF model range (6 × i64 UOR),
            // +0xE0 UORs per storage unit, +0xE8/+0xF0 storage unit num/den.
            var storage = new UnitDefinition(stFlags, r.F64(0xE8), r.F64(0xF0), "");
            double uorPerStorage = r.F64(0xE0);
            var units = new UnitInfo
            {
                Master = Valid(master) ? master : UnitInfo.Default.Master,
                Sub = Valid(sub) ? sub : UnitInfo.Default.Sub,
                Storage = Valid(storage) ? storage : UnitInfo.Default.Storage,
                UorPerStorage = uorPerStorage > 0 && !double.IsNaN(uorPerStorage) && uorPerStorage < 1e12 ? uorPerStorage : 10000,
            };
            model.Units = units;
            // Global origin (UORs) at +0x70/+0x78/+0x80 — verified against MicroStation ModelReference.GlobalOrigin.
            var go = new DPoint3d(r.F64(0x70), r.F64(0x78), r.F64(0x80));
            model.GlobalOrigin = go.IsFinite && go.Length < 1e15 ? go : DPoint3d.Zero;
            uint typeWord = r.U32(0x24);
            model.Type = DgnModelType.Normal;
            if (typeWord >= 1 && typeWord <= 5) model.Type = DgnModelType.Normal; // refined in PostProcess via sheet heuristics
            model.Properties["header.typeWord"] = typeWord;
            model.Properties["header.flags"] = r.U32(0x34);
        }

        private static bool Valid(UnitDefinition u) => u.Numerator > 0 && u.Denominator > 0 && !double.IsNaN(u.Numerator) && u.Numerator < 1e12 && u.Denominator < 1e12;

        /// <summary>Reads and assembles (complex headers ⇒ children) the elements of a blocked element storage.</summary>
        internal static List<Element> ReadElements(V8Container c, CfbEntry? storage, DgnFile file, DgnModel? model, DgnReadOptions opt)
        {
            var result = new List<Element>();
            if (storage == null) return result;
            var data = c.ReadBlockedStorage(storage);
            var stack = new Stack<(Element Header, long Remaining)>();
            foreach (var (off, len) in V8Container.SplitElements(data, c.Warnings))
            {
                Element e;
                try
                {
                    int t = BitConverter.ToUInt16(data, off);
                    bool keep = opt.KeepRawBytes || t is 21 or 26 or 28 or 101 or 102 or 103 or 105 or 33 or 36 or 37 or 38 or 39 or 95 or 96 or 106 or 107 or 66;
                    e = V8ElementDecoder.Decode(data, off, len, keep);
                }
                catch (Exception ex)
                {
                    c.Warnings.Add($"Element decode failed at {storage.Name}+{off}: {ex.Message}");
                    continue;
                }
                e.Model = model;
                file.ElementTypeCounts[e.RawType] = file.ElementTypeCounts.TryGetValue(e.RawType, out int n) ? n + 1 : 1;

                if (stack.Count > 0 && e.IsComponent)
                {
                    // componentCount = number of ALL descendant elements: attach to the innermost open header and
                    // charge every open ancestor.
                    var h = stack.Peek().Header;
                    h.Children.Add(e); e.Parent = h;
                    var open = stack.ToArray(); stack.Clear();
                    for (int i = open.Length - 1; i >= 0; i--) stack.Push((open[i].Header, open[i].Remaining - 1));
                }
                else
                {
                    if (stack.Count > 0)
                    {
                        c.Warnings.Add($"Complex element {stack.Peek().Header.Id} ended early ({stack.Peek().Remaining} components missing)");
                        stack.Clear();
                    }
                    if (e.IsComponent && model != null && result.Count > 0)
                        c.Warnings.Add($"Orphan component element {e.Id} (type {e.RawType})");
                    if (!e.IsDeleted || opt.IncludeDeleted) result.Add(e);
                }

                if (e.IsComplexHeader)
                {
                    long count = ComponentCount(e, data, off);
                    if (count > 0 && count < 10_000_000) stack.Push((e, count));
                }
                // unwind finished headers (inner ones finish first; outer ones are charged in lock-step)
                while (stack.Count > 0 && stack.Peek().Remaining <= 0) stack.Pop();
                if (stack.Count > 0 && stack.Any(s => s.Remaining <= 0))
                {
                    // inconsistent counts: drop finished outer headers too
                    var keep = stack.Where(s => s.Remaining > 0).Reverse().ToList(); stack.Clear();
                    foreach (var k in keep) stack.Push(k);
                }
            }

            foreach (var e in result) FinishComplex(e, opt);
            return result;
        }

        private static long ComponentCount(Element e, byte[] data, int off) =>
            BitConverter.ToUInt32(data, off + (e.IsGraphic ? V8ElementDecoder.GraphicBody : V8ElementDecoder.ElmHdrSize));

        private static void FinishComplex(Element e, DgnReadOptions opt)
        {
            foreach (var ch in e.Children) FinishComplex(ch, opt);
            switch (e)
            {
                case BSplineCurveElement bc:
                    V8ElementDecoder.DecodeBSplineComponents(bc,
                        bc.Children.Where(x => x.RawBytes != null).Select(x => (x, x.RawBytes!, 0, x.RawBytes!.Length)).ToList());
                    break;
                case MeshElement me:
                    V8Mesh.Decode(me);
                    break;
            }
            if (!opt.KeepRawBytes && e.RawType is 21 or 26 or 28 or 101 or 102 or 103) e.RawBytes = null;
        }

        private static void AttachXAttributes(V8Container c, CfbEntry? storage, List<Element> elements, DgnFile file, DgnReadOptions opt)
        {
            if (!opt.ReadXAttributes || storage == null) return;
            var data = c.ReadBlockedStorage(storage);
            if (data.Length == 0) return;
            var map = V8Container.ReadXAttributes(data, c.Warnings);
            if (map.Count == 0) return;
            foreach (var e in elements.SelectMany(x => x.SelfAndDescendants()))
                if (map.TryGetValue(e.Id, out var xa)) e.XAttributes.AddRange(xa);
        }
    }
}
