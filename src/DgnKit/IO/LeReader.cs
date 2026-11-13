using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DgnKit.IO
{
    /// <summary>Little-endian random-access reader over a byte array segment.</summary>
    public readonly struct LeReader
    {
        private readonly byte[] _buf;
        private readonly int _base;
        public int Length { get; }

        public LeReader(byte[] buf) : this(buf, 0, buf.Length) { }

        public LeReader(byte[] buf, int offset, int length)
        {
            _buf = buf;
            _base = offset;
            Length = Math.Max(0, Math.Min(length, buf.Length - offset));
        }

        public byte[] Buffer => _buf;
        public int BaseOffset => _base;

        public bool Has(int offset, int count) => offset >= 0 && count >= 0 && offset + count <= Length;

        public LeReader Slice(int offset, int length) => new LeReader(_buf, _base + offset, Math.Min(length, Length - offset));

        public byte U8(int o) => Has(o, 1) ? _buf[_base + o] : (byte)0;
        public ushort U16(int o) => Has(o, 2) ? (ushort)(_buf[_base + o] | (_buf[_base + o + 1] << 8)) : (ushort)0;
        public short I16(int o) => (short)U16(o);
        public uint U32(int o) => Has(o, 4) ? BitConverter.ToUInt32(_buf, _base + o) : 0u;
        public int I32(int o) => Has(o, 4) ? BitConverter.ToInt32(_buf, _base + o) : 0;
        public ulong U64(int o) => Has(o, 8) ? BitConverter.ToUInt64(_buf, _base + o) : 0ul;
        public long I64(int o) => Has(o, 8) ? BitConverter.ToInt64(_buf, _base + o) : 0L;
        public double F64(int o) => Has(o, 8) ? BitConverter.ToDouble(_buf, _base + o) : 0.0;

        public byte[] Bytes(int o, int n)
        {
            if (!Has(o, 0)) return Array.Empty<byte>();
            n = Math.Max(0, Math.Min(n, Length - o));
            var r = new byte[n];
            System.Buffer.BlockCopy(_buf, _base + o, r, 0, n);
            return r;
        }

        /// <summary>Reads a NUL-terminated (or length-limited) 8-bit string.</summary>
        public string Ansi(int o, int maxBytes, Encoding? enc = null)
        {
            if (!Has(o, 0)) return string.Empty;
            maxBytes = Math.Min(maxBytes, Length - o);
            int n = 0;
            while (n < maxBytes && _buf[_base + o + n] != 0) n++;
            return (enc ?? Latin1).GetString(_buf, _base + o, n);
        }

        /// <summary>Reads a UTF-16LE string of at most <paramref name="maxChars"/>, stopping at NUL.</summary>
        public string Utf16(int o, int maxChars)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < maxChars && Has(o + i * 2, 2); i++)
            {
                char c = (char)U16(o + i * 2);
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
    }

    internal static class Zlib
    {
        /// <summary>Inflates a zlib (RFC1950) stream: 2-byte header + raw deflate + adler32.</summary>
        public static byte[] Inflate(byte[] data, int offset, int count)
        {
            if (count < 2) return Array.Empty<byte>();
            using var ms = new MemoryStream(data, offset + 2, count - 2, false);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream(Math.Max(1024, count * 4));
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }

        public static bool LooksLikeZlib(byte[] d, int o) =>
            d.Length >= o + 2 && (d[o] & 0x0F) == 8 && ((d[o] << 8) | d[o + 1]) % 31 == 0;
    }
}
