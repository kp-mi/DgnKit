using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Elements;
using DgnKit.Geometry;
using DgnKit.Model;

namespace DgnKit.Graphics
{
    /// <summary>
    /// Flattens a model (plus optional reference attachments) into <see cref="Primitive"/>s in root master units:
    /// complex elements are merged, cells/shared cells instanced, ByLevel/ByCell symbology resolved, curves kept exact
    /// where exporters can use them (arcs) and stroked otherwise (B-splines, curve strings).
    /// </summary>
    public static class DisplayListBuilder
    {
        private sealed class Ctx
        {
            public DgnFile File = null!;          // file owning the elements being drawn (levels, colors, styles)
            public DgnModel Model = null!;        // model owning the elements being drawn (units)
            public double UorToRootMaster;        // length scale: current model UOR → root master (attachment scales only)
            public bool InLineStyleSymbol;        // drawing a line style point symbol (no nested symbols)
            public DgnModel RootModel = null!;
            public DisplayListOptions Opt = null!;
            public Transform ToRootMaster;         // current element UOR → root master units
            public string? RefPath;
            public string ModelName = "";
            public double ChordTol;
            public List<Primitive> Out = null!;
            public int Depth;
        }

        public static List<Primitive> Build(DgnFile file, DisplayListOptions? opt = null)
        {
            opt ??= new DisplayListOptions();
            var model = opt.ModelName != null ? file.FindModel(opt.ModelName) ?? throw new ArgumentException($"Model '{opt.ModelName}' not found") : file.DefaultModel;
            if (model == null) return new List<Primitive>();
            return Build(file, model, opt);
        }

        public static List<Primitive> Build(DgnFile file, DgnModel model, DisplayListOptions opt)
        {
            var output = new List<Primitive>();
            double upm = model.Units.UorPerMaster;
            var toMaster = new Transform(Matrix3.Scale(1.0 / upm), model.GlobalOrigin * (-1.0 / upm));
            var ctx = new Ctx { File = file, Model = model, RootModel = model, Opt = opt, ToRootMaster = toMaster, UorToRootMaster = 1.0 / upm, ModelName = model.Name, Out = output };
            ctx.ChordTol = opt.ChordTolerance > 0 ? opt.ChordTolerance : AutoTolerance(model);
            DrawModel(ctx, model, new HashSet<DgnModel>());
            return output;
        }

        private static double AutoTolerance(DgnModel m)
        {
            // ~1/2000 of the model extent, in master units.
            var r = Range3d.Empty;
            foreach (var e in m.Elements.Take(5000)) if (!e.StoredRange.IsEmpty) r.Extend(e.StoredRange);
            double ext = r.IsEmpty ? 0 : Math.Max(r.XSize, r.YSize) / m.Units.UorPerMaster;
            return ext > 0 ? ext / 4000.0 : 0.01;
        }

        private static void DrawModel(Ctx ctx, DgnModel model, HashSet<DgnModel> visiting)
        {
            if (!visiting.Add(model)) return;
            foreach (var e in model.Elements) Draw(ctx, e, ctx.ToRootMaster, e, null, null);
            if (ctx.Opt.IncludeReferences && ctx.Depth < ctx.Opt.MaxReferenceDepth)
            {
                foreach (var a in model.References)
                {
                    if (!a.Displayed || a.LoadedModel == null || a.Loaded == null) continue;
                    var sub = new Ctx
                    {
                        File = a.Loaded, Model = a.LoadedModel, RootModel = ctx.RootModel, Opt = ctx.Opt, Out = ctx.Out, ChordTol = ctx.ChordTol,
                        UorToRootMaster = ctx.UorToRootMaster * (a.Scale > 0 && !double.IsInfinity(a.Scale) ? a.Scale : 1),
                        ToRootMaster = ctx.ToRootMaster * a.ToMasterUors,
                        RefPath = (ctx.RefPath == null ? "" : ctx.RefPath + "/") + (a.LogicalName ?? a.FileName ?? "ref"),
                        ModelName = a.LoadedModel.Name, Depth = ctx.Depth + 1,
                    };
                    DrawModel(sub, a.LoadedModel, visiting);
                }
            }
            visiting.Remove(model);
        }

