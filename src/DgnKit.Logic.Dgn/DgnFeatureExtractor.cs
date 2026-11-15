using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DgnKit.Elements;
using DgnKit.Graphics;
using DgnKit.Logic.Geometry;
using DgnKit.Logic.Model;
using DgnKit.Model;

namespace DgnKit.Logic.Dgn
{
    public sealed class DgnFeatureOptions
    {
        /// <summary>Models to extract (null = every non-empty, non-dictionary model).</summary>
        public IReadOnlyCollection<string>? Models { get; set; }
        /// <summary>Include elements drawn from reference attachments (keyed by reference path).</summary>
        public bool IncludeReferences { get; set; } = true;
        /// <summary>Skip pure annotation (text, dimensions) — they are rarely engineering objects.</summary>
        public bool SkipAnnotation { get; set; }
        /// <summary>Maximum characters of cell text collected into <see cref="Feature.Text"/>.</summary>
        public int MaxText { get; set; } = 240;
    }

    /// <summary>
    /// DGN → logic features. One feature per top-level element (cell, shared cell, complex, or simple element), built from
    /// the display list (so cells, shared cells and references arrive instanced, in world coordinates) converted to
    /// metres, plus the element's levels, cell name, texts, EC / tag / linkage properties and the model's sheet context
    /// (title-block tags). Keys match the IMX element keys (<c>el:&lt;id&gt;</c>), so logic results can be written back into
    /// the iModel next to the elements they are about.
    /// </summary>
    public static class DgnFeatureExtractor
    {
        /// <summary>The IMX element key of a top-level DGN element (same scheme as DgnImxExporter).</summary>
        public static string ElementKey(ulong id) => "el:" + id.ToString(CultureInfo.InvariantCulture);

        public static FeatureSet Extract(DgnFile file, DgnFeatureOptions? options = null)
        {
            var opt = options ?? new DgnFeatureOptions();
            var set = new FeatureSet();
            string source = Path.GetFileName(file.Path ?? "file.dgn");
            set.Info["source"] = source;
            set.Info["format"] = $"DGN {file.Format} {file.MajorVersion}.{file.MinorVersion}";
            var models = file.Models.Where(m => !m.IsDictionary && m.Elements.Count > 0)
                .Where(m => opt.Models == null || opt.Models.Contains(m.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            set.Info["models"] = string.Join(", ", models.Select(m => m.Name));
            foreach (var model in models) ExtractModel(file, model, source, opt, set);
            set.Info["features"] = set.Features.Count;
            return set;
        }

        private static readonly string[] SheetTagNames = { "SheetTitleLine1", "SheetTitleLine2", "SheetTitleLine2a", "SheetTitle", "Title", "PlanReference", "SheetNumber", "SheetNumberOf", "ContractNumber" };

        private static void ExtractModel(DgnFile file, DgnModel model, string source, DgnFeatureOptions opt, FeatureSet set)
        {
            double m = model.Units.MetersPerMaster;
            if (!(m > 0) || double.IsInfinity(m)) m = 1;
            string frame = source + "|" + model.Name;

            // sheet context from title-block tags anywhere in the model
            var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["file"] = source,
                ["model"] = model.Name,
                ["units"] = model.Units.Master.Label,
                ["is3d"] = model.Is3d,
            };
            foreach (var e in model.AllElements())
                foreach (var g in e.PropertyGroups)
                {
                    if (g.Source != "Tag") continue;
                    foreach (var tn in SheetTagNames)
                        if (g.Properties.TryGetValue(tn, out var v) && v is string s && s.Trim().Length > 0 && !context.ContainsKey(Camel(tn)))
                            context[Camel(tn)] = s.Trim();
                }
            if (context.TryGetValue("sheetTitleLine1", out var t1)) context["sheetTitle"] = t1;
            else if (context.TryGetValue("title", out var tt)) context["sheetTitle"] = tt;

            var prims = DisplayListBuilder.Build(file, model, new DisplayListOptions { ModelName = model.Name, IncludeReferences = opt.IncludeReferences });
            var groups = new Dictionary<(string? Ref, ulong Id), List<Primitive>>();
            var order = new List<(string? Ref, ulong Id)>();
            foreach (var p in prims)
            {
                if (p.Source == null) continue;
                var k = (p.ReferencePath, p.Source.Id);
                if (!groups.TryGetValue(k, out var list)) { list = new List<Primitive>(); groups[k] = list; order.Add(k); }
                list.Add(p);
            }

            foreach (var k in order)
            {
                var list = groups[k];
                var src = list[0].Source;
                string kind = KindOf(src);
                if (opt.SkipAnnotation && kind is "Text" or "TextNode" or "Dimension") continue;
                var f = new Feature
                {
                    Key = (k.Ref != null ? "ref:" + k.Ref + "/" : "") + ElementKey(k.Id),
                    Source = k.Ref == null ? source : source + ">" + k.Ref,
                    Container = model.Name,
                    Frame = frame,
                    Kind = kind,
                    Group = src.GraphicGroup,
                };
                foreach (var kv in context) f.Context[kv.Key] = kv.Value;
                if (k.Ref != null) f.Context["reference"] = k.Ref;

                // levels: the cell/complex header level is usually generic ("Default", "Level 0"); the meaning is in the
                // component levels, so the primary layer is the most used component level.
                var levelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in list) if (!string.IsNullOrEmpty(p.LevelName)) levelCounts[p.LevelName] = levelCounts.TryGetValue(p.LevelName, out var c) ? c + 1 : 1;
                var header = file.Levels.NameOf(src.LevelId);
                f.Layers.AddRange(levelCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key));
                f.Layer = list.Count == 1 || f.Layers.Count == 0 ? (list[0].LevelName is { Length: > 0 } ln ? ln : header) : f.Layers[0];
                if (!string.IsNullOrEmpty(header) && !f.Layers.Contains(header!)) f.Layers.Add(header!);
                f.Properties["headerLevel"] = header;

                f.Cell = src switch
                {
                    CellElement ce => ce.Name,
                    SharedCellElement sc => sc.Name ?? sc.Definition?.Name,
                    _ => null,
                };
                if (f.Cell != null) f.Cell = f.Cell.Trim();

                var texts = new StringBuilder();
                foreach (var p in list)
                    if (p.Kind == PrimitiveKind.Text && !string.IsNullOrWhiteSpace(p.Text) && texts.Length < opt.MaxText)
                    {
                        if (texts.Length > 0) texts.Append(" | ");
                        texts.Append(p.Text!.Trim());
                    }
                if (texts.Length > 0) f.Text = texts.Length > opt.MaxText ? texts.ToString(0, opt.MaxText) : texts.ToString();

                AddProperties(src, f);
                foreach (var p in list) AddGeometry(p, m, f.Geometry);
                f.Geometry.Invalidate();
                if (f.Geometry.IsEmpty) continue;
                set.Features.Add(f);
            }
        }

