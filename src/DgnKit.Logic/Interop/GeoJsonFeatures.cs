using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DgnKit.Logic.Geometry;
using DgnKit.Logic.Json;
using DgnKit.Logic.Model;

namespace DgnKit.Logic.Interop
{
    /// <summary>
    /// Reads a GeoJSON FeatureCollection into logic features. Coordinates are taken as metres in a projected frame
    /// (State Plane metres, UTM, or a local engineering frame) — the logic layer measures distances, so lon/lat input
    /// should be projected first. Conventions (all optional):
    /// <list type="bullet">
    /// <item><c>id</c> (feature or property) → key; <c>class</c> → asserted class; <c>layer</c> → layer; <c>cell</c> → symbol name</item>
    /// <item>3D extents for building elements: <c>zmin</c>/<c>zmax</c> (or <c>base</c> + <c>height</c>), and <c>thickness</c> for
    /// walls drawn as centre lines (the box is grown by half the thickness)</item>
    /// <item>asserted relationships: a property <c>relations</c> = <c>[{"relation": "servesPremise", "to": "P-12"}]</c></item>
    /// <item>a top-level <c>"name"</c> becomes the container; the file name the source</item>
    /// </list>
    /// </summary>
    public static class GeoJsonFeatures
    {
        public static FeatureSet ReadFile(string path) => Read(File.ReadAllText(path), Path.GetFileName(path));

        public static FeatureSet Read(string json, string sourceName)
        {
            var set = new FeatureSet();
            if (JsonReader.Parse(json) is not JsonObject root) throw new FormatException("GeoJSON: expected an object");
            string? container = root.Str("name");
            var feats = root.Str("type") == "Feature" ? new List<object?> { root } : root.Arr("features");
            int i = 0;
            foreach (var x in feats)
            {
                i++;
                if (x is not JsonObject fo) continue;
                var props = fo.Obj("properties") ?? new JsonObject();
                var f = new Feature
                {
                    Key = fo.Str("id") ?? props.Str("id") ?? $"{sourceName}#{i}",
                    Source = sourceName,
                    Container = props.Str("container") ?? container,
                    Class = props.Str("class"),
                    Layer = props.Str("layer"),
                    Cell = props.Str("cell"),
                    Text = props.Str("text"),
                };
                if (f.Layer != null) f.Layers.Add(f.Layer);
                foreach (var k in props.OrderedKeys)
                {
                    if (k is "relations") continue;
                    var v = props[k];
                    f.Properties[k] = v is JsonObject or List<object?> ? v : v;
                }
                if (props.Num("group") is double grp) f.Group = (long)grp;
                var geom = fo.Obj("geometry");
                f.Kind = geom?.Str("type");
                if (geom != null) AddGeometry(f.Geometry, geom);
                ApplyExtrusion(f, props);
                set.Features.Add(f);

                foreach (var rx in props.Arr("relations"))
                {
                    if (rx is not JsonObject ro || ro.Str("to") is not string to || ro.Str("relation") is not string rel) continue;
                    var a = new AssertedRelation { FromKey = f.Key, ToKey = to, Relation = rel };
                    foreach (var k in ro.OrderedKeys) if (k is not ("to" or "relation")) a.Properties[k] = ro[k];
                    set.Relations.Add(a);
                }
            }
            set.Info["source"] = sourceName;
            set.Info["features"] = set.Features.Count;
            return set;
        }

        private static void ApplyExtrusion(Feature f, JsonObject props)
        {
            double? zmin = props.Num("zmin") ?? props.Num("base");
            double? zmax = props.Num("zmax") ?? (zmin is double b && props.Num("height") is double h ? b + h : null);
            double thk = props.Num("thickness") ?? 0;
            if (zmin == null && zmax == null && thk <= 0) return;
            var box = f.Geometry.Box;
            if (box.IsEmpty) return;
            double lo = zmin ?? box.Min.Z, hi = zmax ?? Math.Max(box.Max.Z, lo);
            double t = thk / 2;
            f.Geometry.ExplicitBox = new Box3(new Vec3(box.Min.X - t, box.Min.Y - t, lo), new Vec3(box.Max.X + t, box.Max.Y + t, hi));
            // lift 2D footprints/centre lines to the base elevation so plan + vertical predicates agree
            if (zmin is double z0)
            {
                var lifted = f.Geometry.Parts.Select(p => new GeometryPart(p.Kind, p.Points.Select(q => new Vec3(q.X, q.Y, q.Z == 0 ? z0 : q.Z)).ToArray())).ToList();
                f.Geometry.Parts.Clear();
                f.Geometry.Parts.AddRange(lifted);
            }
            f.Geometry.Invalidate();
        }

        private static Vec3 Pt(object? o)
        {
            if (o is not List<object?> a || a.Count < 2) throw new FormatException("GeoJSON: bad position");
            double x = Convert.ToDouble(a[0], CultureInfo.InvariantCulture), y = Convert.ToDouble(a[1], CultureInfo.InvariantCulture);
            double z = a.Count > 2 && a[2] is double dz ? dz : 0;
            return new Vec3(x, y, z);
        }

        private static List<Vec3> Line(object? o) => (o as List<object?> ?? new List<object?>()).Select(Pt).ToList();

        private static void AddGeometry(FeatureGeometry g, JsonObject geom)
        {
            var c = geom["coordinates"];
            switch (geom.Str("type"))
            {
                case "Point": g.Parts.Add(GeometryPart.Point(Pt(c))); break;
                case "MultiPoint": foreach (var p in (List<object?>)c!) g.Parts.Add(GeometryPart.Point(Pt(p))); break;
                case "LineString": g.Parts.Add(GeometryPart.Line(Line(c))); break;
                case "MultiLineString": foreach (var l in (List<object?>)c!) g.Parts.Add(GeometryPart.Line(Line(l))); break;
                case "Polygon": foreach (var ring in (List<object?>)c!) g.Parts.Add(GeometryPart.Ring(Line(ring))); break;
                case "MultiPolygon":
                    foreach (var poly in (List<object?>)c!) foreach (var ring in (List<object?>)poly!) g.Parts.Add(GeometryPart.Ring(Line(ring)));
                    break;
                case "GeometryCollection":
                    foreach (var sub in geom.Arr("geometries")) if (sub is JsonObject so) AddGeometry(g, so);
                    break;
            }
            g.Invalidate();
        }
    }
}