        private static bool Skip(Ctx ctx, Element e)
        {
            if (e.IsDeleted) return true;
            if (ctx.InLineStyleSymbol) return false; // symbol geometry belongs to the line being drawn (its level/class)
            if (e.IsInvisible && !ctx.Opt.IncludeInvisible) return true;
            if (!ctx.Opt.IncludeConstruction && e.Class == ElementClass.Construction) return true;
            if (ctx.Opt.RespectLevelDisplay && e.IsGraphic)
            {
                var lvl = ctx.File.Levels[e.LevelId];
                if (lvl != null && lvl.HasSymbology && !lvl.IsDisplayed) return true;
            }
            return false;
        }

        private static ResolvedSymbology Resolve(Ctx ctx, Element e, ResolvedSymbology? parent, uint levelId)
        {
            var lvl = ctx.File.Levels[levelId];
            var s = new ResolvedSymbology { Priority = e.Priority, Class = e.Class };
            uint c = e.Color;
            if (ctx.File.Format == DgnFormat.V7) s.ColorIndex = c;
            else if (c == Symbology.ColorByLevel) s.ColorIndex = lvl?.Color ?? 0;
            else if (c == Symbology.ColorByCell) s.ColorIndex = parent?.ColorIndex ?? 0;
            else s.ColorIndex = c;
            s.Color = ColorOf(ctx.File, s.ColorIndex);

            uint w = e.Weight;
            if (ctx.File.Format != DgnFormat.V7 && w == Symbology.WeightByLevel) s.WeightIndex = (int)(lvl?.Weight ?? 0);
            else if (ctx.File.Format != DgnFormat.V7 && w == Symbology.WeightByCell) s.WeightIndex = parent?.WeightIndex ?? 0;
            else s.WeightIndex = (int)Math.Min(w, 31);
            if (s.WeightIndex < 0 || s.WeightIndex > 31) s.WeightIndex = 0;

            int st = e.Style;
            if (ctx.File.Format != DgnFormat.V7 && st == Symbology.StyleByLevel) s.StyleNumber = lvl?.Style ?? 0;
            else if (ctx.File.Format != DgnFormat.V7 && st == Symbology.StyleByCell) s.StyleNumber = parent?.StyleNumber ?? 0;
            else s.StyleNumber = st;
            if (s.StyleNumber == Symbology.StyleByLevel || s.StyleNumber == Symbology.StyleByCell) s.StyleNumber = 0;
            s.StyleName = ctx.File.LineStyles.NameOf(s.StyleNumber);
            ResolveCustomStyle(ctx, e, parent, st == Symbology.StyleByCell, s);

            if (e.Fill != null)
            {
                s.Filled = true;
                s.FillColor = e.Fill.Color.HasValue ? ColorOf(ctx.File, e.Fill.Color.Value) : s.Color;
                s.Transparency = e.Fill.Transparency;
            }
            else if (parent != null && parent.Filled && e.Parent is ComplexElement)
            {
                s.Filled = true; s.FillColor = parent.FillColor;
            }
            return s;
        }

