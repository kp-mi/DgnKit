using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Elements;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Formats.V8
{
    /// <summary>
    /// Decodes file-level tables from the non-model element stream (Dgn^Nm).
    /// Tables are TABLE_ELM (96) complex headers whose components are TABLE_ENTRY_ELM (95); the element "level" field
    /// holds the table kind (MS_LEVEL_TABLE_LEVEL = 1, fonts = 2, text styles = 3, dim styles = 5, mline styles = 6,
    /// line style names = 7, line style definitions = 8, level-name dictionary = 19 …).
    /// </summary>
    internal static class V8Tables
    {
        public const uint LevelTable = 1, FontTable = 2, TextStyleTable = 3, FilterTable = 4, DimStyleTable = 5,
            MlineStyleTable = 6, LineStyleNameTable = 7, LineStyleDefTable = 8, DictionaryTable = 9, RegAppTable = 10,
            ColorBookTable = 11, LevelNameDictionary = 19;

        public static void Process(DgnFile file, List<Element> nonModel)
        {
            foreach (var e in nonModel.SelectMany(x => x.SelfAndDescendants()))
            {
                switch (e.RawType)
                {
                    case (int)ElementType.TableEntry:
                        ProcessEntry(file, e);
                        break;
                    case (int)ElementType.SharedCellDefinition:
                        if (!file.SharedCellDefinitions.ContainsKey(e.Id)) file.SharedCellDefinitions[e.Id] = (SharedCellDefinition)e;
                        break;
                    case (int)ElementType.GroupData when e.LevelId == 1 && e.RawBytes != null:
                        TryColorTable(file, e);
                        break;
                    case (int)ElementType.DesignFileHeader:
                        TryExtendedColors(file, e);
                        break;
                }
            }
            if (file.Levels[64] == null) file.Levels.Add(new Level { Id = 64, Name = "Default" });
            V8LineStyles.Resolve(file, nonModel);
            V8Tags.ReadTagSets(file, nonModel.SelectMany(x => x.SelfAndDescendants()));
        }

        private static void ProcessEntry(DgnFile file, Element e)
        {
            var raw = e.RawBytes;
            if (raw == null) return;
            var r = new LeReader(raw);
            string name = e.GetStringLinkage(1) ?? "";
            string? descr = e.GetStringLinkage(2);
            uint id = r.U32(32);
            switch (e.LevelId)
            {
                case LevelTable:
                {
                    var lvl = new Level { Id = id, Name = name, Description = descr, ElementId = e.Id, ParentId = r.U32(36) };
                    V8LevelDecoder.Decode(lvl, r);
                    file.Levels.Add(lvl);
                    break;
                }
                case FontTable:
                    V8Fonts.DecodeFontTableEntry(file, e, r);
                    break;
                case TextStyleTable:
                    file.TextStyles.Add(new TextStyle { ElementId = e.Id, Id = id, Name = name });
                    break;
                case DimStyleTable:
                    file.DimensionStyles.Add(new NamedEntry { ElementId = e.Id, Number = id, Name = name, Description = descr });
                    break;
                case MlineStyleTable:
                    file.MultilineStyles.Add(new NamedEntry { ElementId = e.Id, Number = id, Name = name, Description = descr });
                    break;
            }
        }

        /// <summary>
        /// The TCB carries an XAttribute (UTF-16 XML) listing extended colours:
        /// &lt;ExtendedColors&gt;&lt;Entry Color="(r,g,b)" [Book=".." Name=".."]/&gt;…; colour index 256+i → entry i.
        /// </summary>
        private static void TryExtendedColors(DgnFile file, Element tcb)
        {
            foreach (var xa in tcb.XAttributes)
            {
                if (xa.Data.Length < 40 || xa.Data[0] != (byte)'<' || xa.Data[1] != 0) continue;
                string xml = System.Text.Encoding.Unicode.GetString(xa.Data).TrimEnd('\0');
                if (!xml.StartsWith("<ExtendedColors", StringComparison.Ordinal)) continue;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "Color=\"\\((\\d+),(\\d+),(\\d+)\\)\""))
                {
                    byte C(int i) => (byte)Math.Max(0, Math.Min(255, int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)));
                    file.ExtendedColors.Add(new RgbColor(C(1), C(2), C(3)));
                }
            }
        }

        private static void TryColorTable(DgnFile file, Element e)
        {
            // Color table element (type 5, level 1): Elm_hdr + Int16 screen_flag + 768 bytes RGB.
            var raw = e.RawBytes!;
            int o = V8ElementDecoder.ElmHdrSize + 2;
            if (raw.Length >= o + 768) file.Colors = ColorTable.FromDgnBytes(raw, o);
        }
    }

    /// <summary>
    /// Level table entry (TABLE_ENTRY_ELM, table level 1). Offsets (from element start) verified against MicroStation
    /// COM oracle dumps of FDOT/WSDOT/TxDOT files:
    /// <code>
    ///  +32 u32 level id        +36 u32 parent level id (0xFFFFFFFF none)
    ///  +40 u32 flags           bit21 displayed, bit22 plot
    ///  +44 u32 (creation stamp)
    ///  +48 i32 override style  +52 u32 override weight  +56 u32 override color  +60 u32 (override color copy)
    ///  +64 i32 style (ByLevel) +68 u32 weight (ByLevel) +72 u32 color (ByLevel) +76 u32 (color copy)
    ///  +92 u32 level code/number
    ///  linkages: string key 1 = name, key 2 = description
    /// </code>
    /// </summary>
    internal static class V8LevelDecoder
    {
        public static void Decode(Level lvl, LeReader r)
        {
            uint flags = r.U32(40);
            lvl.Properties["raw.flags40"] = flags;
            lvl.Properties["raw.u44"] = r.U32(44);
            for (int o = 48; o < 96; o += 4) lvl.Properties["raw.u" + o] = r.U32(o);
            lvl.Style = r.I32(64);
            lvl.Weight = r.U32(68);
            lvl.Color = r.U32(72);
            lvl.OverrideStyle = r.I32(48);
            lvl.OverrideWeight = r.U32(52);
            lvl.OverrideColor = r.U32(56);
            lvl.Code = r.U32(92);
            lvl.IsDisplayed = (flags & (1u << 21)) != 0;
            lvl.IsPlot = (flags & (1u << 22)) != 0;
            lvl.HasSymbology = true;
        }
    }
}
