using System;
using System.Collections.Generic;
using System.Text;
using DgnKit.IO;

namespace DgnKit.Elements
{
    /// <summary>Well-known user data linkage primary ids.</summary>
    public static class LinkageIds
    {
        public const ushort Dmrs = 0x0000;
        public const ushort ShapeFill = 0x0041;
        public const ushort Xbase = 0x1971;
        public const ushort Informix = 0x3848;
        public const ushort Odbc = 0x5E62;
        public const ushort Oracle = 0x6091;
        public const ushort Ris = 0x71FB;
        public const ushort OleDb = 0x5800;
        public const ushort String = 0x56D2;        // keyed string linkage (names, descriptions, ref file names …)
        public const ushort ModelInfo = 0x56EE;
        public const ushort AssocId = 0x7D2F;
        public const ushort DoubleArray = 0x7D28;
        public const ushort TextAttributes = 0x80D4;
        public const ushort Thickness = 0x0003;
        public const ushort Pattern = 0x0047;  // V7 pattern linkage (hatch)
        public const ushort Symbology = 0x0021;  // V7 symbology / style params
        public const ushort LineStyleParams = 0x79F9;  // V8 element line style modifiers (scale, widths, phase)

        public static string NameOf(ushort id) => id switch
        {
            Dmrs => "DMRS", ShapeFill => "ShapeFill", Xbase => "XBase", Informix => "Informix", Odbc => "ODBC",
            Oracle => "Oracle", Ris => "RIS", OleDb => "OLEDB", String => "String", ModelInfo => "ModelInfo",
            AssocId => "AssocId", DoubleArray => "DoubleArray", TextAttributes => "TextAttributes", Pattern => "Pattern", LineStyleParams => "LineStyleParams",
            _ => "0x" + id.ToString("X4"),
        };
    }

    /// <summary>A user data linkage (attribute data appended to an element after attrOffset).</summary>
    public class Linkage
    {
        public ushort PrimaryId { get; internal set; }
        public bool IsUser { get; internal set; }
        public bool IsInfo { get; internal set; }
        public bool IsRemote { get; internal set; }
        public byte[] Data { get; internal set; } = Array.Empty<byte>();
        public string Name => LinkageIds.NameOf(PrimaryId);
        public override string ToString() => $"Linkage {Name} ({Data.Length} bytes)";
    }

    public sealed class StringLinkage : Linkage
    {
        public uint Key { get; internal set; }
        public string Value { get; internal set; } = "";
        public override string ToString() => $"String[{Key}]='{Value}'";
    }

    /// <summary>Database (DMRS / RIS / ODBC / Oracle …) linkage: entity number + MSLINK key.</summary>
    public sealed class DatabaseLinkage : Linkage
    {
        public uint EntityNumber { get; internal set; }
        public uint MsLink { get; internal set; }
        public override string ToString() => $"{Name} entity={EntityNumber} mslink={MsLink}";
    }

    internal static class LinkageParser
    {
        /// <summary>Parses V8 linkages from <paramref name="r"/>[start..end).</summary>
        public static List<Linkage> ParseV8(LeReader r, int start, int end)
        {
            var result = new List<Linkage>();
            int o = start;
            int guard = 0;
            while (o + 4 <= end && guard++ < 512)
            {
                ushort hdr = r.U16(o);
                ushort id = r.U16(o + 2);
                if (hdr == 0 && id == 0) break;
                int mant = hdr & 0xFF;
                int exp = (hdr >> 8) & 0xF;
                bool user = (hdr & 0x1000) != 0;
                bool remote = (hdr & 0x4000) != 0;
                bool info = (hdr & 0x8000) != 0;
                int words = exp == 0 ? mant + 1 : (mant + 1) << exp;
                int bytes = words * 2;
                if (!user)
                {
                    // Non-user (V7-style DMRS) linkage: fixed 4 words.
                    bytes = 8;
                }
                if (bytes < 4 || o + bytes > end) bytes = end - o;
                var data = r.Bytes(o + 4, bytes - 4);
                result.Add(Create(user ? id : LinkageIds.Dmrs, user, info, remote, data, hdr, id));
                o += bytes;
            }
            return result;
        }

        internal static Linkage Create(ushort id, bool user, bool info, bool remote, byte[] data, ushort rawHdr = 0, ushort rawId = 0)
        {
            Linkage l;
            if (user && id == LinkageIds.String && data.Length >= 8)
            {
                var r = new LeReader(data);
                uint key = r.U32(0);
                int len = (int)r.U32(4);
                l = new StringLinkage { Key = key, Value = DecodeDgnString(data, 8, Math.Min(len, data.Length - 8)) };
            }
            else if (!user)
            {
                // DMRS: [u16 hdr][u16 entity][u32 mslink] (V7: entity in low bits of first word)
                var r = new LeReader(data);
                l = new DatabaseLinkage { EntityNumber = rawId, MsLink = r.U32(0) };
            }
            else if (user && (id == LinkageIds.Odbc || id == LinkageIds.Oracle || id == LinkageIds.Ris || id == LinkageIds.Informix || id == LinkageIds.Xbase || id == LinkageIds.OleDb) && data.Length >= 8)
            {
                var r = new LeReader(data);
                l = new DatabaseLinkage { EntityNumber = r.U32(0), MsLink = r.U32(4) };
            }
            else l = new Linkage();
            l.PrimaryId = id; l.IsUser = user; l.IsInfo = info; l.IsRemote = remote; l.Data = data;
            return l;
        }

        /// <summary>
        /// Decodes a string as stored in V8 linkages/elements:
        /// 'FF FE 01 00' prefix → UTF-8 follows; 'FF FE' (other) → UTF-16LE follows; otherwise 8-bit locale text.
        /// </summary>
        public static string DecodeDgnString(byte[] d, int o, int n)
        {
            if (n <= 0 || o >= d.Length) return string.Empty;
            n = Math.Min(n, d.Length - o);
            // FF FD → UTF-16LE follows
            if (n >= 2 && d[o] == 0xFF && d[o + 1] == 0xFD)
                return TrimNul(Encoding.Unicode.GetString(d, o + 2, (n - 2) & ~1));
            // FF FE 01 00 → 8-bit text follows (UTF-8 when valid, otherwise the locale code page ≈ Latin-1)
            if (n >= 4 && d[o] == 0xFF && d[o + 1] == 0xFE && d[o + 2] == 0x01 && d[o + 3] == 0x00)
                return TrimNul(Decode8(d, o + 4, n - 4));
            if (n >= 2 && d[o] == 0xFF && d[o + 1] == 0xFE)
                return TrimNul(Encoding.Unicode.GetString(d, o + 2, (n - 2) & ~1));
            return TrimNul(Decode8(d, o, n));
        }

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>8-bit text: strict UTF-8 if it decodes, else Latin-1.</summary>
        public static string Decode8(byte[] d, int o, int n)
        {
            bool ascii = true;
            for (int i = 0; i < n; i++) if (d[o + i] >= 0x80) { ascii = false; break; }
            if (ascii) return LeReader.Latin1.GetString(d, o, n);
            try { return StrictUtf8.GetString(d, o, n); }
            catch (DecoderFallbackException) { return LeReader.Latin1.GetString(d, o, n); }
        }

        public static string TrimNul(string s)
        {
            int i = s.IndexOf('\0');
            return i >= 0 ? s.Substring(0, i) : s;
        }
    }
}
