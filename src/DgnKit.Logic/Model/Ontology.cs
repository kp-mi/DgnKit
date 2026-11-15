using System;
using System.Collections.Generic;
using System.Linq;

namespace DgnKit.Logic.Model
{
    /// <summary>An engineering object class (e.g. <c>Sewer.Lateral</c>), arranged in a single-inheritance taxonomy.</summary>
    public sealed class ClassDef
    {
        public string Name { get; }
        public ClassDef? Parent { get; internal set; }
        public string? ParentName { get; internal set; }
        public bool Abstract { get; internal set; }
        public string? Description { get; internal set; }
        /// <summary>Closest BIS class (e.g. <c>BuildingPhysical:Wall</c>), used when writing iModels.</summary>
        public string? Bis { get; internal set; }
        /// <summary>Closest IFC entity (e.g. <c>IfcWall</c>).</summary>
        public string? Ifc { get; internal set; }
        /// <summary>Linear (network link), point-like (network node / symbol) or areal — informs default predicates.</summary>
        public string? Shape { get; internal set; }

        /// <summary>
        /// Additional supertypes ("traits"): a <c>Gas.Main</c> is a <c>Gas</c> object by domain and also a
        /// <c>Util.Pipe</c> by role, so cross-domain rules ("no pipe may connect to a pipe of another system") can name roles.
        /// </summary>
        public List<ClassDef> Traits { get; } = new();
        internal List<string> TraitNames { get; } = new();

        public ClassDef(string name) { Name = name; }

        public string Domain => Name.Contains('.') ? Name.Substring(0, Name.IndexOf('.')) : Name;

        public bool IsA(ClassDef other)
        {
            foreach (var c in SelfAndAncestors()) if (ReferenceEquals(c, other)) return true;
            return false;
        }

        /// <summary>The class, its parents and all traits (and their ancestors), each once.</summary>
        public IEnumerable<ClassDef> SelfAndAncestors()
        {
            var seen = new HashSet<ClassDef>();
            var stack = new Stack<ClassDef>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c)) continue;
                yield return c;
                for (int i = c.Traits.Count - 1; i >= 0; i--) stack.Push(c.Traits[i]);
                if (c.Parent != null) stack.Push(c.Parent);
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>A relation type (edge label) such as <c>hostedBy</c>, <c>connectsTo</c>, <c>flowsInto</c>.</summary>
    public sealed class RelationDef
    {
        public string Name { get; }
        /// <summary>Name of the inverse reading (<c>hostedBy</c> ↔ <c>hosts</c>), for reports and queries.</summary>
        public string? Inverse { get; internal set; }
        /// <summary>a R b ⇔ b R a (connectivity). Stored once; queries traverse both ways.</summary>
        public bool Symmetric { get; internal set; }
        /// <summary>Directed and transitive in meaning (flowsInto, feeds); traces follow chains.</summary>
        public bool Transitive { get; internal set; }
        public string? Description { get; internal set; }
        /// <summary>
        /// Default-deny (Esri Utility Network policy): an edge of this relation is only legitimate between class pairs
        /// that some granting relate rule or <c>allow</c> entry names; any other pair is reported.
        /// </summary>
        public bool DefaultDeny { get; internal set; }
        /// <summary>
        /// False for purely spatial facts (near, parallel, crosses) that are recomputed from geometry and only serve as
        /// evidence: they stay in reports and graph exports but are not written into iModels.
        /// </summary>
        public bool Persist { get; internal set; } = true;
        /// <summary>BIS relationship class to write (default: a dynamic <c>bis:ElementRefersToElements</c> subclass).</summary>
        public string? Bis { get; internal set; }

        public RelationDef(string name) { Name = name; }
        public override string ToString() => Name;
    }

    /// <summary>The class taxonomy and relation vocabulary of a loaded rule set.</summary>
    public sealed class Ontology
    {
        private readonly Dictionary<string, ClassDef> _classes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RelationDef> _relations = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The implicit abstract root class every class descends from.</summary>
        public const string RootName = "Object";

        public IEnumerable<ClassDef> Classes => _classes.Values;
        public IEnumerable<RelationDef> Relations => _relations.Values;

        public ClassDef? FindClass(string? name) => name != null && _classes.TryGetValue(name, out var c) ? c : null;
        public RelationDef? FindRelation(string? name) => name != null && _relations.TryGetValue(name, out var r) ? r : null;

        public ClassDef Class(string name)
        {
            if (!_classes.TryGetValue(name, out var c)) { c = new ClassDef(name); _classes[name] = c; }
            return c;
        }

        public RelationDef Relation(string name)
        {
            if (!_relations.TryGetValue(name, out var r)) { r = new RelationDef(name); _relations[name] = r; }
            return r;
        }

        /// <summary>Resolves parent names (implicitly creating abstract parents named by prefix, e.g. <c>Sewer</c>).</summary>
        internal void Link(List<string> errors)
        {
            var work = new Queue<ClassDef>(_classes.Values);
            while (work.Count > 0)
            {
                var c = work.Dequeue();
                if (c.Parent != null) continue;
                // explicit parent, else the dotted prefix (Sewer.Pipe.Main → Sewer.Pipe → Sewer)
                string? parent = c.ParentName ?? (c.Name.Contains('.') ? c.Name.Substring(0, c.Name.LastIndexOf('.')) : null);
                if (parent == null) continue;
                if (!_classes.TryGetValue(parent, out var p))
                {
                    p = new ClassDef(parent) { Abstract = true };
                    _classes[parent] = p;
                    work.Enqueue(p);
                }
                c.Parent = p;
            }
            // every taxonomy hangs below one abstract root, so generic rules can name "any object"
            var root = Class(RootName);
            root.Abstract = true;
            root.Description ??= "Root of the taxonomy: any engineering object.";
            foreach (var c in _classes.Values.ToList())
                if (c.Parent == null && !ReferenceEquals(c, root)) c.Parent = root;
            foreach (var c in _classes.Values.ToList())
            {
                c.Traits.Clear();
                foreach (var t in c.TraitNames)
                {
                    if (_classes.TryGetValue(t, out var tc)) c.Traits.Add(tc);
                    else errors.Add($"class {c.Name}: unknown trait '{t}'");
                }
            }
            foreach (var c in _classes.Values)
            {
                var seen = new HashSet<ClassDef>();
                for (var x = c; x != null; x = x.Parent)
                    if (!seen.Add(x)) { errors.Add($"class {c.Name}: inheritance cycle"); c.Parent = null; break; }
                foreach (var t in c.Traits)
                    if (t.SelfAndAncestors().Contains(c)) errors.Add($"class {c.Name}: trait {t.Name} cycles back");
            }
        }
    }
}