        /// <summary>
        /// Custom line style → dash pattern in root master units: definition units (master → × uorPerMaster of the
        /// element's model; UOR as is) × element style scale × the model's line style scale for non-physical styles
        /// (physical styles are true size), then the element model's UOR → root master scale. Matches MicroStation's
        /// stroked geometry (<c>LineStyleTests.DroppedStrokes_MatchMicroStation</c>).
        /// </summary>
        private static void ResolveCustomStyle(Ctx ctx, Element e, ResolvedSymbology? parent, bool byCell, ResolvedSymbology s)
        {
            if (byCell && parent != null)
            {
                s.LineStyleScale = parent.LineStyleScale;
                s.LineStyle = parent.LineStyle; s.DashPattern = parent.DashPattern; s.DashPhase = parent.DashPhase;
                s.LineStyleWidth = parent.LineStyleWidth; s.LineStyleToMaster = parent.LineStyleToMaster;
                return;
            }
            s.LineStyleScale = e.StyleParams != null && e.StyleParams.HasScale ? e.StyleParams.Scale : 1;
            if (s.StyleNumber >= 0 && s.StyleNumber <= 7) return;
            var def = ctx.File.LineStyles.Find(s.StyleNumber);
            if (def == null) return;
            s.LineStyle = def;
            if (!def.UnitsArePhysical && ctx.Model.LineStyleScale > 0) s.LineStyleScale *= ctx.Model.LineStyleScale;
            double toUor = LineStyleDefinition.DefinitionToUor(def.Units, ctx.Model.Units.UorPerMaster);
            double k = toUor * s.LineStyleScale * ctx.UorToRootMaster;
            if (!(k > 0) || double.IsInfinity(k)) return;
            s.LineStyleToMaster = k;
            s.LineStyleWidth =LineStyleDefinition.UniformWidth(LineStyleDefinition.MainStrokePattern(def.Root)) * k;
            if (def.DashPattern == null) return;
            s.DashPattern = def.DashPattern.Select(d => d * k).ToArray();
            double phase = def.DashPhase, total = def.DashPattern.Sum();
            // element distance phase is in the model's master units whatever the style's units (MicroStation drop
            // geometry: UOR-unit styles shift by phase × uorPerMaster × scale); fraction phase = share of the pattern (⚠️)
            if (e.StyleParams != null && (e.StyleParams.Modifiers & 0x20) != 0) phase += e.StyleParams.Phase * ctx.Model.Units.UorPerMaster / toUor;
            if (e.StyleParams != null && (e.StyleParams.Modifiers & 0x40) != 0) phase += e.StyleParams.FractionPhase * total;
            s.DashPhase = total > 0 ? (((phase % total) + total) % total) * k : 0;
        }

        private const int MaxSymbolsPerLine = 2000;

        /// <summary>Line-point components of a style with their accumulated perpendicular compound offsets (definition units).</summary>
        private static IEnumerable<(LineStyleComponent LinePoint, double Offset)> LinePoints(LineStyleComponent c, double offset, int depth)
        {
            if (depth > 6) yield break;
            if (c.Kind == LineStyleComponentKind.LinePoint) { yield return (c, offset); yield break; }
            if (c.Kind != LineStyleComponentKind.Compound) yield break;
            for (int i = 0; i < c.Children.Count; i++)
                foreach (var x in LinePoints(c.Children[i], offset + (i < c.ChildOffsets.Count ? c.ChildOffsets[i] : 0), depth + 1))
                    yield return x;
        }

