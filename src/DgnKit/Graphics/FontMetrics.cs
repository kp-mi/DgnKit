using System;
using System.Collections.Generic;

namespace DgnKit.Graphics
{
    /// <summary>
    /// Text metrics for plotting and layout without the CAD font files.
    /// <para>
    /// Size: MicroStation sizes TrueType and RSC text so that the <b>cap height equals the text size</b>. Measured on official
    /// DOT plots (vector glyph ink of all-caps words vs the text size in the plot's invisible text layer): ink cap height /
    /// size 1.03–1.05 including stroke width, and our Helvetica at em = size / <see cref="HelveticaCapHeight"/> matches it
    /// within 0.96–1.03 for Arial, FDOT, WSDOT CAE, Engineering (TrueType) and NJDOT RSC fonts.
    /// </para>
    /// <para>
    /// Width: what still differs is each font's advance widths, see <see cref="WidthFactor"/>.
    /// </para>
    /// </summary>
    public static class FontMetrics
    {
        /// <summary>Cap height of Helvetica / Arial in em units (Arial 0.716).</summary>
        public const double HelveticaCapHeight = 0.718;

        // Advance widths relative to Helvetica at the same cap height, from official plots after text registration: median
        // per sheet of (first-to-last letter position of a word in the plot's text layer, which MicroStation places at the
        // real glyph positions) / (the same in DgnKit's PDF). Word ink widths agree within ~5%. FDOT 1.20–1.30 all give the
        // best FDOT ink F1 (plateau); 1.15 is clearly worse. Arial, WSDOT CAE and NJDOT RSC fonts measured 0.98–1.03.
        private static readonly Dictionary<string, double> Widths = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FDOT"] = 1.24,                // FDOT 000-510, 102-600, 110-100: letter span 1.21–1.28, ink 1.16–1.22
            ["FDOT Bold"] = 1.14,           // letter span 1.11–1.27, ink 1.05–1.18
            ["Engineering Regular"] = 1.13, // TxDOT ed10-26, tcp(1-4)-26: letter span 1.12–1.14
            ["Engineering Bold"] = 1.12,    // letter span 1.12–1.13, ink 1.09
        };

        /// <summary>
        /// Advance-width ratio of the named CAD font to Helvetica drawn at the same cap height; 1 for unknown fonts.
        /// Exporters that substitute Helvetica/Arial apply it as a horizontal text scale so that words keep MicroStation's
        /// length (and later words on a line stay where MicroStation placed them).
        /// </summary>
        public static double WidthFactor(string? fontName) =>
            fontName != null && Widths.TryGetValue(fontName.Trim(), out var f) ? f : 1.0;
    }
}
