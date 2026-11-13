using System.Globalization;
using System.Text;
using System.Text.Json;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.Graphics;
using DgnKit.Model;

namespace DgnKit.Tests;

/// <summary>Field-by-field comparison of DgnKit's decode against a MicroStation COM oracle dump.</summary>
public sealed class OracleComparison
{
    public string File { get; init; } = "";
    public Dictionary<string, (int Match, int Total)> Fields { get; } = new();
    public List<string> Mismatches { get; } = new();

    public void Tally(string field, bool ok, string? detail = null)
    {
        var (m, t) = Fields.TryGetValue(field, out var v) ? v : (0, 0);
        Fields[field] = (m + (ok ? 1 : 0), t + 1);
        if (!ok && detail != null && Mismatches.Count(x => x.StartsWith(field + ":", StringComparison.Ordinal)) < 6) Mismatches.Add(field + ": " + detail);
    }

    public double Rate(string field) => Fields.TryGetValue(field, out var v) && v.Total > 0 ? (double)v.Match / v.Total : 1.0;
}

public static class OracleComparer
{
    public static OracleComparison Compare(string dgnPath, string oraclePath)
    {
        var f = DgnFile.Open(dgnPath);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(oraclePath));
        var o = doc.RootElement;
        var cmp = new OracleComparison { File = Path.GetFileName(dgnPath) };
        bool v7 = f.Format == DgnFormat.V7;

        // ---------------- levels
        if (!v7)
        {
            foreach (var ol in o.GetProperty("levels").EnumerateArray())
            {
                uint id = (uint)ol.GetProperty("id").GetInt64();
                var l = f.Levels[id];
                cmp.Tally("level.exists", l != null, $"id {id} '{Str(ol, "name")}' missing");
                if (l == null) continue;
                cmp.Tally("level.name", l.Name == Str(ol, "name"), $"{id}: '{l.Name}' vs '{Str(ol, "name")}'");
                cmp.Tally("level.color", l.Color == (uint)Int(ol, "color"), $"{l.Name}: {l.Color} vs {Int(ol, "color")}");
                cmp.Tally("level.weight", l.Weight == (uint)Int(ol, "weight"), $"{l.Name}: {l.Weight} vs {Int(ol, "weight")}");
                if (ol.TryGetProperty("styleNumber", out var sn) && sn.ValueKind == JsonValueKind.Number)
                    cmp.Tally("level.style", l.Style == sn.GetInt32(), $"{l.Name}: {l.Style} vs {sn.GetInt32()}");
                cmp.Tally("level.displayed", l.IsDisplayed == Bool(ol, "displayed"), $"{l.Name}: {l.IsDisplayed} vs {Bool(ol, "displayed")}");
                cmp.Tally("level.plot", l.IsPlot == Bool(ol, "plot"), $"{l.Name}: {l.IsPlot} vs {Bool(ol, "plot")}");
                if (ol.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.Number)
                    cmp.Tally("level.code", l.Code == (uint)cd.GetInt64(), $"{l.Name}: {l.Code} vs {cd.GetInt64()}");
            }
        }