        /// <summary>
        /// Point symbols of a custom line style along a drawn line (LCPOINT_*): per repetition of the line point's
        /// stroke pattern at the origin/end/centre of the chosen stroke, or at the element origin/end/vertices. The
        /// pattern runs continuously along the path (no single-segment restart). Symbol geometry (the definition cell,
        /// in symbol units ÷ symbol scale = definition units) is drawn with the line's colour/weight unless the
        /// symbol keeps its own (LCPOINT_COLOR / LCPOINT_WEIGHT).
        /// </summary>
        private static void EmitLineStyleSymbols(Ctx ctx, Primitive line, ResolvedSymbology s, Element e, string? cellPath, uint level)
        {
            var pts = line.Kind == PrimitiveKind.Arc ? line.ArcPoints(16, ctx.ChordTol).ToList() : new List<DPoint3d>(line.Points);
            if (line.Closed && line.Kind == PrimitiveKind.Polyline && pts.Count > 2 && pts[0].DistanceTo(pts[pts.Count - 1]) > 0) pts.Add(pts[0]);
            if (pts.Count < 2) return;
            var cum = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
            double total = cum[cum.Length - 1];
            if (!(total > 0)) return;
            double k = s.LineStyleToMaster;
            double elemPhase = e.StyleParams != null && (e.StyleParams.Modifiers & 0x20) != 0 ? e.StyleParams.Phase * k : 0;
            foreach (var (lp, offset) in LinePoints(s.LineStyle!.Root!, 0, 0))
            {
                foreach (var si in lp.Symbols)
                {
                    var sym = si.Symbol;
                    if (sym == null || sym.SymbolElements.Count == 0 || !(sym.SymbolScale > 0)) continue;
                    var places = new List<double>();
                    switch (si.Location)
                    {
                        case LineStyleSymbolLocation.LineOrigin: places.Add(0); break;
                        case LineStyleSymbolLocation.LineEnd: places.Add(total); break;
                        case LineStyleSymbolLocation.LineVertex: places.AddRange(cum); break;
                        case LineStyleSymbolLocation.Stroke:
                        {
                            var lc = lp.LineCode;
                            if (lc == null || si.StrokeNumber >= lc.Strokes.Count) break;
                            double period = lc.PatternLength * k;
                            if (!(period > 0) || total / period > MaxSymbolsPerLine) break;
                            double before = lc.Strokes.Take(si.StrokeNumber).Sum(x => x.Length) * k;
                            double len = lc.Strokes[si.StrokeNumber].Length * k;
                            double at = si.StrokeLocation == 1 ? before : si.StrokeLocation == 2 ? before + len : before + len / 2;
                            double phase = (lc.PhaseMode == LineStylePhaseMode.Fixed ? lc.Phase * k : lc.PhaseMode == LineStylePhaseMode.Fraction ? lc.Phase * period : 0) + elemPhase;
                            phase = ((phase % period) + period) % period;
                            bool noPartial = (si.Flags & 0x0400) != 0;
                            for (double b = -phase; b < total; b += period)
                            {
                                double d = b + at;
                                if (d < -1e-9 * period || d > total + 1e-9 * period) continue;
                                if (noPartial && (b + before < -1e-9 * period || b + before + len > total + 1e-9 * period)) continue;
                                places.Add(Math.Max(0, Math.Min(total, d)));
                            }
                            break;
                        }
                    }
                    foreach (double d in places) PlaceSymbol(ctx, line, s, cellPath, level, pts, cum, d, offset * k, si, sym, k);
                }
            }
        }

