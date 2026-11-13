using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DgnKit.Export.Pdf;

/// <summary>
/// Minimal dependency-free PDF 1.4 writer for vector plots: one or more pages with stroked/filled paths, dash patterns,
/// RGB colours and text in the standard Helvetica font (WinAnsi), content streams Flate-compressed.
/// Coordinates are PDF points (1/72 in), origin bottom-left.
/// </summary>
public sealed class MiniPdf
{
    private readonly List<MiniPdfPage> _pages = new();
    public string Title { get; set; } = "";
    public string Creator { get; set; } = "DgnKit";

    public MiniPdfPage AddPage(double widthPt, double heightPt)
    {
        var p = new MiniPdfPage(widthPt, heightPt);
        _pages.Add(p);
        return p;
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        Save(fs);
    }

    public void Save(Stream output)
    {
        var objects = new List<byte[]>();
        int Add(byte[] body) { objects.Add(body); return objects.Count; }
        byte[] A(string s) => Encoding.ASCII.GetBytes(s);

        // 1 catalog, 2 pages, 3 font, 4 info; pages/contents follow
        int catalog = Add(Array.Empty<byte>());
        int pagesId = Add(Array.Empty<byte>());
        int font = Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        int info = Add(A($"<< /Title {PdfString(Title)} /Creator {PdfString(Creator)} /Producer (DgnKit MiniPdf) >>"));
        var pageIds = new List<int>();
        foreach (var p in _pages)
        {
            var content = p.Content();
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content, 0, content.Length);
                compressed = ms.ToArray();
            }
            var cs = new MemoryStream();
            var hdr = A($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            cs.Write(hdr); cs.Write(compressed); cs.Write(A("\nendstream"));
            int contentId = Add(cs.ToArray());
            int pageId = Add(A(string.Format(CultureInfo.InvariantCulture,
                "<< /Type /Page /Parent {0} 0 R /MediaBox [0 0 {1:0.###} {2:0.###}] /Resources << /Font << /F1 {3} 0 R >> /ExtGState << /GS0 << /Type /ExtGState >> >> >> /Contents {4} 0 R >>",
                pagesId, p.Width, p.Height, font, contentId)));
            pageIds.Add(pageId);
        }
        objects[catalog - 1] = A($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        objects[pagesId - 1] = A($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => i + " 0 R"))}] /Count {pageIds.Count} >>");

        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        void Raw(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        Raw("%PDF-1.4\n%");
        w.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // binary marker comment
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            w.Flush(); offsets[i] = output.Position;
            Raw($"{i + 1} 0 obj\n"); w.Write(objects[i]); Raw("\nendobj\n");
        }
        w.Flush();
        long xref = output.Position;
        Raw($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Raw(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Raw($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        w.Flush();
    }

    internal static string PdfString(string s)
    {
        var sb = new StringBuilder("(");
        foreach (char c in s)
        {
            if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append(' ');
            else if (c < 127) sb.Append(c);
            else sb.Append(WinAnsi.TryGetValue(c, out byte b) ? "\\" + Convert.ToString(b, 8).PadLeft(3, '0') : "?");
        }
        return sb.Append(')').ToString();
    }

    /// <summary>Unicode → WinAnsiEncoding (cp1252) for the characters we may meet in engineering text.</summary>
    internal static readonly Dictionary<char, byte> WinAnsi = BuildWinAnsi();

    private static Dictionary<char, byte> BuildWinAnsi()
    {
        var d = new Dictionary<char, byte>();
        for (int i = 0xA0; i <= 0xFF; i++) d[(char)i] = (byte)i;
        var hi = new (char, byte)[] { ('€', 0x80), ('‚', 0x82), ('ƒ', 0x83), ('„', 0x84), ('…', 0x85), ('†', 0x86), ('‡', 0x87), ('ˆ', 0x88), ('‰', 0x89), ('Š', 0x8A), ('‹', 0x8B), ('Œ', 0x8C), ('Ž', 0x8E), ('‘', 0x91), ('’', 0x92), ('“', 0x93), ('”', 0x94), ('•', 0x95), ('–', 0x96), ('—', 0x97), ('˜', 0x98), ('™', 0x99), ('š', 0x9A), ('›', 0x9B), ('œ', 0x9C), ('ž', 0x9E), ('Ÿ', 0x9F) };
        foreach (var (c, b) in hi) d[c] = b;
        return d;
    }
}

public sealed class MiniPdfPage
{
    private readonly StringBuilder _c = new();
    public double Width { get; }
    public double Height { get; }
    internal MiniPdfPage(double w, double h) { Width = w; Height = h; }

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private (double, double, double)? _stroke, _fill;
    private double _lw = -1;
    private string _dash = "";

    public void SetStroke(byte r, byte g, byte b)
    {
        var c = (r / 255.0, g / 255.0, b / 255.0);
        if (_stroke == c) return;
        _stroke = c; _c.Append(N(c.Item1)).Append(' ').Append(N(c.Item2)).Append(' ').Append(N(c.Item3)).Append(" RG\n");
    }

    public void SetFill(byte r, byte g, byte b)
    {
        var c = (r / 255.0, g / 255.0, b / 255.0);
        if (_fill == c) return;
        _fill = c; _c.Append(N(c.Item1)).Append(' ').Append(N(c.Item2)).Append(' ').Append(N(c.Item3)).Append(" rg\n");
    }

    public void SetLineWidth(double pt)
    {
        if (Math.Abs(pt - _lw) < 1e-6) return;
        _lw = pt; _c.Append(N(pt)).Append(" w\n");
    }

    /// <summary>Dash pattern in points (alternating on/off); null/empty = solid. <paramref name="phase"/> = distance into the pattern where stroking starts.</summary>
    public void SetDash(double[]? pattern, double phase = 0)
    {
        string d = pattern == null || pattern.Length == 0 ? "[] 0 d" : "[" + string.Join(" ", pattern.Select(N)) + "] " + N(phase) + " d";
        if (d == _dash) return;
        _dash = d; _c.Append(d).Append('\n');
    }

    public void SetCaps(int cap, int join) => _c.Append(cap).Append(" J ").Append(join).Append(" j\n");

    public void Path(IReadOnlyList<(double X, double Y)> pts, bool close, bool stroke, bool fill)
    {
        if (pts.Count == 0) return;
        _c.Append(N(pts[0].X)).Append(' ').Append(N(pts[0].Y)).Append(" m\n");
        for (int i = 1; i < pts.Count; i++) _c.Append(N(pts[i].X)).Append(' ').Append(N(pts[i].Y)).Append(" l\n");
        if (close) _c.Append("h\n");
        _c.Append(fill && stroke ? "B*\n" : fill ? "f*\n" : "S\n");
    }

    public void Dot(double x, double y, double r)
    {
        // approximate circle with 4 Béziers
        double k = 0.5523 * r;
        _c.Append($"{N(x + r)} {N(y)} m {N(x + r)} {N(y + k)} {N(x + k)} {N(y + r)} {N(x)} {N(y + r)} c {N(x - k)} {N(y + r)} {N(x - r)} {N(y + k)} {N(x - r)} {N(y)} c {N(x - r)} {N(y - k)} {N(x - k)} {N(y - r)} {N(x)} {N(y - r)} c {N(x + k)} {N(y - r)} {N(x + r)} {N(y - k)} {N(x + r)} {N(y)} c f\n");
    }

    /// <summary>Text with baseline origin (x,y), em size, rotation (radians CCW) and horizontal scale.</summary>
    public void Text(string s, double x, double y, double emSize, double angle, double hScale = 1)
    {
        double c = Math.Cos(angle), sn = Math.Sin(angle);
        _c.Append("BT /F1 ").Append(N(emSize)).Append(" Tf ")
          .Append(N(c * hScale)).Append(' ').Append(N(sn * hScale)).Append(' ').Append(N(-sn)).Append(' ').Append(N(c)).Append(' ')
          .Append(N(x)).Append(' ').Append(N(y)).Append(" Tm ").Append(MiniPdf.PdfString(s)).Append(" Tj ET\n");
    }

    internal byte[] Content() => Encoding.ASCII.GetBytes(_c.ToString());
}