        // ---------------- models & elements
        foreach (var om in o.GetProperty("models").EnumerateArray())
        {
            string mname = Str(om, "name");
            var m = f.Models.FirstOrDefault(x => x.Name == mname) ?? (v7 ? f.Models[0] : null);
            cmp.Tally("model.exists", m != null, $"model '{mname}' missing (have: {string.Join(", ", f.Models.Select(x => x.Name))})");
            if (m == null) continue;
            if (!v7) cmp.Tally("model.is3d", m.Is3d == Bool(om, "is3d"), $"{mname}: {m.Is3d} vs {Bool(om, "is3d")}");
            double ouor = Dbl(om, "uorPerMaster");
            cmp.Tally("model.uorPerMaster", Math.Abs(m.Units.UorPerMaster - ouor) <= 1e-6 * Math.Max(1, ouor), $"{mname}: {m.Units.UorPerMaster} vs {ouor}");
            if (om.TryGetProperty("globalOrigin", out var go) && go.ValueKind == JsonValueKind.Array)
            {
                // COM reports the global origin in UORs.
                var ogo = P(go);
                var mgo = m.GlobalOrigin;
                cmp.Tally("model.globalOrigin", mgo.DistanceTo(ogo) < 1e-6 * Math.Max(1, ogo.Length), $"{mname}: {mgo} vs {ogo}");
            }

            var byId = m.Elements.Concat(m.ControlElements).Concat(f.NonModelElements).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            var byOffsetV7 = v7 ? m.Elements.Concat(m.ControlElements).ToList() : null;
            int v7Index = 0;
            foreach (var oe in om.GetProperty("elements").EnumerateArray())
            {
                if (!Bool(oe, "graphic")) continue;
                int otype = Int(oe, "type");
                Element? e = null;
                if (v7)
                {
                    // V7 has no element ids: walk graphic elements in file order
                    while (v7Index < byOffsetV7!.Count && !byOffsetV7[v7Index].IsGraphic) v7Index++;
                    if (v7Index < byOffsetV7.Count) e = byOffsetV7[v7Index++];
                }
                else if (ulong.TryParse(Str(oe, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id)) byId.TryGetValue(id, out e);
                cmp.Tally("element.found", e != null, $"id {Str(oe, "id")} type {otype}");
                if (e == null) continue;
                cmp.Tally("element.type", e.RawType == otype, $"{e.Id}: {e.RawType} vs {otype}");
                if (e.RawType != otype) continue;
                // Complex headers (cells) are stored on level 0; MicroStation reports "" or the first component's level.
                uint lid = e.LevelId;
                string lname = lid == 0 ? "" : f.Levels.NameOf(lid);
                string olname = Str(oe, "level");
                bool levelOk = lname == olname || (lid == 0 && e.SelfAndDescendants().Skip(1).Any(c => c.LevelId != 0 && f.Levels.NameOf(c.LevelId) == olname));
                if (!v7) cmp.Tally("element.level", levelOk, $"{e.Id} ({e.TypeName}): '{lname}' vs '{olname}'");
                cmp.Tally("element.color", e.Color == (uint)Int(oe, "color"), $"{e.Id} ({e.TypeName}): {e.Color} vs {Int(oe, "color")}");
                cmp.Tally("element.weight", e.Weight == (uint)Int(oe, "weight"), $"{e.Id}: {e.Weight} vs {Int(oe, "weight")}");
                string ostyle = Str(oe, "style");
                string style = StyleName(f, e.Style);
                cmp.Tally("element.style", string.Equals(style, ostyle, StringComparison.OrdinalIgnoreCase), $"{e.Id}: '{style}' ({e.Style}) vs '{ostyle}'");

                if (oe.TryGetProperty("range", out var rg) && rg.ValueKind == JsonValueKind.Array && e is not TextElement && e is not TextNodeElement && e.RawType != 33 && e.RawType != 106)
                {
                    var r = ElementRange.Compute(e, includeText: false);
                    if (!r.IsEmpty)
                    {
                        var lo = m.ToMaster(r.Low); var hi = m.ToMaster(r.High);
                        var olo = P(rg[0]); var ohi = P(rg[1]);
                        double ext = Math.Max(1e-9, Math.Max(ohi.X - olo.X, ohi.Y - olo.Y));
                        double tol = Math.Max(1e-6, 1e-3 * ext);
                        bool ok = lo.DistanceTo(olo) <= tol * 3 && hi.DistanceTo(ohi) <= tol * 3;
                        // MicroStation ranges include line width/style padding for some elements; accept containment within 2%.
                        if (!ok) ok = Within(lo, hi, olo, ohi, 0.02 * ext + tol);
                        cmp.Tally("element.range", ok, $"{e.Id} ({e.TypeName}): {lo}-{hi} vs {olo}-{ohi}");
                    }
                }
                if (otype == 17 && e is TextElement t)
                {
                    cmp.Tally("text.string", t.Text == Str(oe, "text"), $"{e.Id}: '{t.Text}' vs '{Str(oe, "text")}'");
                    if (oe.TryGetProperty("origin", out var og) && t.Text.Length > 0)
                    {
                        // MicroStation derives centre/right justification points from real glyph extents (font metrics),
                        // so only the axis perpendicular to the baseline is exact for those; left-justified must match fully.
                        var jp = m.ToMaster(t.JustificationPoint);
                        var d = P(og) - jp;
                        bool left = (int)t.Justification <= 5 || (int)t.Justification == 16;
                        double err = left ? d.Length : Math.Abs(d.Dot(t.Rotation.Column(1).Normalized()));
                        double tol = Math.Max(1e-6 * Math.Max(1, P(og).Length), 0.01 * m.ToMaster(t.Height));
                        cmp.Tally("text.origin", err < tol, $"{e.Id} ({t.Justification}): {jp} vs {P(og)}");
                    }
                    double oh = Dbl(oe, "height");
                    if (oh > 0 && t.Text.Length > 0) cmp.Tally("text.height", Math.Abs(m.ToMaster(t.Height) - oh) <= 1e-6 * Math.Max(1, oh), $"{e.Id}: {m.ToMaster(t.Height)} vs {oh}");
                }
                if (otype == 37 && e is TagElement tg && oe.TryGetProperty("tagValue", out _))
                {
                    string ov = Str(oe, "tagValue");
                    string mv = Convert.ToString(tg.Value, CultureInfo.InvariantCulture) ?? "";
                    cmp.Tally("tag.value", mv == ov, $"{e.Id}: '{mv}' vs '{ov}'");
                    cmp.Tally("tag.name", (tg.TagName ?? "") == Str(oe, "tagName"), $"{e.Id}: '{tg.TagName}' vs '{Str(oe, "tagName")}'");
                    cmp.Tally("tag.set", (tg.TagSetName ?? "") == Str(oe, "tagSet"), $"{e.Id}: '{tg.TagSetName}' vs '{Str(oe, "tagSet")}'");
                    if (oe.TryGetProperty("origin", out var tog) && tg.TextHeight > 0)
                    {
                        // MicroStation reports the tag's justification point; accept within one text height.
                        double dist = m.ToMaster(tg.Origin).DistanceTo(P(tog));
                        cmp.Tally("tag.origin", dist <= 1.0 * m.ToMaster(tg.TextHeight) + 1e-6, $"{e.Id}: {m.ToMaster(tg.Origin)} vs {P(tog)}");
                    }
                    if (oe.TryGetProperty("tagTarget", out var tt) && tt.ValueKind == JsonValueKind.String)
                        cmp.Tally("tag.target", tg.TargetId.ToString(CultureInfo.InvariantCulture) == tt.GetString(), $"{e.Id}: {tg.TargetId} vs {tt.GetString()}");
                }
                if (otype == 2 && e is CellElement c)
                {
                    cmp.Tally("cell.name", (c.Name ?? "") == Str(oe, "name"), $"{e.Id}: '{c.Name}' vs '{Str(oe, "name")}'");
                    if (oe.TryGetProperty("origin", out var og)) cmp.Tally("cell.origin", m.ToMaster(c.Origin).DistanceTo(P(og)) < 1e-6 * Math.Max(1, P(og).Length), $"{e.Id}: {m.ToMaster(c.Origin)} vs {P(og)}");
                }
                if (otype == 35 && e is SharedCellElement sc)
                {
                    cmp.Tally("sharedcell.name", (sc.Name ?? "") == Str(oe, "name"), $"{e.Id}: '{sc.Name}' vs '{Str(oe, "name")}'");
                    cmp.Tally("sharedcell.resolved", sc.Definition != null, $"{e.Id}: '{sc.Name}' definition not found");
                    if (oe.TryGetProperty("origin", out var og)) cmp.Tally("sharedcell.origin", m.ToMaster(sc.Origin).DistanceTo(P(og)) < 1e-6 * Math.Max(1, P(og).Length), $"{e.Id}: {m.ToMaster(sc.Origin)} vs {P(og)}");
                }
                if ((otype == 3 || otype == 4 || otype == 6) && oe.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array)
                {
                    var mine = e is LineElement le ? new List<DPoint3d> { le.Start, le.End } : e is LineStringElement lse ? lse.Points : new List<DPoint3d>();
                    bool ok = mine.Count == pts.GetArrayLength();
                    for (int i = 0; ok && i < mine.Count; i++) ok = m.ToMaster(mine[i]).DistanceTo(P(pts[i])) < 1e-6 * Math.Max(1, P(pts[i]).Length);
                    cmp.Tally("geometry.vertices", ok, $"{e.Id}: {mine.Count} pts, first {(mine.Count > 0 ? m.ToMaster(mine[0]).ToString() : "-")} vs {(pts.GetArrayLength() > 0 ? P(pts[0]).ToString() : "-")}");
                }
                if ((otype == 15 || otype == 16) && e is ArcElement a && oe.TryGetProperty("center", out var ctr))
                {
                    bool ok = m.ToMaster(a.Origin).DistanceTo(P(ctr)) < 1e-6 * Math.Max(1, P(ctr).Length)
                              && Math.Abs(m.ToMaster(a.PrimaryAxis) - Dbl(oe, "primary")) < 1e-6 * Math.Max(1, Dbl(oe, "primary"))
                              && Math.Abs(m.ToMaster(a.SecondaryAxis) - Dbl(oe, "secondary")) < 1e-6 * Math.Max(1, Dbl(oe, "secondary"));
                    cmp.Tally("geometry.arc", ok, $"{e.Id}: c={m.ToMaster(a.Origin)} r={m.ToMaster(a.PrimaryAxis)}/{m.ToMaster(a.SecondaryAxis)} vs c={P(ctr)} r={Dbl(oe, "primary")}/{Dbl(oe, "secondary")}");
                    if (otype == 16 && oe.TryGetProperty("startPoint", out var sp))
                        cmp.Tally("geometry.arcStart", m.ToMaster(a.PointAt(a.StartAngle)).DistanceTo(P(sp)) < 1e-5 * Math.Max(1, Dbl(oe, "primary")), $"{e.Id}: {m.ToMaster(a.PointAt(a.StartAngle))} vs {P(sp)}");
                }
                if (otype == 33 && e is DimensionElement dim && oe.TryGetProperty("dimType", out var odt) && odt.ValueKind == JsonValueKind.Number)
                    CompareDimension(cmp, m, dim, oe, odt.GetInt32());
            }
        }
        return cmp;
    }