        private static void PlaceSymbol(Ctx ctx, Primitive line, ResolvedSymbology s, string? cellPath, uint level, List<DPoint3d> pts, double[] cum,
            double d, double lineOffset, LineStylePointSymbol si, LineStyleComponent sym, double k)
        {
            int i = 0;
            while (i < cum.Length - 2 && cum[i + 1] < d) i++;
            // a zero-length segment has no direction: use the next one with length
            int j = i;
            while (j < cum.Length - 2 && cum[j + 1] - cum[j] <= 0) j++;
            var a = pts[j]; var b = pts[j + 1];
            double seg = cum[j + 1] - cum[j];
            double f = seg > 0 ? Math.Max(0, Math.Min(1, (d - cum[j]) / seg)) : 0;
            var p = a + (b - a) * f;
            var dir = b - a;
            double dl = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (dl <= 0) return;
            double tx = dir.X / dl, ty = dir.Y / dl;
            double ang = (si.Flags & 0x0040) != 0 ? si.Angle : Math.Atan2(ty, tx) + si.Angle;
            if ((si.Flags & 0x0020) != 0 && Math.Cos(ang) < -1e-9) ang += Math.PI; // adjusted: keep symbols reading left to right
            var origin = new DPoint3d(p.X - ty * (lineOffset + si.YOffset * k) + tx * si.XOffset * k,
                                      p.Y + tx * (lineOffset + si.YOffset * k) + ty * si.XOffset * k, p.Z);
            double scale = (sym.SymbolFlags & 0x02) != 0 && s.LineStyleScale > 0 ? k / s.LineStyleScale : k;
            double g = scale / sym.SymbolScale;
            double c = Math.Cos(ang) * g, sn = Math.Sin(ang) * g;
            var T = new Transform(new Matrix3(c, -sn, 0, sn, c, 0, 0, 0, g), origin);

            int first = ctx.Out.Count;
            ctx.InLineStyleSymbol = true;
            try
            {
                foreach (var el in sym.SymbolElements) Draw(ctx, el, T, line.Source, s, cellPath, level);
            }
            finally { ctx.InLineStyleSymbol = false; }
            for (int n = first; n < ctx.Out.Count; n++)
            {
                var q = ctx.Out[n];
                var own = q.Symbology;
                var ns = s.Clone();
                ns.StyleNumber = 0; ns.StyleName = "Continuous"; ns.LineStyle = null; ns.DashPattern = null; ns.DashPhase = 0;
                ns.LineStyleWidth = 0; ns.LineStyleToMaster = 0;
                if ((si.Flags & 0x4000) != 0) { ns.ColorIndex = own.ColorIndex; ns.Color = own.Color; }
                if ((si.Flags & 0x8000) != 0) ns.WeightIndex = own.WeightIndex;
                ns.Filled = own.Filled;
                ns.FillColor = own.Filled ? ((si.Flags & 0x4000) != 0 ? own.FillColor : ns.Color) : null;
                q.Symbology = ns;
                q.LevelId = line.LevelId; q.LevelName = line.LevelName; q.Leaf = line.Leaf;
            }
        }

        public static RgbColor ColorOf(DgnFile f, uint idx)
        {
            if (idx < 256) return f.Colors[(int)idx];
            // Extended (true/book) colours: 256 + i → TCB extended colour entry i.
            long ext = (long)idx - 256;
            if (ext >= 0 && ext < f.ExtendedColors.Count) return f.ExtendedColors[(int)ext];
            return f.Colors[(int)(idx & 0xFF)];
        }

        private static Primitive NewPrim(Ctx ctx, PrimitiveKind kind, Element source, Element leaf, ResolvedSymbology s, string? cellPath, uint levelId)
        {
            return new Primitive
            {
                Kind = kind, Source = source, Leaf = leaf, Symbology = s, CellName = cellPath, LevelId = levelId,
                LevelName = ctx.File.Levels.NameOf(levelId), ReferencePath = ctx.RefPath, ModelName = ctx.ModelName,
            };
        }

        private static void Draw(Ctx ctx, Element e, Transform T, Element source, ResolvedSymbology? parentSymb, string? cellPath, uint? inheritedLevel = null)
        {
            if (Skip(ctx, e)) return;
            uint level = e.LevelId == 0 && inheritedLevel.HasValue ? inheritedLevel.Value : e.LevelId;
            var s = Resolve(ctx, e, parentSymb, level);
            int firstOut = ctx.Out.Count;
            DrawElement(ctx, e, T, source, s, cellPath, level);
            if (s.LineStyle?.Root != null && s.LineStyleToMaster > 0 && !ctx.InLineStyleSymbol)
            {
                int end = ctx.Out.Count;
                for (int i = firstOut; i < end; i++)
                {
                    var p = ctx.Out[i];
                    if (p.Leaf == e && (p.Kind == PrimitiveKind.Polyline || p.Kind == PrimitiveKind.Arc))
                        EmitLineStyleSymbols(ctx, p, s, e, cellPath, level);
                }
            }
        }

