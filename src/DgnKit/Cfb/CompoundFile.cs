using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DgnKit.Cfb
{
    /// <summary>
    /// Minimal read-only Microsoft Compound File Binary (OLE2 / structured storage) reader.
    /// DGN V8 (V8 2004 … V8i … CONNECT … 2024+) files are compound files.
    /// </summary>
    public sealed class CompoundFile
    {
        private const uint FreeSect = 0xFFFFFFFF, EndOfChain = 0xFFFFFFFE;
        private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        private readonly byte[] _data;
        private readonly int _sectorShift;
        private readonly int _miniShift;
        private readonly uint _miniCutoff;
        private readonly uint[] _fat;
        private readonly uint[] _miniFat;
        private readonly byte[] _miniStream;
        private readonly List<CfbEntry> _entries = new();

        public CfbEntry Root => _entries[0];
        public IReadOnlyList<CfbEntry> Entries => _entries;

        public static bool IsCompoundFile(byte[] head) =>
            head.Length >= 8 && head.Take(8).SequenceEqual(Signature);

        public CompoundFile(byte[] data)
        {
            _data = data;
            if (!IsCompoundFile(data)) throw new InvalidDataException("Not a compound file (missing OLE2 signature).");
            _sectorShift = BitConverter.ToUInt16(data, 0x1E);
            _miniShift = BitConverter.ToUInt16(data, 0x20);
            uint numFat = BitConverter.ToUInt32(data, 0x2C);
            uint firstDir = BitConverter.ToUInt32(data, 0x30);
            _miniCutoff = BitConverter.ToUInt32(data, 0x38);
            uint firstMiniFat = BitConverter.ToUInt32(data, 0x3C);
            uint firstDifat = BitConverter.ToUInt32(data, 0x44);
            uint numDifat = BitConverter.ToUInt32(data, 0x48);
            int ssz = 1 << _sectorShift;

            var difat = new List<uint>();
            for (int i = 0; i < 109; i++) difat.Add(BitConverter.ToUInt32(data, 0x4C + i * 4));
            uint s = firstDifat; int guard = 0;
            while (s != FreeSect && s != EndOfChain && numDifat-- > 0 && guard++ < 1_000_000)
            {
                long off = SectorOffset(s);
                int per = ssz / 4;
                for (int i = 0; i < per - 1; i++) difat.Add(ReadU32(off + i * 4));
                s = ReadU32(off + (per - 1) * 4);
            }

            var fat = new List<uint>((int)numFat * (ssz / 4));
            foreach (uint fs in difat.Take((int)numFat))
            {
                if (fs == FreeSect || fs == EndOfChain) continue;
                long off = SectorOffset(fs);
                for (int i = 0; i < ssz / 4; i++) fat.Add(ReadU32(off + i * 4));
            }
            _fat = fat.ToArray();

            byte[] dir = ReadChain(firstDir, long.MaxValue);
            for (int i = 0; i + 128 <= dir.Length; i += 128)
            {
                int nameLen = BitConverter.ToUInt16(dir, i + 64);
                string name = nameLen >= 2 ? Encoding.Unicode.GetString(dir, i, Math.Min(64, nameLen) - 2) : string.Empty;
                _entries.Add(new CfbEntry(
                    _entries.Count, name, (CfbEntryType)dir[i + 66],
                    BitConverter.ToInt32(dir, i + 68), BitConverter.ToInt32(dir, i + 72), BitConverter.ToInt32(dir, i + 76),
                    BitConverter.ToUInt32(dir, i + 116), BitConverter.ToUInt64(dir, i + 120)));
            }
            if (_entries.Count == 0) throw new InvalidDataException("Compound file has no directory entries.");

            var root = _entries[0];
            _miniStream = root.StartSector == EndOfChain ? Array.Empty<byte>() : ReadChain(root.StartSector, (long)root.Size);
            if (firstMiniFat != EndOfChain && firstMiniFat != FreeSect)
            {
                byte[] mf = ReadChain(firstMiniFat, long.MaxValue);
                _miniFat = new uint[mf.Length / 4];
                for (int i = 0; i < _miniFat.Length; i++) _miniFat[i] = BitConverter.ToUInt32(mf, i * 4);
            }
            else _miniFat = Array.Empty<uint>();
        }

        private long SectorOffset(uint sector) => ((long)sector + 1) << _sectorShift;
        private uint ReadU32(long off) => off + 4 <= _data.Length ? BitConverter.ToUInt32(_data, (int)off) : FreeSect;

        private byte[] ReadChain(uint start, long size)
        {
            int ssz = 1 << _sectorShift;
            using var ms = new MemoryStream();
            uint s = start; int guard = 0;
            var seen = new HashSet<uint>();
            while (s != FreeSect && s != EndOfChain && s < _fat.Length + 1 && guard++ < 50_000_000 && ms.Length < size)
            {
                if (!seen.Add(s)) break; // cycle protection
                long off = SectorOffset(s);
                if (off >= _data.Length) break;
                int n = (int)Math.Min(ssz, _data.Length - off);
                ms.Write(_data, (int)off, n);
                if (s >= _fat.Length) break;
                s = _fat[s];
            }
            var arr = ms.ToArray();
            if (size != long.MaxValue && arr.Length > size) Array.Resize(ref arr, (int)size);
            return arr;
        }

        private byte[] ReadMiniChain(uint start, long size)
        {
            int msz = 1 << _miniShift;
            var result = new byte[size];
            long pos = 0; uint s = start; int guard = 0;
            while (s != FreeSect && s != EndOfChain && pos < size && guard++ < 50_000_000)
            {
                long off = (long)s * msz;
                int n = (int)Math.Min(msz, size - pos);
                if (off + n > _miniStream.Length) break;
                Buffer.BlockCopy(_miniStream, (int)off, result, (int)pos, n);
                pos += n;
                if (s >= _miniFat.Length) break;
                s = _miniFat[s];
            }
            return result;
        }

        public byte[] ReadStream(CfbEntry e)
        {
            if (e.Type != CfbEntryType.Stream) throw new ArgumentException("Entry is not a stream: " + e.Name);
            if (e.Size == 0) return Array.Empty<byte>();
            return e.Size < _miniCutoff ? ReadMiniChain(e.StartSector, (long)e.Size) : ReadChain(e.StartSector, (long)e.Size);
        }

        /// <summary>Children of a storage in directory (red-black tree in-order) order.</summary>
        public IReadOnlyList<CfbEntry> Children(CfbEntry storage)
        {
            var list = new List<CfbEntry>();
            var stack = new Stack<int>();
            int cur = storage.Child;
            var visited = new HashSet<int>();
            while ((cur >= 0 && cur < _entries.Count) || stack.Count > 0)
            {
                while (cur >= 0 && cur < _entries.Count && visited.Add(cur)) { stack.Push(cur); cur = _entries[cur].Left; }
                if (stack.Count == 0) break;
                int n = stack.Pop();
                list.Add(_entries[n]);
                cur = _entries[n].Right;
            }
            return list;
        }

        public CfbEntry? Child(CfbEntry storage, string name) =>
            Children(storage).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

        /// <summary>Resolves a '/'-separated path from the root storage.</summary>
        public CfbEntry? Find(string path)
        {
            CfbEntry? cur = Root;
            foreach (var part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur == null) return null;
                cur = Child(cur, part);
            }
            return cur;
        }

        public IEnumerable<(string Path, CfbEntry Entry)> Walk(CfbEntry? from = null, string prefix = "")
        {
            foreach (var c in Children(from ?? Root))
            {
                string p = prefix + "/" + c.Name;
                yield return (p, c);
                if (c.Type == CfbEntryType.Storage)
                    foreach (var x in Walk(c, p)) yield return x;
            }
        }
    }

    public enum CfbEntryType : byte { Empty = 0, Storage = 1, Stream = 2, Root = 5 }

    public sealed class CfbEntry
    {
        public int Index { get; }
        public string Name { get; }
        public CfbEntryType Type { get; }
        internal int Left { get; }
        internal int Right { get; }
        internal int Child { get; }
        internal uint StartSector { get; }
        public ulong Size { get; }

        internal CfbEntry(int index, string name, CfbEntryType type, int left, int right, int child, uint start, ulong size)
        {
            Index = index; Name = name; Type = type; Left = left; Right = right; Child = child; StartSector = start; Size = size;
        }

        public override string ToString() => $"{Type} {Name} ({Size} bytes)";
    }
}