    /// <summary>
    /// Dimension (33) vs the oracle's DimensionElement API (type, 1-based Point(i), DimHeight, per-segment ActualValue /
    /// PrimaryText) and MicroStation's regenerated graphics (Drop(): text strings, note terminator ellipse vs arrow).
    /// </summary>
    private static void CompareDimension(OracleComparison cmp, DgnModel m, DimensionElement d, JsonElement oe, int otype)
    {
        cmp.Tally("dimension.type", d.DimensionType == otype, $"{d.Id}: {d.DimensionType} vs {otype}");
        if (oe.TryGetProperty("pointsRaw", out var pr) && pr.ValueKind == JsonValueKind.Array)
        {
            var opts = pr.EnumerateArray().Skip(1).Where(p => p.ValueKind == JsonValueKind.Array).Select(P).ToList();
            bool ok = d.Points.Count == opts.Count;
            for (int i = 0; ok && i < opts.Count; i++) ok = m.ToMaster(d.Points[i]).DistanceTo(opts[i]) < 1e-6 * Math.Max(1, opts[i].Length);
            cmp.Tally("dimension.points", ok, $"{d.Id}: {d.Points.Count} pts, first {(d.Points.Count > 0 ? m.ToMaster(d.Points[0]).ToString() : "-")} vs {opts.Count} pts, first {(opts.Count > 0 ? opts[0].ToString() : "-")}");
        }
        if (d.Kind != DimensionKind.Note && oe.TryGetProperty("dimHeight", out var oh) && oh.ValueKind == JsonValueKind.Number)
            cmp.Tally("dimension.height", Math.Abs(m.ToMaster(d.Height) - oh.GetDouble()) < 1e-6 * Math.Max(1, Math.Abs(oh.GetDouble())), $"{d.Id}: {m.ToMaster(d.Height)} vs {oh.GetDouble()}");
        if (d.Kind != DimensionKind.Note && oe.TryGetProperty("segmentsRaw", out var segs) && segs.ValueKind == JsonValueKind.Array)
        {
            var list = segs.EnumerateArray().Skip(1).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var s = i < d.Segments.Count ? d.Segments[i] : null;
                if (list[i].TryGetProperty("value", out var ov) && ov.ValueKind == JsonValueKind.Number)
                    cmp.Tally("dimension.value", s != null && Math.Abs(s.Value - ov.GetDouble()) < 1e-4 * Math.Max(1, Math.Abs(ov.GetDouble())), $"{d.Id}[{i}]: {s?.Value} vs {ov.GetDouble()}");
                if (list[i].TryGetProperty("primary", out var op) && op.ValueKind == JsonValueKind.String)
                    cmp.Tally("dimension.override", s != null && (s.OverrideText ?? "*") == op.GetString(), $"{d.Id}[{i}]: '{s?.OverrideText ?? "*"}' vs '{op.GetString()}'");
            }
        }
        if (oe.TryGetProperty("dropped", out var dr) && dr.ValueKind == JsonValueKind.Array)
        {
            var otexts = dr.EnumerateArray().Where(x => Int(x, "type") == 17).Select(x => Str(x, "text")).ToList();
            var mine = d.Segments.SelectMany(s => s.Lines).ToList();
            cmp.Tally("dimension.text", string.Join("|", mine) == string.Join("|", otexts), $"{d.Id} ({d.Kind}): [{string.Join("|", mine)}] vs [{string.Join("|", otexts)}]");
            if (d.Kind == DimensionKind.Note)
            {
                bool dot = dr.EnumerateArray().Any(x => Int(x, "type") == 15);
                cmp.Tally("dimension.terminator", (d.NoteTerminator == DimensionTerminator.Dot) == dot, $"{d.Id}: {d.NoteTerminator} vs {(dot ? "Dot" : "Arrow")}");
            }
        }
    }

    private static bool Within(DPoint3d lo, DPoint3d hi, DPoint3d olo, DPoint3d ohi, double tol) =>
        lo.X >= olo.X - tol && lo.Y >= olo.Y - tol && hi.X <= ohi.X + tol && hi.Y <= ohi.Y + tol &&
        Math.Abs((hi.X - lo.X) - (ohi.X - olo.X)) <= tol * 2 && Math.Abs((hi.Y - lo.Y) - (ohi.Y - olo.Y)) <= tol * 2;

    private static string StyleName(DgnFile f, int style)
    {
        if (style == Symbology.StyleByLevel) return "STYLE_ByLevel";
        if (style == Symbology.StyleByCell) return "STYLE_ByCell";
        if (style >= 0 && style <= 7) return style.ToString(CultureInfo.InvariantCulture);
        return f.LineStyles.Find(style)?.Name ?? ("#" + style);
    }

    private static DPoint3d P(JsonElement a) => new(D(a[0]), D(a[1]), D(a[2]));
    private static double D(JsonElement v) => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : -1;
    private static double Dbl(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    public static string Report(IEnumerable<OracleComparison> results)
    {
        var all = results.ToList();
        var fields = all.SelectMany(r => r.Fields.Keys).Distinct().OrderBy(x => x).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# DgnKit vs MicroStation oracle");
        sb.AppendLine();
        sb.AppendLine("Match rate per field (matched/total). Oracle = MicroStation 2026 COM automation dump of the same file.");
        sb.AppendLine();
        sb.Append("| file |"); foreach (var fl in fields) sb.Append(' ').Append(fl).Append(" |"); sb.AppendLine();
        sb.Append("|---|"); foreach (var _ in fields) sb.Append("---|"); sb.AppendLine();
        foreach (var r in all)
        {
            sb.Append("| ").Append(r.File).Append(" |");
            foreach (var fl in fields)
                sb.Append(' ').Append(r.Fields.TryGetValue(fl, out var v) ? (v.Match == v.Total ? $"✓ {v.Total}" : $"{v.Match}/{v.Total}") : "").Append(" |");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.Append("| **total** |");
        foreach (var fl in fields)
        {
            int m = all.Sum(r => r.Fields.TryGetValue(fl, out var v) ? v.Match : 0), t = all.Sum(r => r.Fields.TryGetValue(fl, out var v) ? v.Total : 0);
            sb.Append(' ').Append(t == 0 ? "" : $"{100.0 * m / t:0.0}%").Append(" |");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Sample mismatches");
        foreach (var r in all.Where(r => r.Mismatches.Count > 0))
        {
            sb.AppendLine($"### {r.File}");
            foreach (var mm in r.Mismatches.Take(40)) sb.AppendLine("- " + mm);
        }
        return sb.ToString();
    }
}
