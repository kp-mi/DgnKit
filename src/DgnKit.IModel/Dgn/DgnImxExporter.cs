using System.Globalization;
using System.Security.Cryptography;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.Graphics;
using DgnKit.IModel.Imx;
using DgnKit.Model;

namespace DgnKit.IModel.Dgn;

public sealed class DgnImxOptions
{
    /// <summary>
    /// Treat 2D design models as spatial (bis:PhysicalModel, z = 0) instead of drawings. Useful for georeferenced 2D plan
    /// models; Bentley's own DGN connector defaults to drawings (its <c>Consider2dModelsSpatial</c> option, default false).
    /// </summary>
    public bool TwoDAsSpatial { get; set; }
    /// <summary>Convert elements on levels that are switched off (as invisible sub-categories). Default true.</summary>
    public bool IncludeHiddenLevels { get; set; } = true;
    /// <summary>Convert construction-class elements (as GeometryClass.Construction). Default true.</summary>
    public bool IncludeConstruction { get; set; } = true;
    /// <summary>Only convert models with these names (null = all non-dictionary models).</summary>
    public IReadOnlyCollection<string>? Models { get; set; }
    /// <summary>
    /// Semantic enrichment (e.g. from the logic layer, <see cref="Logic.LogicImx"/>): extra property groups per element
    /// key, plus relationship classes and relationships written after the elements. Relationships whose ends are not
    /// elements of this stream are dropped (counted in the report).
    /// </summary>
    public ImxEnrichment? Enrichment { get; set; }
}

/// <summary>Content added to an IMX stream on top of the faithful DGN mapping.</summary>
public sealed class ImxEnrichment
{
    public Dictionary<string, List<ImxPropertyGroup>> ElementProperties { get; } = new(StringComparer.Ordinal);
    public List<ImxRelationshipClass> RelationshipClasses { get; } = new();
    public List<ImxRelationship> Relationships { get; } = new();
}

public sealed class DgnImxReport
{
    public int Models { get; set; }
    public int Elements { get; set; }
    public int ChildElements { get; set; }
    public int SkippedEmpty { get; set; }
    /// <summary>Enrichment relationships not written because an end is not an element of the stream (e.g. in a reference).</summary>
    public int DroppedRelationships { get; set; }
    public Dictionary<string, int> Unsupported { get; } = new();
    public Dictionary<string, int> Records { get; set; } = new();
    public override string ToString() =>
        $"{Models} model(s), {Elements} element(s) (+{ChildElements} level-split children), {SkippedEmpty} without geometry" +
        (Unsupported.Count > 0 ? "; not converted: " + string.Join(", ", Unsupported.Select(k => $"{k.Key}×{k.Value}")) : "") +
        "; records: " + string.Join(", ", Records.Select(k => $"{k.Key}={k.Value}"));
    internal void Skip(string what) => Unsupported[what] = Unsupported.TryGetValue(what, out var n) ? n + 1 : 1;
}

/// <summary>
/// Maps a <see cref="DgnFile"/> onto BIS and writes it as IMX (see docs/IMODEL-INTERCHANGE.md):
/// <list type="bullet">
/// <item>levels → categories (one SpatialCategory and/or DrawingCategory each) whose default sub-category carries the level symbology;</item>
/// <item>3D design models → physical models; 2D design/drawing models → drawings; sheet models → sheets;</item>
/// <item>graphic elements → one element each with an exact geometry stream (lines, arcs, B-splines, complex chains as
/// paths, filled shapes as loops, text, meshes, cones) in metres; ByLevel symbology stays "inherit from sub-category";
/// cells and shared cells become one element, split into a parent + children when their components sit on different levels
/// (BIS allows one category per element);</item>
/// <item>EC instances, item types, tags, string and database linkages → property groups; V8 element ids → provenance.</item>
/// </list>
/// </summary>
public sealed class DgnImxExporter
{
    private readonly DgnFile _file;
    private readonly DgnImxOptions _opt;
    private readonly DgnImxReport _report = new();
    private readonly SortedDictionary<uint, ImxCategory> _categories = new();
    private readonly SortedSet<uint> _fonts = new();
    private readonly SortedSet<int> _styles = new();
    private readonly List<ImxModel> _models = new();
    private readonly List<ImxElement> _elements = new();

    public DgnImxExporter(DgnFile file, DgnImxOptions? options = null)
    {
        _file = file;
        _opt = options ?? new DgnImxOptions();
    }

    public static DgnImxReport Export(DgnFile file, string imxPath, DgnImxOptions? options = null)
    {
        using var w = ImxWriter.Create(imxPath);
        return new DgnImxExporter(file, options).Write(w);
    }

