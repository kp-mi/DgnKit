using System;
using System.Collections.Generic;
using DgnKit.Logic.Geometry;

namespace DgnKit.Logic.Model
{
    /// <summary>
    /// The format-neutral input record of the logic layer: one source object (a DGN element, a GIS feature, an IFC
    /// product …) with its identity, naming signals, attributes and geometry in metres. Adapters produce features;
    /// classification turns them into typed graph nodes.
    /// </summary>
    public sealed class Feature
    {
        /// <summary>Stable key, unique within the run (DGN: <c>el:&lt;id&gt;</c>, the IMX element key).</summary>
        public string Key { get; set; } = "";
        /// <summary>Source document (file name / dataset).</summary>
        public string? Source { get; set; }
        /// <summary>Container inside the source (DGN model, GIS layer).</summary>
        public string? Container { get; set; }
        /// <summary>Source object kind (DGN element type such as <c>Cell</c>, <c>LineString</c>; GeoJSON geometry type).</summary>
        public string? Kind { get; set; }
        /// <summary>Primary layer / level name.</summary>
        public string? Layer { get; set; }
        /// <summary>All layers the object's components are on (cells span several levels).</summary>
        public List<string> Layers { get; } = new();
        /// <summary>Cell / block / symbol name (outermost first, joined with '/').</summary>
        public string? Cell { get; set; }
        /// <summary>Text content (text elements, or texts inside a cell).</summary>
        public string? Text { get; set; }
        /// <summary>Class asserted by the source (e.g. a GeoJSON <c>class</c> property or a BIS class) — classified with full confidence.</summary>
        public string? Class { get; set; }
        /// <summary>Attributes (EC properties, item types, tags, GIS attributes), case-insensitive names.</summary>
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Context of the object (e.g. <c>sheetRole</c>, <c>sheetTitle</c>, <c>units</c>) — readable in rules like properties.</summary>
        public Dictionary<string, object?> Context { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Coordinate frame: features only relate spatially to features in the same frame. DGN: file|model (each model has
        /// its own coordinates; references are transformed into the root model). Null = the shared world frame (e.g. all
        /// GIS layers of one project in one CRS).
        /// </summary>
        public string? Frame { get; set; }
        /// <summary>Grouping id from the source (DGN graphic group), 0 = none.</summary>
        public long Group { get; set; }
        public FeatureGeometry Geometry { get; set; } = new();

        public override string ToString() => $"{Key} {Kind} layer={Layer} cell={Cell}";
    }

    /// <summary>A relationship asserted by the source data itself (e.g. a GIS foreign key, an EC relationship).</summary>
    public sealed class AssertedRelation
    {
        public string FromKey { get; set; } = "";
        public string ToKey { get; set; } = "";
        public string Relation { get; set; } = "";
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What an adapter hands the engine.</summary>
    public sealed class FeatureSet
    {
        public List<Feature> Features { get; } = new();
        public List<AssertedRelation> Relations { get; } = new();
        /// <summary>Free-form description of where the features came from (files, models, units).</summary>
        public Dictionary<string, object?> Info { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new();

        public void AddRange(FeatureSet other)
        {
            Features.AddRange(other.Features);
            Relations.AddRange(other.Relations);
            Warnings.AddRange(other.Warnings);
            foreach (var kv in other.Info) Info[kv.Key] = kv.Value;
        }
    }
}