        private static void DrawElement(Ctx ctx, Element e, Transform T, Element source, ResolvedSymbology s, string? cellPath, uint level)
        {
            switch (e)
            {
                case LineElement l:
                {
                    var p = NewPrim(ctx, PrimitiveKind.Polyline, source, e, s, cellPath, level);
                    p.Points.Add(T.Apply(l.Start)); p.Points.Add(T.Apply(l.End));
                    ctx.Out.Add(p);
                    break;
                }
                case LineStringElement ls:
                {
                    if (ls.Points.Count == 0) break;
                    if (ls.IsPointString)
                    {
                        foreach (var pt in ls.Points)
                        {
                            if (pt.IsDisconnect) continue;
                            var pp = NewPrim(ctx, PrimitiveKind.Point, source, e, s, cellPath, level);
                            pp.Points.Add(T.Apply(pt)); ctx.Out.Add(pp);
                        }
                        break;
                    }
                    // a disconnected line string draws each run on its own (and is never closed)
                    var runs = DPoint3d.SplitAtDisconnects(ls.Points);
                    foreach (var run in runs)
                    {
                        var p = NewPrim(ctx, PrimitiveKind.Polyline, source, e, s, cellPath, level);
                        var pts = ls.IsCurve ? Curves.StrokeCurveElement(run) : run;
                        p.IsStroked = ls.IsCurve;
                        foreach (var pt in pts) p.Points.Add(T.Apply(pt));
                        p.Closed = ls.IsClosed && runs.Count == 1;
                        if (p.Points.Count == 1) p.Kind = PrimitiveKind.Point;
                        ctx.Out.Add(p);
                    }
                    break;
                }
                case ArcElement a:
                {
                    var p = NewPrim(ctx, PrimitiveKind.Arc, source, e, s, cellPath, level);
                    p.Center = T.Apply(a.Origin);
                    p.AxisX = T.ApplyVector(a.Rotation.Column(0) * a.PrimaryAxis);
                    p.AxisY = T.ApplyVector(a.Rotation.Column(1) * a.SecondaryAxis);
                    p.StartAngle = a.StartAngle; p.SweepAngle = a.SweepAngle;
                    p.Closed = a.IsFullEllipse || Math.Abs(Math.Abs(a.SweepAngle) - 2 * Math.PI) < 1e-9;
                    ctx.Out.Add(p);
                    break;
                }
                case TextElement t:
                    EmitText(ctx, t, T, source, s, cellPath, level);
                    break;
                case TextNodeElement tn:
                    foreach (var c in tn.Children) Draw(ctx, c, T, source, s, cellPath, level);
                    break;
                case ComplexElement cx:
                    DrawComplex(ctx, cx, T, source, s, cellPath, level);
                    break;
                case CellElement cell:
                {
                    string path = cellPath == null ? (cell.Name ?? "cell") : cellPath + "/" + (cell.Name ?? "cell");
                    foreach (var c in cell.Children) Draw(ctx, c, T, source, s, path, level);
                    break;
                }
                case SharedCellElement sc:
                {
                    var def = sc.Definition;
                    if (def == null) break;
                    var inst = new Transform(sc.Transform, sc.Origin - sc.Transform.Multiply(def.Origin));
                    var T2 = T * inst;
                    string path = cellPath == null ? (sc.Name ?? "sharedcell") : cellPath + "/" + (sc.Name ?? "sharedcell");
                    foreach (var c in def.Children) Draw(ctx, c, T2, source, s, path, level);
                    break;
                }
                case BSplineCurveElement bc:
                {
                    var pts = Curves.StrokeBSpline(bc, 0);
                    if (pts.Count < 2) break;
                    var p = NewPrim(ctx, PrimitiveKind.Polyline, source, e, s, cellPath, level);
                    foreach (var pt in pts) p.Points.Add(T.Apply(pt));
                    p.IsStroked = true; p.Closed = bc.Closed;
                    ctx.Out.Add(p);
                    break;
                }
                case MeshElement me when me.Vertices.Count > 0:
                {
                    var p = NewPrim(ctx, PrimitiveKind.Mesh, source, e, s, cellPath, level);
                    foreach (var v in me.Vertices) p.Points.Add(T.Apply(v));
                    p.Faces = me.Faces;
                    ctx.Out.Add(p);
                    break;
                }
                case DimensionElement dim:
                    DrawDimension(ctx, dim, T, source, s, cellPath, level);
                    break;
                case TagElement tag when tag.Displayed && tag.Value != null && tag.TextHeight > 0:
                {
                    string txt = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (txt.Length == 0) break;
                    var p = NewPrim(ctx, PrimitiveKind.Text, source, e, s, cellPath, level);
                    p.Points.Add(T.Apply(tag.Origin));
                    double sc = T.Matrix.XYScale; if (sc <= 0) sc = T.Matrix.AverageScale;
                    p.TextHeight = tag.TextHeight * sc; p.TextWidth = tag.TextWidth * sc;
                    p.Text = txt; p.Justification = TextJustification.LeftBottom;
                    p.FontName = ctx.File.Fonts.NameOf(tag.FontId);
                    ctx.Out.Add(p);
                    break;
                }
                default:
                    // Surfaces/solids (18/19), extended elements (106) and others with graphic children: draw children.
                    if (e.IsGraphic && e.Children.Count > 0 && e.RawType != (int)ElementType.BSplineCurve)
                        foreach (var c in e.Children) Draw(ctx, c, T, source, s, cellPath, level);
                    break;
            }
        }