    public DgnImxReport Write(ImxWriter w)
    {
        var spatial = Range3d.Empty;
        foreach (var m in _file.Models)
        {
            if (m.IsDictionary) continue;
            if (_opt.Models != null && !_opt.Models.Contains(m.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var im = ConvertModel(m);
            if (im == null) continue;
            _models.Add(im);
            if (im.Type == "physical") spatial.Extend(im.Extents);
        }

        w.Write(Header(spatial));
        foreach (var id in _fonts)
        {
            var fi = _file.Fonts.ById.TryGetValue(id, out var f) ? f : null;
            w.Write(new ImxFont
            {
                Key = FontKey(id), Name = fi?.Name ?? _file.Fonts.NameOf(id),
                Type = fi?.Kind switch { FontKind.Rsc => "rsc", FontKind.Shx => "shx", FontKind.TrueType => "truetype", _ => id < 256 ? "rsc" : "truetype" },
            });
        }
        foreach (var n in _styles) w.Write(LineStyle(n));
        foreach (var c in _categories.Values) w.Write(c);
        foreach (var m in _models) w.Write(m);
        var enrich = _opt.Enrichment;
        if (enrich != null)
            foreach (var e in _elements)
                if (e.Parent == null && enrich.ElementProperties.TryGetValue(e.Key, out var extra))
                    e.Properties.AddRange(extra);
        foreach (var e in _elements) w.Write(e);
        if (enrich != null && enrich.Relationships.Count > 0)
        {
            var keys = new HashSet<string>(_elements.Select(e => e.Key), StringComparer.Ordinal);
            var rels = enrich.Relationships.Where(r => keys.Contains(r.SourceKey) && keys.Contains(r.TargetKey)).ToList();
            _report.DroppedRelationships = enrich.Relationships.Count - rels.Count;
            var used = new HashSet<string>(rels.Select(r => r.Class), StringComparer.Ordinal);
            foreach (var c in enrich.RelationshipClasses) if (used.Contains(c.Name)) w.Write(c);
            foreach (var r in rels) w.Write(r);
        }
        _report.Records = new Dictionary<string, int>(w.Counts);
        return _report;
    }

    // ------------------------------------------------------------------ header / tables

    private ImxHeader Header(Range3d spatial)
    {
        string? sha = null; long size = 0; DateTime? mod = null;
        if (_file.Path != null && File.Exists(_file.Path))
        {
            var fi = new FileInfo(_file.Path);
            size = fi.Length; mod = fi.LastWriteTimeUtc;
            using var s = File.OpenRead(_file.Path);
            sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }
        ImxGcs? gcs = null;
        var g = _file.GeoCoordinateSystem;
        if (g != null)
            gcs = new ImxGcs
            {
                Epsg = g.Epsg ?? GcsKeys.CsMapKeyToEpsg(g.Name), CsMapKey = g.Name, Wkt = g.Wkt, Description = g.Description,
                UnitMeters = g.UnitMeters, Source = g.Source,
            };
        return new ImxHeader
        {
            Generator = "DgnKit " + typeof(DgnFile).Assembly.GetName().Version,
            SourcePath = _file.Path ?? "", SourceName = Path.GetFileName(_file.Path ?? "untitled.dgn"),
            SourceFormat = _file.Format == DgnFormat.V7 ? "DGN V7" : "DGN V8", SourceFormatVersion = $"{_file.MajorVersion}.{_file.MinorVersion}",
            SourceSize = size, SourceModifiedUtc = mod, SourceSha256 = sha, SpatialExtents = spatial, Gcs = gcs,
        };
    }

    private static string FontKey(uint id) => "font:" + id.ToString(CultureInfo.InvariantCulture);
    private static string StyleKey(int n) => "ls:" + n.ToString(CultureInfo.InvariantCulture);
    private static string LevelKey(uint id) => "lvl:" + id.ToString(CultureInfo.InvariantCulture);

    private ImxLineStyle LineStyle(int n)
    {
        var def = _file.LineStyles.Find(n);
        string name = _file.LineStyles.NameOf(n);
        if (n >= 1 && n <= 7)
            return new ImxLineStyle { Key = StyleKey(n), Name = name, LinePixels = n, Source = new ImxSource("LineStyle", n.ToString(CultureInfo.InvariantCulture)) };
        List<ImxStroke>? strokes = null; double phase = 0;
        var mpm = (_file.DefaultModel?.Units ?? UnitInfo.Default).MetersPerMaster;
        if (def?.DashPatternMaster is { Length: > 0 } dashes)
        {
            strokes = dashes.Select((d, i) => new ImxStroke(d * mpm, i % 2 == 0)).ToList();
            phase = def.DashPhase * def.UnitScale * mpm;
        }
        return new ImxLineStyle
        {
            Key = StyleKey(n), Name = name, Strokes = strokes, Phase = phase, Annotation = def != null && !def.UnitsArePhysical,
            Approximation = LineStyleApproximation(def, strokes != null),
            Source = new ImxSource("LineStyle", n.ToString(CultureInfo.InvariantCulture))
            {
                Info = def == null ? null : new Dictionary<string, object?> { ["hasDefinition"] = def.HasDefinition, ["units"] = def.Units.ToString() },
            },
        };
    }

    /// <summary>
    /// What the IMX record (a single dash pattern, or continuous) loses against the source definition; null when nothing
    /// is lost (including genuinely continuous styles).
    /// </summary>
    private static string? LineStyleApproximation(LineStyleDefinition? def, bool hasStrokes)
    {
        if (def == null || !def.HasDefinition)
            return def?.IsContinuous == true ? null : "no definition in the source file (external resource or dgnlib); drawn continuous";
        var parts = def.Root!.SelfAndDescendants().ToList();
        var lost = new List<string>();
        if (parts.Any(c => c.Kind == LineStyleComponentKind.PointSymbol || (c.Kind == LineStyleComponentKind.LinePoint && c.Symbols.Count > 0)))
            lost.Add("point symbols");
        if (parts.Any(c => c.Kind == LineStyleComponentKind.RasterImage))
            lost.Add("raster image");
        if (parts.Count(c => c.Kind is LineStyleComponentKind.StrokePattern or LineStyleComponentKind.Internal && c.Strokes.Count > 0) > 1)
            lost.Add("parallel stroke patterns");
        return lost.Count == 0 ? null : $"{string.Join(" and ", lost)} not converted; drawn {(hasStrokes ? "with its main dash pattern" : "continuous")}";
    }

    private static uint Tbgr(RgbColor c, double transparency = 0)
    {
        uint t = (uint)Math.Clamp(Math.Round(transparency * 255), 0, 255);
        return (t << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
    }

    private uint ColorTbgr(uint index) => Tbgr(DisplayListBuilder.ColorOf(_file, index));

    private ImxCategory Category(uint levelId, string usage)
    {
        if (!_categories.TryGetValue(levelId, out var c))
        {
            var l = _file.Levels[levelId];
            bool sym = l?.HasSymbology == true;
            int style = sym ? l!.Style : 0;
            if (style != 0 && style != Symbology.StyleByLevel) _styles.Add(style);
            c = new ImxCategory
            {
                Key = LevelKey(levelId),
                Name = _file.Levels.NameOf(levelId),
                Description = l?.Description,
                Appearance = new ImxAppearance
                {
                    Color = sym ? ColorTbgr(l!.Color) : Tbgr(new RgbColor(255, 255, 255)),
                    Weight = sym ? (int)Math.Min(l!.Weight, 31) : 0,
                    Style = style != 0 && style != Symbology.StyleByLevel ? StyleKey(style) : null,
                    Priority = l?.Priority ?? 0,
                    Transparency = l?.Transparency ?? 0,
                    Invisible = l != null && sym && !l.IsDisplayed,
                    DontPlot = l != null && !l.IsPlot,
                },
                Source = new ImxSource("Level", levelId.ToString(CultureInfo.InvariantCulture))
                {
                    Info = l?.Code is uint code ? new Dictionary<string, object?> { ["code"] = code } : null,
                },
            };
            _categories[levelId] = c;
        }
        c.Usage.Add(usage);
        return c;
    }

    // ------------------------------------------------------------------ models

    private sealed class ModelCtx
    {
        public DgnModel Model = null!;
        public string Key = "";
        public bool Is3d;          // geometry dimensionality in the iModel
        public string Usage = "";  // "spatial" | "drawing"
        public Transform ToWorld;  // UOR → metres
        public double MetersPerUor;
        public Range3d Extents = Range3d.Empty;
    }

    private ImxModel? ConvertModel(DgnModel m)
    {
        string type = m.Type == DgnModelType.Sheet ? "sheet"
            : m.Is3d || _opt.TwoDAsSpatial ? "physical" : "drawing";
        double mpm = m.Units.MetersPerMaster > 0 ? m.Units.MetersPerMaster : 1;
        double upm = m.Units.UorPerMaster > 0 ? m.Units.UorPerMaster : 1;
        var ctx = new ModelCtx
        {
            Model = m, Key = "model:" + m.Id.ToString(CultureInfo.InvariantCulture), Is3d = type == "physical",
            Usage = type == "physical" ? "spatial" : "drawing", MetersPerUor = mpm / upm,
            ToWorld = new Transform(Matrix3.Scale(mpm / upm), m.GlobalOrigin * (-mpm / upm)),
        };
        int before = _elements.Count;
        foreach (var e in m.Elements) ConvertTopLevel(ctx, e);
        if (_elements.Count == before && m.Elements.Count == 0) return null; // empty model
        _report.Models++;
        return new ImxModel
        {
            Key = ctx.Key, Name = string.IsNullOrEmpty(m.Name) ? "Model " + m.Id : m.Name, Description = m.Description, Type = type,
            Is3d = ctx.Is3d, MasterUnit = m.Units.Master.Label, MetersPerMaster = mpm, Extents = ctx.Extents,
            Source = new ImxSource("Model", m.Id.ToString(CultureInfo.InvariantCulture))
            {
                Info = new Dictionary<string, object?> { ["dgnType"] = m.Type.ToString(), ["is3d"] = m.Is3d },
            },
        };
    }

    // ------------------------------------------------------------------ elements

    /// <summary>Symbology inherited from a cell header (for ByCell components).</summary>
    private sealed record Inherit(uint Level, uint Color, uint Weight, int Style);

    /// <summary>Per-level geometry collector for one source element.</summary>
    private sealed class Pieces
    {
        public readonly Dictionary<uint, GeometryStream> ByLevel = new();
        public readonly List<uint> Order = new();
        public readonly Dictionary<uint, GsAppearance?> LastAppearance = new();
        public readonly bool Is3d;
        public Pieces(bool is3d) { Is3d = is3d; }

        public GeometryStream For(uint level)
        {
            if (!ByLevel.TryGetValue(level, out var s)) { s = new GeometryStream(Is3d); ByLevel[level] = s; Order.Add(level); LastAppearance[level] = null; }
            return s;
        }

        /// <summary>Adds a primitive after the symbology change it needs (appearance entries only when the symbology changes).</summary>
        public void Add(uint level, GsAppearance app, GsFill? fill, GsStyleMod? mod, GsPrimitive prim)
        {
            var s = For(level);
            var last = LastAppearance[level];
            bool needApp = last == null ? !app.IsDefault || fill != null || mod != null : !last.Equals(app) || fill != null || mod != null;
            if (needApp) { s.Add(app); LastAppearance[level] = app; }
            if (mod != null) s.Add(mod);
            if (fill != null) s.Add(fill);
            s.Add(prim);
            // fill / style modifiers apply until the next appearance entry → force one before the next primitive
            if (fill != null || mod != null) LastAppearance[level] = new GsAppearance { SubCategory = "\u0000reset" };
        }
    }

    private void ConvertTopLevel(ModelCtx ctx, Element e)
    {
        if (e.IsDeleted || !e.IsGraphic || e.IsInvisible) return;
        if (e is SharedCellDefinition) return;
        if (e is TagElement tag && !tag.Displayed) return;          // tag values travel as properties of their target
        if (!_opt.IncludeConstruction && e.Class == ElementClass.Construction) return;

        var pieces = new Pieces(ctx.Is3d);
        uint headerLevel = e.LevelId;
        Collect(ctx, e, ctx.ToWorld, null, pieces, depth: 0);
        if (pieces.Order.Count == 0) { _report.SkippedEmpty++; return; }

        // main element = the header's level if it carries geometry, else the level with the most primitives
        uint main = pieces.ByLevel.ContainsKey(headerLevel) ? headerLevel
            : pieces.Order.OrderByDescending(l => pieces.ByLevel[l].Entries.Count(x => x is GsPrimitive)).First();
        string key = "el:" + e.Id.ToString(CultureInfo.InvariantCulture);
        string role = IsAnnotation(e) ? "annotation" : "geometry";
        var info = new Dictionary<string, object?> { ["type"] = e.TypeName, ["typeNumber"] = e.RawType };
        if (e.Class != ElementClass.Primary) info["class"] = e.Class.ToString();
        if (e.GraphicGroup != 0) info["graphicGroup"] = e.GraphicGroup;
        string? label = Label(e);
        if (label != null && e is CellElement or SharedCellElement) info["cell"] = label;

        var mainEl = MakeElement(ctx, key, e, main, pieces.ByLevel[main], role, null, label, new ImxSource("Element", e.Id.ToString(CultureInfo.InvariantCulture)) { Info = info });
        foreach (var g in e.PropertyGroups)
            mainEl.Properties.Add(new ImxPropertyGroup { Source = g.Source, Name = g.Name, Schema = g.Schema, ClassName = g.ClassName, Properties = new Dictionary<string, object?>(g.Properties) });
        _elements.Add(mainEl);
        _report.Elements++;

        foreach (var lvl in pieces.Order)
        {
            if (lvl == main) continue;
            var child = MakeElement(ctx, key + "/" + LevelKey(lvl), e, lvl, pieces.ByLevel[lvl], role, key, label,
                new ImxSource("ElementLevelPart", e.Id.ToString(CultureInfo.InvariantCulture) + "/" + lvl.ToString(CultureInfo.InvariantCulture)));
            _elements.Add(child);
            _report.ChildElements++;
        }
    }

    private ImxElement MakeElement(ModelCtx ctx, string key, Element src, uint level, GeometryStream gs, string role, string? parent, string? label, ImxSource source)
    {
        var r = gs.Range();
        ctx.Extents.Extend(r);
        // origin: range centre snapped to 1 mm keeps local coordinates small (float precision in graphics) and stable
        var c = r.IsEmpty ? DPoint3d.Zero : r.Center;
        var origin = new DPoint3d(Math.Round(c.X, 3), Math.Round(c.Y, 3), ctx.Is3d ? Math.Round(c.Z, 3) : 0);
        return new ImxElement
        {
            Key = key, Model = ctx.Key, Category = Category(level, ctx.Usage).Key, Role = role, Parent = parent, UserLabel = label,
            Origin = origin, Geometry = gs, Source = source, SourceModifiedUtc = src.LastModified,
        };
    }

    private static bool IsAnnotation(Element e) => e is TextElement or TextNodeElement or DimensionElement or TagElement;

    private static string? Label(Element e) => e switch
    {
        CellElement c => c.Name,
        SharedCellElement s => s.Name ?? s.Definition?.Name,
        TextElement t => Trunc(t.Text),
        TextNodeElement tn => Trunc(tn.Text.Replace('\n', ' ')),
        _ => null,
    };

    private static string? Trunc(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= 80 ? s : s[..80];

    // ------------------------------------------------------------------ symbology

    private (uint Level, GsAppearance App, GsFill? Fill, GsStyleMod? Mod) Symb(ModelCtx ctx, Element e, Inherit? parent)
    {
        uint level = e.LevelId == 0 && parent != null ? parent.Level : e.LevelId;
        bool v7 = _file.Format == DgnFormat.V7;

        // colour / weight / style relative to the element's own category (ByLevel ⇒ inherit = null)
        uint? color = null; int? weight = null; string? style = null;
        uint c = e.Color;
        if (!v7 && c == Symbology.ColorByCell && parent != null) c = parent.Color;
        if (v7 || c != Symbology.ColorByLevel && c != Symbology.ColorByCell) color = ColorTbgr(c);
        else if (c == Symbology.ColorByLevel && parent != null && parent.Level != level && e.Color == Symbology.ColorByCell)
            color = ColorTbgr(_file.Levels[parent.Level]?.Color ?? 0);

        uint w = e.Weight;
        if (!v7 && w == Symbology.WeightByCell && parent != null) w = parent.Weight;
        if (v7 || w != Symbology.WeightByLevel && w != Symbology.WeightByCell) weight = (int)Math.Min(w, 31);
        else if (w == Symbology.WeightByLevel && parent != null && parent.Level != level && e.Weight == Symbology.WeightByCell)
            weight = (int)Math.Min(_file.Levels[parent.Level]?.Weight ?? 0, 31);

        int st = e.Style;
        if (!v7 && st == Symbology.StyleByCell && parent != null) st = parent.Style;
        if (v7 || st != Symbology.StyleByLevel && st != Symbology.StyleByCell)
        {
            if (st != 0) _styles.Add(st);
            style = StyleKey(st); // "ls:0" = explicitly continuous (overrides a styled level)
        }
        else if (st == Symbology.StyleByLevel && parent != null && parent.Level != level && e.Style == Symbology.StyleByCell)
        {
            int ls = _file.Levels[parent.Level]?.Style ?? 0;
            if (ls != 0) _styles.Add(ls);
            style = StyleKey(ls);
        }

        int geomClass = e.Class switch
        {
            ElementClass.Construction or ElementClass.ConstructionRule => 1,
            ElementClass.Dimension => 2,
            ElementClass.PatternComponent or ElementClass.LinearPatterned => 3,
            _ => 0,
        };
        var app = new GsAppearance
        {
            Color = color, Weight = weight, Style = style == StyleKey(0) && (_file.Levels[level]?.Style ?? 0) == 0 ? null : style,
            // MicroStation's range is ±500 (as in iModels: net priority = sub-category + element); V7 has none
            DisplayPriority = !ctx.Is3d && !v7 && e.Priority != 0 && e.Priority >= -500 && e.Priority <= 500 ? e.Priority : null,
            GeometryClass = geomClass == 0 ? null : geomClass,
        };

        GsFill? fill = null;
        if (e.Fill != null)
            fill = new GsFill
            {
                Display = 1, Color = e.Fill.Color.HasValue ? ColorTbgr(e.Fill.Color.Value) : null, UseOutlineColor = !e.Fill.Color.HasValue,
                Transparency = e.Fill.Transparency > 0 ? e.Fill.Transparency : null,
            };

        GsStyleMod? mod = null;
        var sp = e.StyleParams;
        if (sp != null && (sp.Modifiers & 0x7F) != 0)
        {
            double toM = ctx.MetersPerUor; // width/phase modifiers are in UORs
            mod = new GsStyleMod
            {
                Scale = sp.HasScale ? sp.Scale : null,
                DashScale = (sp.Modifiers & 0x02) != 0 ? sp.DashScale : null,
                GapScale = (sp.Modifiers & 0x04) != 0 ? sp.GapScale : null,
                StartWidth = (sp.Modifiers & 0x08) != 0 ? sp.StartWidth * toM : null,
                EndWidth = (sp.Modifiers & 0x10) != 0 ? sp.EndWidth * toM : null,
                DistPhase = (sp.Modifiers & 0x20) != 0 ? sp.Phase * toM : null,
                FractPhase = (sp.Modifiers & 0x40) != 0 ? sp.FractionPhase : null,
                CenterPhase = sp.CenterPhase,
            };
        }
        return (level, app, fill, mod);
    }

    // ------------------------------------------------------------------ geometry

    private void Collect(ModelCtx ctx, Element e, Transform T, Inherit? parent, Pieces pieces, int depth)
    {
        if (depth > 32 || e.IsDeleted) return;
        if (depth > 0 && e.IsInvisible) return;
        if (!_opt.IncludeConstruction && e.Class == ElementClass.Construction) return;
        var (level, app, fill, mod) = Symb(ctx, e, parent);
        if (!_opt.IncludeHiddenLevels && _file.Levels[level] is { HasSymbology: true, IsDisplayed: false }) return;
        void Add(GsPrimitive p, bool filled = false) => pieces.Add(level, app, filled ? fill : null, mod, p);

        switch (e)
        {
            case LineElement l:
            {
                var p0 = T.Apply(l.Start); var p1 = T.Apply(l.End);
                Add(p0.DistanceTo(p1) < 1e-12 ? new GsPointString(new[] { p0 }) : new GsLineSegment(p0, p1));
                break;
            }
            case LineStringElement ls when ls.Points.Count > 0:
            {
                if (ls.IsPointString)
                {
                    var vertices = ls.Points.Where(p => !p.IsDisconnect).Select(T.Apply).ToList();
                    if (vertices.Count > 0) Add(new GsPointString(vertices));
                    break;
                }
                // a disconnected line string (disconnect vertices separate its runs) becomes one open line string per run
                var runs = DPoint3d.SplitAtDisconnects(ls.Points);
                bool closed = ls.IsClosed && runs.Count == 1;
                foreach (var run in runs)
                {
                    var pts = Dedup((ls.IsCurve ? Curves.StrokeCurveElement(run) : run).Select(T.Apply).ToList());
                    if (pts.Count == 1) { Add(new GsPointString(pts)); continue; }
                    if (closed && pts.Count >= 3 && pts[0].DistanceTo(pts[^1]) > 1e-12) pts.Add(pts[0]);
                    var line = new GsLineString(pts);
                    if (closed && fill != null && pts.Count >= 4) { var loop = new GsCurveChain { Closed = true }; loop.Curves.Add(line); Add(loop, filled: true); }
                    else Add(line);
                }
                break;
            }
            case ArcElement a:
            {
                var arc = Arc(a, T);
                if (a.IsFullEllipse && fill != null) { var loop = new GsCurveChain { Closed = true }; loop.Curves.Add(arc); Add(loop, filled: true); }
                else Add(arc);
                break;
            }
            case BSplineCurveElement bc:
            {
                var cv = BSpline(bc, T);
                if (cv != null) Add(cv);
                break;
            }
            case ComplexElement cx:
            {
                // a component line string with disconnect vertices breaks the chain into separate open chains
                var chains = new List<List<GsCurve>> { new() };
                foreach (var c in cx.Children)
                {
                    if (c.IsDeleted) continue;
                    if (c is LineStringElement l)
                    {
                        var runs = DPoint3d.SplitAtDisconnects(l.Points);
                        for (int i = 0; i < runs.Count; i++)
                        {
                            if (i > 0) chains.Add(new List<GsCurve>());
                            var pts = Dedup((l.IsCurve ? Curves.StrokeCurveElement(runs[i]) : runs[i]).Select(T.Apply).ToList());
                            if (pts.Count >= 2) chains[^1].Add(new GsLineString(pts));
                        }
                        continue;
                    }
                    GsCurve? cv = c switch
                    {
                        LineElement ln => new GsLineSegment(T.Apply(ln.Start), T.Apply(ln.End)),
                        ArcElement arc => Arc(arc, T),
                        BSplineCurveElement b => BSpline(b, T),
                        _ => null,
                    };
                    if (cv != null) chains[^1].Add(cv);
                }
                bool closed = cx.IsClosed && fill != null && chains.Count == 1;
                foreach (var curves in chains.Where(cs => cs.Count > 0))
                {
                    var chain = new GsCurveChain { Closed = closed };
                    chain.Curves.AddRange(curves);
                    Add(chain, filled: closed);
                }
                break;
            }
            case TextElement t:
                AddText(t, T, level, app, mod, pieces);
                break;
            case TextNodeElement tn:
                foreach (var c in tn.Children) Collect(ctx, c, T, new Inherit(level, e.Color, e.Weight, e.Style), pieces, depth + 1);
                break;
            case CellElement cell:
            {
                var inh = new Inherit(level, e.Color, e.Weight, e.Style);
                foreach (var c in cell.Children) Collect(ctx, c, T, inh, pieces, depth + 1);
                break;
            }
            case SharedCellElement sc:
            {
                var def = sc.Definition;
                if (def == null) { _report.Skip("SharedCell without definition"); break; }
                var inst = new Transform(sc.Transform, sc.Origin - sc.Transform.Multiply(def.Origin));
                var inh = new Inherit(level, e.Color, e.Weight, e.Style);
                foreach (var c in def.Children) Collect(ctx, c, T * inst, inh, pieces, depth + 1);
                break;
            }
            case MeshElement me when me.Vertices.Count > 0:
                Add(new GsMesh { Points = me.Vertices.Select(T.Apply).ToList(), Faces = me.Faces.Where(f => f.Length >= 3).ToList() });
                break;
            case ConeElement cone:
            {
                double s = T.Matrix.AverageScale;
                Add(new GsCone { Start = T.Apply(cone.Center1), End = T.Apply(cone.Center2), StartRadius = cone.Radius1 * s, EndRadius = cone.Radius2 * s });
                break;
            }
            case DimensionElement dim:
                AddDimension(ctx, dim, T, level, app, pieces);
                foreach (var c in dim.Children) Collect(ctx, c, T, new Inherit(level, e.Color, e.Weight, e.Style), pieces, depth + 1);
                break;
            case TagElement tag when tag.Displayed && tag.Value != null && tag.TextHeight > 0:
            {
                string txt = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? "";
                if (txt.Length == 0) break;
                double sc = T.Matrix.XYScale > 0 ? T.Matrix.XYScale : T.Matrix.AverageScale;
                if (TextTooSmall(tag.TextHeight * sc)) break;
                _fonts.Add(tag.FontId);
                Add(new GsText
                {
                    Text = txt, Font = FontKey(tag.FontId), Height = tag.TextHeight * sc,
                    WidthFactor = tag.TextHeight > 0 && tag.TextWidth > 0 ? tag.TextWidth / tag.TextHeight : 1,
                    Origin = T.Apply(tag.Origin), Rotation = Rigid(T.Matrix),
                });
                break;
            }
            case MultilineElement:
                _report.Skip("Multiline");
                break;
            default:
                if (e.Children.Count > 0)
                {
                    var inh = new Inherit(level, e.Color, e.Weight, e.Style);
                    foreach (var c in e.Children) Collect(ctx, c, T, inh, pieces, depth + 1);
                }
                else if (depth == 0) _report.Skip(e.TypeName);
                break;
        }
    }

    private static List<DPoint3d> Dedup(List<DPoint3d> pts)
    {
        var o = new List<DPoint3d>(pts.Count);
        foreach (var p in pts) if (o.Count == 0 || o[^1].DistanceTo(p) > 1e-12) o.Add(p);
        return o;
    }

    private static GsArc Arc(ArcElement a, Transform T) => new()
    {
        Center = T.Apply(a.Origin),
        VectorX = T.ApplyVector(a.Rotation.Column(0) * a.PrimaryAxis),
        VectorY = T.ApplyVector(a.Rotation.Column(1) * a.SecondaryAxis),
        Start = a.StartAngle,
        Sweep = a.IsFullEllipse ? 2 * Math.PI : a.SweepAngle,
    };

    /// <summary>Open B-splines become clamped bcurves (MicroStation knot conventions); closed/periodic ones are stroked.</summary>
    private static GsCurve? BSpline(BSplineCurveElement bc, Transform T)
    {
        if (bc.Poles.Count < 2) return null;
        int order = Math.Max(2, Math.Min(bc.Order, bc.Poles.Count));
        bool rational = bc.Rational && bc.Weights.Count == bc.Poles.Count;
        if (bc.Closed)
        {
            var pts = Dedup(Curves.StrokeBSpline(bc, 0).Select(T.Apply).ToList());
            return pts.Count >= 2 ? new GsLineString(pts) : null;
        }
        List<double> knots;
        int n = bc.Poles.Count;
        if (!bc.InteriorKnotsOnly && bc.Knots.Count == n + order) knots = bc.Knots.ToList();
        else
        {
            int interior = n - order;
            var inner = bc.Knots.Count == interior ? bc.Knots.ToList()
                : Enumerable.Range(1, Math.Max(0, interior)).Select(i => (double)i / (interior + 1)).ToList();
            knots = Enumerable.Repeat(0.0, order).Concat(inner).Concat(Enumerable.Repeat(1.0, order)).ToList();
        }
        // MicroStation stores rational poles pre-multiplied by their weights: back to Cartesian, then transform.
        var poles = bc.Poles.Select((p, i) => rational && bc.Weights[i] != 0 ? p / bc.Weights[i] : p).Select(T.Apply).ToList();
        return new GsBSpline { Poles = poles, Weights = rational ? bc.Weights.ToList() : null, Knots = knots, Order = order };
    }

    /// <summary>Normalised rotation part of a matrix (columns scaled to unit length, right-handed).</summary>
    private static Matrix3 Rigid(Matrix3 m)
    {
        var c0 = m.Column(0).Normalized(); var c1 = m.Column(1).Normalized(); var c2 = c0.Cross(c1).Normalized();
        c1 = c2.Cross(c0).Normalized();
        return new Matrix3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);
    }

    /// <summary>
    /// Smallest text height (metres) IMX carries: numbers are written to 9 decimals, and iTwin.js rejects the whole
    /// GeometryStream of an element that contains a zero-height text string (its other geometry would be lost too).
    /// </summary>
    private const double MinTextHeight = 1e-9;

    private bool TextTooSmall(double height)
    {
        if (height >= MinTextHeight) return false;
        _report.Skip("Text (zero height)");
        return true;
    }

    private void AddText(TextElement t, Transform T, uint level, GsAppearance app, GsStyleMod? mod, Pieces pieces)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        var m = T.Matrix * t.Rotation;
        double sc = T.Matrix.XYScale > 0 ? T.Matrix.XYScale : T.Matrix.AverageScale;
        if (TextTooSmall(t.Height * sc)) return;
        _fonts.Add(t.FontId);
        pieces.Add(level, app, null, null, new GsText
        {
            Text = t.Text, Font = FontKey(t.FontId), Height = t.Height * sc,
            WidthFactor = t.Height > 0 && t.Width > 0 ? t.Width / t.Height : 1,
            Origin = T.Apply(t.Origin), Rotation = Rigid(m),
        });
    }

    private void AddDimension(ModelCtx ctx, DimensionElement dim, Transform T, uint level, GsAppearance app, Pieces pieces)
    {
        var graphics = DimensionGraphics.Build(dim, _file.Fonts.NameOf(dim.FontId));
        if (graphics.Count == 0) { _report.Skip("Dimension (no graphics)"); return; }
        double sc = T.Matrix.XYScale > 0 ? T.Matrix.XYScale : T.Matrix.AverageScale;
        var dimApp = app; // the element's class (normally Dimension → GeometryClass 2) was mapped by Symb
        foreach (var g in graphics)
        {
            switch (g.Kind)
            {
                case DimensionGraphic.GraphicKind.Line:
                case DimensionGraphic.GraphicKind.Polygon:
                {
                    var pts = Dedup(g.Points.Select(T.Apply).ToList());
                    if (pts.Count < 2) break;
                    if (g.Kind == DimensionGraphic.GraphicKind.Polygon && pts[0].DistanceTo(pts[^1]) > 1e-12) pts.Add(pts[0]);
                    var line = new GsLineString(pts);
                    if (g.Filled && pts.Count >= 4)
                    {
                        var loop = new GsCurveChain { Closed = true }; loop.Curves.Add(line);
                        pieces.Add(level, dimApp, new GsFill { Display = 2, UseOutlineColor = true }, null, loop);
                    }
                    else pieces.Add(level, dimApp, null, null, line);
                    break;
                }
                case DimensionGraphic.GraphicKind.Circle:
                {
                    var arc = new GsArc
                    {
                        Center = T.Apply(g.Center), VectorX = T.ApplyVector(new DPoint3d(g.Radius, 0, 0)), VectorY = T.ApplyVector(new DPoint3d(0, g.Radius, 0)),
                    };
                    if (g.Filled) { var loop = new GsCurveChain { Closed = true }; loop.Curves.Add(arc); pieces.Add(level, dimApp, new GsFill { Display = 2, UseOutlineColor = true }, null, loop); }
                    else pieces.Add(level, dimApp, null, null, arc);
                    break;
                }
                case DimensionGraphic.GraphicKind.Text when !string.IsNullOrEmpty(g.Text):
                {
                    if (TextTooSmall(g.TextHeight * sc)) break;
                    _fonts.Add(dim.FontId);
                    var c0 = T.ApplyVector(g.TextDirection).Normalized(); var c1 = T.ApplyVector(g.TextUp).Normalized(); var c2 = c0.Cross(c1).Normalized();
                    pieces.Add(level, dimApp, null, null, new GsText
                    {
                        Text = g.Text!, Font = FontKey(dim.FontId), Height = g.TextHeight * sc,
                        WidthFactor = g.TextHeight > 0 && g.TextWidth > 0 ? g.TextWidth / g.TextHeight : 1,
                        Origin = T.Apply(g.Points[0]), Rotation = new Matrix3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z),
                    });
                    break;
                }
            }
        }
    }
}