        private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s.Substring(1);

        private static string KindOf(Element e) => e.Type switch
        {
            ElementType.CellHeader => "Cell",
            ElementType.MeshHeader => "Mesh",
            _ => e.TypeName,
        };

        /// <summary>Flattens EC / item type / tag / linkage groups into properties (first value of a name wins).</summary>
        private static void AddProperties(Element e, Feature f)
        {
            var ecClasses = new List<object?>();
            foreach (var g in e.PropertyGroups)
            {
                switch (g.Source)
                {
                    case "EC":
                        if (g.ClassName != null && !ecClasses.Contains(g.ClassName)) ecClasses.Add(g.ClassName);
                        if (g.Schema != null && !f.Properties.ContainsKey("ecSchema")) f.Properties["ecSchema"] = g.Schema;
                        break;
                    case "Tag":
                        if (!f.Properties.ContainsKey("tagSet")) f.Properties["tagSet"] = g.Name;
                        break;
                    case "StringLinkage":
                        foreach (var kv in g.Properties) if (!f.Properties.ContainsKey("stringLinkage")) f.Properties["stringLinkage"] = kv.Value;
                        continue;
                    case "Database":
                        foreach (var kv in g.Properties) if (!f.Properties.ContainsKey(kv.Key)) f.Properties[kv.Key] = kv.Value;
                        continue;
                }
                foreach (var kv in g.Properties)
                    if (!f.Properties.ContainsKey(kv.Key)) f.Properties[kv.Key] = kv.Value;
            }
            if (ecClasses.Count > 0)
            {
                f.Properties["ecClass"] = ecClasses[0];
                f.Properties["ecClasses"] = ecClasses;
            }
        }

        private static Vec3 V(DgnKit.Geometry.DPoint3d p, double m) => new(p.X * m, p.Y * m, p.Z * m);

        private static void AddGeometry(Primitive p, double m, FeatureGeometry g)
        {
            switch (p.Kind)
            {
                case PrimitiveKind.Polyline:
                    if (p.Points.Count == 0) return;
                    if (p.Points.Count == 1) { g.Parts.Add(GeometryPart.Point(V(p.Points[0], m))); return; }
                    g.Parts.Add(p.Closed ? GeometryPart.Ring(p.Points.Select(q => V(q, m))) : GeometryPart.Line(p.Points.Select(q => V(q, m))));
                    return;
                case PrimitiveKind.Arc:
                {
                    var pts = p.ArcPoints(12).Select(q => V(q, m)).ToList();
                    bool full = Math.Abs(Math.Abs(p.SweepAngle) - 2 * Math.PI) < 1e-6;
                    g.Parts.Add(full ? GeometryPart.Ring(pts) : GeometryPart.Line(pts));
                    return;
                }
                case PrimitiveKind.Point:
                    foreach (var q in p.Points) g.Parts.Add(GeometryPart.Point(V(q, m)));
                    return;
                case PrimitiveKind.Text:
                    if (p.Points.Count > 0) g.Parts.Add(GeometryPart.Point(V(p.Points[0], m)));
                    return;
                case PrimitiveKind.Mesh:
                {
                    var r = p.Range();
                    if (r.IsEmpty) return;
                    var lo = V(r.Low, m); var hi = V(r.High, m);
                    g.Parts.Add(GeometryPart.Ring(new[] { new Vec3(lo.X, lo.Y, lo.Z), new Vec3(hi.X, lo.Y, lo.Z), new Vec3(hi.X, hi.Y, hi.Z), new Vec3(lo.X, hi.Y, hi.Z) }));
                    return;
                }
            }
        }
    }
}