        /// <summary>Dimensions (33): MicroStation regenerates their graphics; DimensionGraphics synthesises them.</summary>
        private static void DrawDimension(Ctx ctx, DimensionElement dim, Transform T, Element source, ResolvedSymbology s, string? cellPath, uint level)
        {
            foreach (var c in dim.Children) Draw(ctx, c, T, source, s, cellPath, level);
            ResolvedSymbology? filled = null;
            foreach (var g in DimensionGraphics.Build(dim, ctx.File.Fonts.NameOf(dim.FontId)))
            {
                switch (g.Kind)
                {
                    case DimensionGraphic.GraphicKind.Line:
                    case DimensionGraphic.GraphicKind.Polygon:
                    {
                        if (g.Filled && filled == null)
                        {
                            filled = (ResolvedSymbology)CloneMethod.Invoke(s, null)!;
                            filled.Filled = true; filled.FillColor = s.Color;
                            filled.DashPattern = null; filled.StyleNumber = 0; filled.StyleName = "Continuous";
                        }
                        var p = NewPrim(ctx, PrimitiveKind.Polyline, source, dim, g.Filled ? filled! : s, cellPath, level);
                        foreach (var pt in g.Points) p.Points.Add(T.Apply(pt));
                        p.Closed = g.Kind == DimensionGraphic.GraphicKind.Polygon;
                        ctx.Out.Add(p);
                        break;
                    }
                    case DimensionGraphic.GraphicKind.Circle:
                    {
                        if (g.Filled && filled == null)
                        {
                            filled = (ResolvedSymbology)CloneMethod.Invoke(s, null)!;
                            filled.Filled = true; filled.FillColor = s.Color;
                            filled.DashPattern = null; filled.StyleNumber = 0; filled.StyleName = "Continuous";
                        }
                        var p = NewPrim(ctx, PrimitiveKind.Arc, source, dim, g.Filled ? filled! : s, cellPath, level);
                        p.Center = T.Apply(g.Center);
                        p.AxisX = T.ApplyVector(new DPoint3d(g.Radius, 0, 0));
                        p.AxisY = T.ApplyVector(new DPoint3d(0, g.Radius, 0));
                        p.StartAngle = 0; p.SweepAngle = 2 * Math.PI; p.Closed = true;
                        ctx.Out.Add(p);
                        break;
                    }
                    case DimensionGraphic.GraphicKind.Text:
                    {
                        var p = NewPrim(ctx, PrimitiveKind.Text, source, dim, s, cellPath, level);
                        p.Points.Add(T.Apply(g.Points[0]));
                        double sc = T.Matrix.XYScale; if (sc <= 0) sc = T.Matrix.AverageScale;
                        p.TextHeight = g.TextHeight * sc; p.TextWidth = g.TextWidth * sc;
                        var c0 = T.ApplyVector(g.TextDirection).Normalized(); var c1 = T.ApplyVector(g.TextUp).Normalized(); var c2 = c0.Cross(c1).Normalized();
                        p.TextRotation = new Matrix3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);
                        p.Text = g.Text; p.Justification = TextJustification.LeftBottom;
                        p.FontName = ctx.File.Fonts.NameOf(dim.FontId);
                        ctx.Out.Add(p);
                        break;
                    }
                }
            }
        }

