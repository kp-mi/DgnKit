using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DgnKit.Elements;
using DgnKit.IO;
using DgnKit.Model;

namespace DgnKit.Properties
{
    /// <summary>A relationship decoded from an EC relationship XAttribute (e.g. civil "IsRepresentationOf" → feature).</summary>
    public sealed class EcRelationship
    {
        public string Schema { get; set; } = "";
        public string ClassName { get; set; } = "";
        public ulong TargetElementId { get; set; }
        public ushort TargetHandlerMajor { get; set; }
        public uint TargetXAttributeId { get; set; }
        public override string ToString() => $"{Schema}:{ClassName} → element {TargetElementId}";
    }

    /// <summary>
    /// Turns linkages and XAttributes into <see cref="PropertyGroup"/>s ("feature metadata"):
    /// string linkages, database (MSLINK) linkages, binary EC XML instances (civil/feature data), EC relationships.
    /// </summary>
    public static class MetadataDecoder
    {
        public static void Decode(Element e, DgnFile file)
        {
            // string linkages (except names already surfaced on the element)
            var strs = e.Linkages.OfType<StringLinkage>().ToList();
            if (strs.Count > 0)
            {
                var g = new PropertyGroup { Name = "Strings", Source = "StringLinkage" };
                foreach (var s in strs) g.Properties[StringKeyName(s.Key)] = s.Value;
                e.PropertyGroups.Add(g);
            }
            foreach (var db in e.Linkages.OfType<DatabaseLinkage>())
            {
                var g = new PropertyGroup { Name = "DatabaseLink", Source = "Database" };
                g.Properties["Type"] = db.Name;
                g.Properties["EntityNumber"] = db.EntityNumber;
                g.Properties["MSLINK"] = db.MsLink;
                e.PropertyGroups.Add(g);
            }

            foreach (var xa in e.XAttributes)
            {
                try
                {
                    if (BinaryEcXml.IsBecxml(xa.Data))
                    {
                        var node = BinaryEcXml.Decode(xa.Data);
                        if (node != null) e.PropertyGroups.Add(ToGroup(node, $"ECXA::{xa.Id}"));
                    }
                    else if (xa.HandlerMajor == XAttributeHandlers.EcInstance)
                    {
                        var rel = DecodeRelationship(xa.Data);
                        if (rel != null)
                        {
                            var g = new PropertyGroup { Name = rel.ClassName, Source = "ECRelationship", Schema = rel.Schema, ClassName = rel.ClassName };
                            g.Properties["TargetElementId"] = rel.TargetElementId;
                            g.Properties["TargetXAttributeId"] = rel.TargetXAttributeId;
                            e.PropertyGroups.Add(g);
                        }
                    }
                }
                catch (Exception ex)
                {
                    file.Warn($"Metadata decode failed for element {e.Id} xattr {xa}: {ex.Message}");
                }
            }
        }

        private static PropertyGroup ToGroup(EcXmlNode node, string localKey)
        {
            var g = new PropertyGroup { Name = node.Name, Source = "EC", ClassName = node.Name };
            if (node.Attributes.TryGetValue("xmlns", out var ns)) g.Schema = ns;
            g.Properties["$localKey"] = localKey;
            Flatten(node, "", g.Properties);
            return g;
        }

        private static void Flatten(EcXmlNode n, string prefix, Dictionary<string, object?> into)
        {
            foreach (var c in n.Children)
            {
                string key = prefix.Length == 0 ? c.Name : prefix + "." + c.Name;
                if (c.Children.Count > 0) Flatten(c, key, into);
                else
                {
                    if (into.ContainsKey(key))
                    {
                        int i = 1; while (into.ContainsKey(key + "[" + i + "]")) i++;
                        key = key + "[" + i + "]";
                    }
                    into[key] = c.Value;
                }
            }
        }

        /// <summary>
        /// EC relationship XAttribute (handler 0x5716): u32 ver, u32 size, u32 0, u8 u8, UTF-16 schema\0, u32 u32 u32 u8,
        /// UTF-16 class\0, u8, UTF-16 "ECXA::n"\0, … u64? target element id, u16 handler minor/major, u32 xattr id.
        /// </summary>
        public static EcRelationship? DecodeRelationship(byte[] d)
        {
            if (d.Length < 40) return null;
            int p = 14;
            string schema = ReadUtf16Z(d, ref p);
            if (schema.Length == 0 || !schema.All(ch => ch >= 0x20 && ch < 0x7F)) return null;
            // skip numeric block to next UTF-16 string start (heuristic: next ASCII letter followed by 0)
            p = NextUtf16String(d, p);
            if (p < 0) return null;
            string cls = ReadUtf16Z(d, ref p);
            var rel = new EcRelationship { Schema = schema, ClassName = cls };
            // find "ECXA::" key then the target record after it
            int q = NextUtf16String(d, p);
            if (q > 0)
            {
                string key = ReadUtf16Z(d, ref q);
                // after key: u32 len, i64 ?, ... target element id (u64) followed by u16 minor, u16 major, u32 id
                for (int o = q; o + 16 <= d.Length; o++)
                {
                    ushort major = BitConverter.ToUInt16(d, o + 10);
                    if (major == XAttributeHandlers.BinaryEcXml && BitConverter.ToUInt16(d, o + 8) == 0)
                    {
                        rel.TargetElementId = BitConverter.ToUInt64(d, o);
                        rel.TargetHandlerMajor = major;
                        rel.TargetXAttributeId = BitConverter.ToUInt32(d, o + 12);
                        break;
                    }
                }
            }
            return rel;
        }

        private static string ReadUtf16Z(byte[] d, ref int p)
        {
            var sb = new StringBuilder();
            while (p + 1 < d.Length)
            {
                char c = (char)(d[p] | (d[p + 1] << 8)); p += 2;
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int NextUtf16String(byte[] d, int p)
        {
            for (int i = p; i + 5 < d.Length; i++)
                if (IsAsciiLetter(d[i]) && d[i + 1] == 0 && (IsAsciiLetter(d[i + 2]) || d[i + 2] == '_') && d[i + 3] == 0) return i;
            return -1;
        }

        private static bool IsAsciiLetter(byte b) => (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');

        public static string StringKeyName(uint key) => key switch
        {
            0 => "Generic", 1 => "Name", 2 => "Description", 3 => "FileName", 4 => "LogicalName", 5 => "PatternCell",
            6 => "DimensionStyle", 8 => "Library", 9 => "ProfileName", 19 => "MasterUnitLabel", 20 => "SubUnitLabel",
            21 => "ModelName", 31 => "FullReferencePath", 33 => "XData", 38 => "DWGPatternName",
            _ => "Key" + key,
        };
    }
}
