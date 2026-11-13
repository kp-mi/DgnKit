using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DgnKit.Cfb;
using DgnKit.IO;

namespace DgnKit.Formats.V8
{
    /// <summary>
    /// Physical layout of a DGN V8 compound file (reverse-engineered):
    /// <code>
    ///  /Dgn~H                 file header (20-byte prefix + zlib)
    ///  /Dgn~S                 session info
    ///  /Dgn^Nm  , /Dgn^NmA    non-model (file-level) elements: TCB, level/font/linestyle tables, shared cell defs, tag sets…
    ///  /Dgn-Md/#xxxxxx/       one storage per model (hex model id):
    ///        Dgn~Mh           model header (4096-byte header + type-66 level-32 model element), zlib
    ///        Dgn^C  ,Dgn^CA   control elements and their XAttributes
    ///        Dgn^G  ,Dgn^GA   graphic elements and their XAttributes
    ///  /Dgn-Nd/#xxxxxx/       additional (non-design / dictionary-like) models with the same layout
    /// </code>
    /// Element streams are split into blocks named $1, $2 … each = 16-byte header {u32 count, u32 flags, 8 reserved} + zlib data.
    /// Decompressed element data is a sequence of {u32 reserved; element (Elm_hdr.elementSize words)}.
    /// XAttribute streams use the same block framing; contents are per-element records (see <see cref="ReadXAttributes"/>).
    /// </summary>
    internal sealed class V8Container
    {
        public CompoundFile Cfb { get; }
        public List<string> Warnings { get; } = new();

        public V8Container(byte[] bytes) { Cfb = new CompoundFile(bytes); }

        public static int BlockNumber(string name) =>
            name.Length > 1 && name[0] == '$' && int.TryParse(name.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

        /// <summary>Concatenated decompressed payload of all $n blocks in a storage.</summary>
        public byte[] ReadBlockedStorage(CfbEntry? storage)
        {
            if (storage == null || storage.Type != CfbEntryType.Storage) return Array.Empty<byte>();
            var blocks = Cfb.Children(storage).Where(c => c.Type == CfbEntryType.Stream && c.Name.StartsWith("$", StringComparison.Ordinal))
                .OrderBy(c => BlockNumber(c.Name)).ToList();
            var parts = new List<byte[]>(blocks.Count);
            long total = 0;
            foreach (var b in blocks)
            {
                var raw = Cfb.ReadStream(b);
                if (raw.Length <= 16) continue;
                try
                {
                    int zo = 16;
                    if (!Zlib.LooksLikeZlib(raw, zo))
                    {
                        // Fallback: search a few bytes for a zlib header.
                        zo = -1;
                        for (int i = 0; i < Math.Min(64, raw.Length - 2); i++) if (Zlib.LooksLikeZlib(raw, i)) { zo = i; break; }
                        if (zo < 0) { Warnings.Add($"Block {storage.Name}/{b.Name}: no zlib header"); continue; }
                    }
                    var data = Zlib.Inflate(raw, zo, raw.Length - zo);
                    parts.Add(data); total += data.Length;
                }
                catch (Exception ex) { Warnings.Add($"Block {storage.Name}/{b.Name}: {ex.Message}"); }
            }
            var all = new byte[total];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, o, p.Length); o += p.Length; }
            return all;
        }

        /// <summary>Reads a stream that is a bare zlib payload (optionally after a fixed prefix), e.g. Dgn~Mh, Dgn~H.</summary>
        public byte[]? ReadZlibStream(string path)
        {
            var e = Cfb.Find(path);
            if (e == null || e.Type != CfbEntryType.Stream) return null;
            var raw = Cfb.ReadStream(e);
            for (int i = 0; i < Math.Min(64, raw.Length - 2); i++)
            {
                if (!Zlib.LooksLikeZlib(raw, i)) continue;
                try { return Zlib.Inflate(raw, i, raw.Length - i); } catch { /* try next */ }
            }
            return null;
        }

        /// <summary>Splits an element payload into raw element records (offset, byteLength).</summary>
        public static IEnumerable<(int Offset, int Length)> SplitElements(byte[] data, List<string>? warnings = null)
        {
            int o = 0;
            while (o + 4 + 32 <= data.Length)
            {
                o += 4; // reserved/prefix word
                uint words = BitConverter.ToUInt32(data, o + 4);
                long len = (long)words * 2;
                if (words < 16 || o + len > data.Length)
                {
                    warnings?.Add($"Element stream truncated or corrupt at offset {o} (size {words} words)");
                    yield break;
                }
                yield return (o, (int)len);
                o += (int)len;
            }
        }

        /// <summary>
        /// Parses an XAttribute storage payload into element-id → xattributes.
        /// Record layout per element: u32 magic(0xA11B) | u32 recordBytes | u32 ? | u32 0 | u64 elementId | u32 count |
        /// count × { u16 minor, u16 major, u32 xattrId, u32 size|flags(0x80000000), u32 reserved, byte[size] }.
        /// </summary>
        public static Dictionary<ulong, List<Elements.XAttribute>> ReadXAttributes(byte[] data, List<string> warnings)
        {
            var map = new Dictionary<ulong, List<Elements.XAttribute>>();
            int o = 0;
            var r = new LeReader(data);
            int guard = 0;
            while (o + 28 <= data.Length && guard++ < 10_000_000)
            {
                uint magic = r.U32(o);
                uint recBytes = r.U32(o + 4) + 28; // stored length excludes the 28-byte record header
                if (magic != 0xA11B || o + recBytes > data.Length)
                {
                    // try to resync on next magic
                    int next = FindMagic(data, o + 1);
                    if (next < 0) { if (o < data.Length - 8) warnings.Add($"XAttribute stream: lost sync at {o}"); break; }
                    o = next; continue;
                }
                ulong elemId = r.U64(o + 16);
                uint count = r.U32(o + 24);
                int p = o + 28, end = o + (int)recBytes;
                var list = map.TryGetValue(elemId, out var l) ? l : (map[elemId] = new List<Elements.XAttribute>());
                for (uint i = 0; i < count && p + 16 <= end; i++)
                {
                    ushort minor = r.U16(p), major = r.U16(p + 2);
                    uint id = r.U32(p + 4);
                    uint sizeFlags = r.U32(p + 8);
                    int size = (int)(sizeFlags & 0x7FFFFFFF);
                    if (p + 16 + size > end) { warnings.Add($"XAttribute of element {elemId} overruns record"); break; }
                    var payload = r.Bytes(p + 16, size);
                    bool compressed = false;
                    // Compressed payload: u32 3 | u32 uncompressedSize | zlib
                    if (payload.Length > 10 && BitConverter.ToUInt32(payload, 0) == 3 && Zlib.LooksLikeZlib(payload, 8))
                    {
                        try { payload = Zlib.Inflate(payload, 8, payload.Length - 8); compressed = true; } catch { /* keep raw */ }
                    }
                    list.Add(new Elements.XAttribute
                    {
                        HandlerMinor = minor, HandlerMajor = major, Id = id, Flags = sizeFlags & 0x80000000,
                        Data = payload, WasCompressed = compressed,
                    });
                    p += 16 + size;
                }
                o += (int)recBytes;
            }
            return map;
        }

        private static int FindMagic(byte[] d, int from)
        {
            for (int i = from; i + 4 <= d.Length; i++)
                if (d[i] == 0x1B && d[i + 1] == 0xA1 && d[i + 2] == 0 && d[i + 3] == 0) return i;
            return -1;
        }
    }
}