        private static readonly System.Reflection.MethodInfo CloneMethod =
            typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        private static void EmitText(Ctx ctx, TextElement t, Transform T, Element source, ResolvedSymbology s, string? cellPath, uint level)
        {
            if (string.IsNullOrEmpty(t.Text)) return;
            var p = NewPrim(ctx, PrimitiveKind.Text, source, t, s, cellPath, level);
            p.Points.Add(T.Apply(t.Origin));
            var m = T.Matrix * t.Rotation;
            double sc = T.Matrix.XYScale;
            if (sc <= 0) sc = T.Matrix.AverageScale;
            p.TextHeight = t.Height * sc;
            p.TextWidth = t.Width * sc;
            // normalise rotation (remove scale)
            var c0 = m.Column(0).Normalized(); var c1 = m.Column(1).Normalized(); var c2 = c0.Cross(c1).Normalized();
            p.TextRotation = new Matrix3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);
            p.Text = t.Text;
            // MicroStation text elements store the lower-left origin; justification is retained on the leaf.
            p.Justification = TextJustification.LeftBottom;
            p.FontName = t.FontName ?? ctx.File.Fonts.NameOf(t.FontId);
            ctx.Out.Add(p);
        }

        private static void DrawComplex(Ctx ctx, ComplexElement cx, Transform T, Element source, ResolvedSymbology s, string? cellPath, uint level)
        {
            if (!ctx.Opt.MergeComplexChains)
            {
                foreach (var c in cx.Children) Draw(ctx, c, T, source, s, cellPath, level);
                return;
            }
            var pts = new List<DPoint3d>();
            bool stroked = false, broken = false;
            void Flush(bool closed)
            {
                if (pts.Count >= 2)
                {
                    var p = NewPrim(ctx, PrimitiveKind.Polyline, source, cx, s, cellPath, level);
                    foreach (var pt in pts) p.Points.Add(T.Apply(pt));
                    p.Closed = closed;
                    p.IsStroked = stroked;
                    ctx.Out.Add(p);
                }
                pts.Clear();
            }
            foreach (var c in cx.Children)
            {
                if (c.IsDeleted) continue;
                // a component line string with disconnect vertices breaks the chain into separate (open) polylines
                IEnumerable<IEnumerable<DPoint3d>> runs = c switch
                {
                    LineElement l => new[] { new[] { l.Start, l.End } },
                    LineStringElement ls => DPoint3d.SplitAtDisconnects(ls.Points).Select(run => ls.IsCurve ? Curves.StrokeCurveElement(run) : (IEnumerable<DPoint3d>)run),
                    ArcElement a => new[] { Curves.StrokeArc(a, ctx.ChordTol * ctx.RootModel.Units.UorPerMaster) },
                    BSplineCurveElement bc => new[] { Curves.StrokeBSpline(bc, 0) },
                    _ => Array.Empty<IEnumerable<DPoint3d>>(),
                };
                if (c is ArcElement || c is BSplineCurveElement || (c is LineStringElement l2 && l2.IsCurve)) stroked = true;
                bool first = true;
                foreach (var seg in runs)
                {
                    if (!first) { Flush(false); broken = true; }
                    first = false;
                    foreach (var pt in seg)
                    {
                        if (pts.Count > 0 && pts[pts.Count - 1].DistanceTo(pt) < 1e-9) continue;
                        pts.Add(pt);
                    }
                }
            }
            Flush(cx.IsClosed && !broken);
        }
    }
}
