using System;

namespace DgnKit.Model
{
    public readonly struct RgbColor : IEquatable<RgbColor>
    {
        public readonly byte R, G, B;
        public RgbColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public static RgbColor FromRgb24(int rgb) => new((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        public int ToRgb24() => (R << 16) | (G << 8) | B;
        public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
        public bool Equals(RgbColor o) => R == o.R && G == o.G && B == o.B;
        public override bool Equals(object? obj) => obj is RgbColor c && Equals(c);
        public override int GetHashCode() => ToRgb24();
        public override string ToString() => ToHex();
        /// <summary>Perceived luminance 0..1.</summary>
        public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;
    }

    /// <summary>
    /// 256-entry indexed color table. Index 255 is the background color in MicroStation;
    /// files may carry their own table (type 5 level 1), otherwise the MicroStation default applies.
    /// </summary>
    public sealed class ColorTable
    {
        private readonly RgbColor[] _colors = new RgbColor[256];
        public bool IsFromFile { get; private set; }

        public RgbColor this[int index] => _colors[index & 0xFF];
        public RgbColor Background => _colors[255];

        public static ColorTable CreateDefault()
        {
            var t = new ColorTable();
            for (int i = 0; i < 256; i++) t._colors[i] = RgbColor.FromRgb24(DefaultRgb[i]);
            return t;
        }

        /// <summary>Builds a table from 768 bytes (RGB triples) in DGN color table element order (entry 0 = background).</summary>
        public static ColorTable FromDgnBytes(byte[] rgb, int offset)
        {
            var t = new ColorTable { IsFromFile = true };
            // DGN color-table elements and .tbl files store the background color first, then colors 1..255… shifted:
            // file entry k ↔ color (k-1); file entry 0 ↔ background (255).
            for (int k = 0; k < 256 && offset + k * 3 + 2 < rgb.Length; k++)
            {
                var c = new RgbColor(rgb[offset + k * 3], rgb[offset + k * 3 + 1], rgb[offset + k * 3 + 2]);
                int idx = k == 0 ? 255 : k - 1;
                t._colors[idx] = c;
            }
            return t;
        }

        /// <summary>MicroStation default color table (color 0..254, 255 = background).</summary>
        internal static readonly int[] DefaultRgb = BuildDefault();

        private static int[] BuildDefault()
        {
            // As stored in MicroStation's default color.tbl (entry 0 = background), re-indexed to color numbers.
            int[] tbl =
            {
                0x000000,0xFFFFFF,0x0000FF,0x00FF00,0xFF0000,0xFFFF00,0xFF00FF,0xFF7F00,0x00FFFF,0x404040,0xC0C0C0,0xFE0060,
                0xA0E000,0x00FEA0,0x8000A0,0xB0B0B0,0x00F0F0,0xF0F0F0,0x0000F0,0x00F000,0xF00000,0xF0F000,0xF000F0,0xF07A00,
                0x00F0F0,0xF0F0F0,0x0000F0,0x00F000,0xF00000,0xF0F000,0xF000F0,0xF07A00,0x00E1E1,0xE1E1E1,0x0000E1,0x00E100,
                0xE10000,0xE1E100,0xE100E1,0xE17500,0x00E1E1,0xE1E1E1,0x0000E1,0x00E100,0xE10000,0xE1E100,0xE100E1,0xE17500,
                0x00D2D2,0xD2D2D2,0x0000D2,0x00D200,0xD20000,0xD2D200,0xD200D2,0xD27000,0x00D2D2,0xD2D2D2,0x0000D2,0x00D200,
                0xD20000,0xD2D200,0xD200D2,0xD27000,0x00C3C3,0xC3C3C3,0x0000C3,0x00C300,0xC30000,0xC3C300,0xC300C3,0xC36B00,
                0x00C3C3,0xC3C3C3,0x0000C3,0x00C300,0xC30000,0xC3C300,0xC300C3,0xC36B00,0x00B4B4,0xB4B4B4,0x0000B4,0x00B400,
                0xB40000,0xB4B400,0xB400B4,0xB46600,0x00B4B4,0xB4B4B4,0x0000B4,0x00B400,0xB40000,0xB4B400,0xB400B4,0xB46600,
                0x00A5A5,0xA5A5A5,0x0000A5,0x00A500,0xA50000,0xA5A500,0xA500A5,0xA56100,0x00A5A5,0xA5A5A5,0x0000A5,0x00A500,
                0xA50000,0xA5A500,0xA500A5,0xA56100,0x009696,0x969696,0x000096,0x009600,0x960000,0x969600,0x960096,0x965C00,
                0x009696,0x969696,0x000096,0x009600,0x960000,0x969600,0x960096,0x965C00,0x008787,0x878787,0x000087,0x008700,
                0x870000,0x878700,0x870087,0x875700,0x008787,0x878787,0x000087,0x008700,0x870000,0x878700,0x870087,0x875700,
                0x007878,0x787878,0x000078,0x007800,0x780000,0x787800,0x780078,0x785200,0x007878,0x787878,0x000078,0x007800,
                0x780000,0x787800,0x780078,0x785200,0x006969,0x696969,0x000069,0x006900,0x690000,0x696900,0x690069,0x694D00,
                0x006969,0x696969,0x000069,0x006900,0x690000,0x696900,0x690069,0x694D00,0x005A5A,0x5A5A5A,0x00005A,0x005A00,
                0x5A0000,0x5A5A00,0x5A005A,0x5A4800,0x005A5A,0x5A5A5A,0x00005A,0x005A00,0x5A0000,0x5A5A00,0x5A005A,0x5A4800,
                0x004B4B,0x4B4B4B,0x00004B,0x004B00,0x4B0000,0x4B4B00,0x4B004B,0x4B4300,0x004B4B,0x4B4B4B,0x00004B,0x004B00,
                0x4B0000,0x4B4B00,0x4B004B,0x4B4300,0x003C3C,0x3C3C3C,0x00003C,0x003C00,0x3C0000,0x3C3C00,0x3C003C,0x3C3E00,
                0x003C3C,0x3C3C3C,0x00003C,0x003C00,0x3C0000,0x3C3C00,0x3C003C,0x3C3E00,0x002D2D,0x2D2D2D,0x00002D,0x002D00,
                0x2D0000,0x2D2D00,0x2D002D,0x2D3900,0x002D2D,0x2D2D2D,0x00002D,0x002D00,0x2D0000,0x2D2D00,0x2D002D,0x2D3900,
                0x001E1E,0x1E1E1E,0x00001E,0x001E00,0x1E0000,0x1E1E00,0x1E001E,0x1E3400,0x001E1E,0x1E1E1E,0x00001E,0x001E00,
                0x1E0000,0x1E1E00,0x1E001E,0xC0C0C0,
            };
            var r = new int[256];
            for (int k = 0; k < 256; k++) r[k == 0 ? 255 : k - 1] = tbl[k];
            return r;
        }
    }
}
