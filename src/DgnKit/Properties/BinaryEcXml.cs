using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DgnKit.Properties
{
    /// <summary>A minimal XML-like node produced by the binary EC XML decoder.</summary>
    public sealed class EcXmlNode
    {
        public string Name { get; set; } = "";
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
        public List<EcXmlNode> Children { get; } = new();
        public object? Value { get; set; }

        public override string ToString() => Children.Count > 0 ? $"<{Name}> ({Children.Count} children)" : $"<{Name}>={Value}";
    }

    /// <summary>
    /// Decoder for the "BECXML" binary EC instance serialization found in V8 XAttributes (handler 0x56FF).
    /// Reverse-engineered layout:
    /// <code>
    ///   ... "BECXML\0" FF 0A 0D xx xx xx xx xx xx      header
    ///   30 &lt;n&gt;                                       string table count (n)
    ///   n × (FA &lt;len:u8&gt; bytes | FB &lt;len:u16&gt; bytes) UTF-8 strings
    ///   token stream:
    ///     03 &lt;s&gt;       start element (with attributes)   02 &lt;s&gt;  start element (no attributes)
    ///     05 &lt;s&gt; 11 &lt;s&gt;  attribute name = string value    06       end of attributes
    ///     10 &lt;v&gt;       typed value: v &lt; 0xF0 small int; F7 + 8-byte double; F5 + int32; F6 + int64 …
    ///     11 &lt;s&gt;       string value (string-table index)   04       end element
    /// </code>
    /// Index values ≥ 0xF0 are escape-prefixed (0xF1 + u16, 0xF2 + u32) when the string table exceeds 0xEF entries.
    /// </summary>
    public static class BinaryEcXml
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BECXML\0");

        public static bool IsBecxml(byte[] data) => IndexOf(data, Magic, 0, 64) >= 0;

        public static EcXmlNode? Decode(byte[] data, List<string>? diagnostics = null)
        {
            int m = IndexOf(data, Magic, 0, Math.Min(data.Length, 256));
            if (m < 0) return null;
            int p = m + Magic.Length;
            // header: FF 0A 0D + 6 bytes — find the string table marker 0x30 within the next 16 bytes.
            int st = -1;
            for (int i = p; i < Math.Min(data.Length, p + 24); i++) if (data[i] == 0x30 && i + 2 < data.Length && (data[i + 2] == 0xFA || data[i + 2] == 0xFB || data[i + 1] == 0)) { st = i; break; }
            if (st < 0) { diagnostics?.Add("BECXML: string table not found"); return null; }
            p = st + 1;
            int count = ReadIndex(data, ref p);
            var strings = new List<string>(count);
            for (int i = 0; i < count && p < data.Length; i++)
            {
                byte tag = data[p++];
                int len;
                if (tag == 0xFA) len = data[p++];
                else if (tag == 0xFB) { len = data[p] | (data[p + 1] << 8); p += 2; }
                else if (tag == 0xFC) { len = BitConverter.ToInt32(data, p); p += 4; }
                else { diagnostics?.Add($"BECXML: unexpected string tag 0x{tag:X2} at {p - 1}"); return null; }
                if (p + len > data.Length) { diagnostics?.Add("BECXML: string overruns buffer"); return null; }
                strings.Add(Encoding.UTF8.GetString(data, p, len));
                p += len;
            }

            string S(int idx) => idx >= 0 && idx < strings.Count ? strings[idx] : $"#{idx}";

            var root = new EcXmlNode { Name = "#document" };
            var stack = new Stack<EcXmlNode>();
            stack.Push(root);
            int guard = 0;
            while (p < data.Length && guard++ < 1_000_000)
            {
                byte tok = data[p++];
                switch (tok)
                {
                    case 0x02:
                    case 0x03:
                    {
                        var n = new EcXmlNode { Name = S(ReadIndex(data, ref p)) };
                        stack.Peek().Children.Add(n);
                        stack.Push(n);
                        break;
                    }
                    case 0x05:
                    {
                        string an = S(ReadIndex(data, ref p));
                        object? v = ReadValue(data, ref p, strings);
                        if (stack.Count > 0) stack.Peek().Attributes[an] = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                        break;
                    }
                    case 0x06: break; // end of attributes
                    case 0x10:
                    case 0x11:
                    {
                        p--; // let ReadValue consume the type byte
                        var v = ReadValue(data, ref p, strings);
                        var cur = stack.Peek();
                        cur.Value = cur.Value == null ? v : Convert.ToString(cur.Value, CultureInfo.InvariantCulture) + Convert.ToString(v, CultureInfo.InvariantCulture);
                        break;
                    }
                    case 0x04:
                        if (stack.Count > 1) stack.Pop();
                        if (stack.Count == 1 && root.Children.Count > 0 && LooksDone(data, p)) return root.Children.Count == 1 ? root.Children[0] : root;
                        break;
                    case 0x00:
                        return root.Children.Count == 1 ? root.Children[0] : root;
                    default:
                        diagnostics?.Add($"BECXML: unknown token 0x{tok:X2} at {p - 1}");
                        return root.Children.Count == 1 ? root.Children[0] : root;
                }
            }
            return root.Children.Count == 1 ? root.Children[0] : root;
        }

        private static bool LooksDone(byte[] d, int p) => p >= d.Length || d[p] == 0x00 || d[p] == 0x04;

        private static object? ReadValue(byte[] d, ref int p, List<string> strings)
        {
            if (p >= d.Length) return null;
            byte t = d[p++];
            if (t == 0x11) { int idx = ReadIndex(d, ref p); return idx >= 0 && idx < strings.Count ? strings[idx] : null; }
            if (t != 0x10) { p--; return null; }
            byte v = d[p++];
            if (v < 0xF0) return (int)v;
            // 0xF1..0xF4: unsigned little-endian integer of 1..4 bytes; 0xF5/0xF6: 8-byte integer; 0xF7: double.
            if (v >= 0xF1 && v <= 0xF4)
            {
                int n = v - 0xF0; long x = 0;
                for (int i = 0; i < n && p + i < d.Length; i++) x |= (long)d[p + i] << (8 * i);
                p += n;
                return x <= int.MaxValue ? (object)(int)x : x;
            }
            switch (v)
            {
                case 0xF5: { long x = BitConverter.ToInt64(d, p); p += 8; return x; }
                case 0xF6: { long x = BitConverter.ToInt64(d, p); p += 8; return x; }
                case 0xF7: { double x = BitConverter.ToDouble(d, p); p += 8; return x; }
                case 0xF8: { float x = BitConverter.ToSingle(d, p); p += 4; return (double)x; }
                default: throw new FormatException($"BECXML value code 0x{v:X2}");
            }
        }

        private static int ReadIndex(byte[] d, ref int p)
        {
            if (p >= d.Length) return -1;
            byte b = d[p++];
            if (b < 0xF0) return b;
            if (b >= 0xF1 && b <= 0xF4)
            {
                int n = b - 0xF0, x = 0;
                for (int i = 0; i < n && p + i < d.Length; i++) x |= d[p + i] << (8 * i);
                p += n;
                return x;
            }
            throw new FormatException($"BECXML index code 0x{b:X2}");
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from, int maxLen)
        {
            int end = Math.Min(hay.Length - needle.Length, from + maxLen);
            for (int i = from; i <= end; i++)
            {
                int k = 0;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }
}
