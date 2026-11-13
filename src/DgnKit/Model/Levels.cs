using System;
using System.Collections.Generic;
using System.Linq;

namespace DgnKit.Model
{
    /// <summary>A MicroStation level (≈ DWG layer, ≈ iModel SubCategory).</summary>
    public sealed class Level
    {
        public uint Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public string? Description { get; internal set; }
        /// <summary>User-visible level number/code (V7 level number or V8 "level code").</summary>
        public uint? Code { get; internal set; }
        public uint ParentId { get; internal set; }
        public uint Color { get; internal set; }
        public uint Weight { get; internal set; }
        public int Style { get; internal set; }
        /// <summary>Level override symbology (used when the view's "level symbology" flag is on).</summary>
        public uint OverrideColor { get; internal set; }
        public uint OverrideWeight { get; internal set; }
        public int OverrideStyle { get; internal set; }
        public int Priority { get; internal set; }
        public double Transparency { get; internal set; }
        public bool IsDisplayed { get; internal set; } = true;
        public bool IsFrozen { get; internal set; }
        public bool IsPlot { get; internal set; } = true;
        public bool IsLocked { get; internal set; }
        public ulong ElementId { get; internal set; }
        /// <summary>True if colour/weight/style came from a decoded level record (not defaults).</summary>
        public bool HasSymbology { get; internal set; }
        /// <summary>Raw/diagnostic values from the level record.</summary>
        public Dictionary<string, object?> Properties { get; } = new();
        public override string ToString() => $"{Id}: {Name}";
    }

    public sealed class LevelTable
    {
        private readonly Dictionary<uint, Level> _byId = new();
        public IReadOnlyCollection<Level> All => _byId.Values;
        public int Count => _byId.Count;

        public Level? this[uint id] => _byId.TryGetValue(id, out var l) ? l : null;

        internal void Add(Level l) => _byId[l.Id] = l;

        public Level? FindByName(string name) => _byId.Values.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Level display name; falls back to "Default" for id 64 (V8 default level) and to the numeric id otherwise.</summary>
        public string NameOf(uint id)
        {
            if (_byId.TryGetValue(id, out var l) && !string.IsNullOrEmpty(l.Name)) return l.Name;
            if (id == 64) return "Default";
            return "Level " + id;
        }
    }
}
