using System.Globalization;
using System.Text;
using DgnKit.IModel.Dgn;
using DgnKit.IModel.Imx;
using DgnKit.Logic.Engine;
using DgnKit.Logic.Model;

namespace DgnKit.IModel.Logic;

/// <summary>
/// Maps an engineering-logic result onto IMX enrichment, so it lands in the iModel next to the converted elements:
/// <list type="bullet">
/// <item>each classified element gets a <c>Logic / Classification</c> property group (class, domain, confidence, rule,
/// BIS / IFC hint) → a <c>DgnKitDynamic:Logic_Classification</c> aspect;</item>
/// <item>each persisted relation becomes a <c>DgnKitLogic:&lt;Relation&gt;</c> link-table relationship
/// (<c>bis:ElementRefersToElements</c> subclass) with <c>Confidence</c>, <c>Rule</c>, <c>Status</c>, <c>Evidence</c>, <c>Ambiguous</c>.</item>
/// </list>
/// Only edges at or above <paramref name="minConfidence"/> and relations marked persistent are written (spatial facts
/// such as <c>crosses</c> / <c>nearParallel</c> stay in reports).
/// </summary>
public static class LogicImx
{
    public static ImxEnrichment ToEnrichment(LogicResult result, double minConfidence = 0.5)
    {
        var en = new ImxEnrichment();
        foreach (var n in result.Graph.Nodes)
        {
            if (n.Class == null) continue;
            var values = new Dictionary<string, object?>
            {
                ["Class"] = n.Class.Name,
                ["Domain"] = n.Class.Domain,
                ["Confidence"] = Math.Round(n.ClassConfidence, 4),
                ["Rule"] = n.ClassRule,
            };
            if (n.Class.Bis != null) values["BisClass"] = n.Class.Bis;
            if (n.Class.Ifc != null) values["IfcClass"] = n.Class.Ifc;
            var groups = new List<ImxPropertyGroup> { new() { Source = "Logic", Name = "Classification", ClassName = "Classification", Properties = values } };
            var derived = n.Derived.Where(kv => kv.Value is string or double or bool or int or long)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (derived.Count > 0) groups.Add(new ImxPropertyGroup { Source = "Logic", Name = "Derived", ClassName = "Derived", Properties = derived });
            en.ElementProperties[n.Key] = groups;
        }

        var classes = new Dictionary<string, ImxRelationshipClass>(StringComparer.Ordinal);
        foreach (var e in result.Graph.Edges)
        {
            if (!e.Relation.Persist || e.Confidence < minConfidence) continue;
            string cls = ClassName(e.Relation.Name);
            if (!classes.ContainsKey(cls))
                classes[cls] = new ImxRelationshipClass
                {
                    Name = cls,
                    Description = e.Relation.Description,
                    SourceRole = e.Relation.Name,
                    TargetRole = e.Relation.Inverse ?? e.Relation.Name,
                    Properties = new Dictionary<string, string>
                    {
                        ["Confidence"] = "double", ["Rule"] = "string", ["Status"] = "string", ["Evidence"] = "string", ["Ambiguous"] = "boolean",
                    },
                };
            en.Relationships.Add(new ImxRelationship
            {
                Key = $"rel:{e.Relation.Name}:{e.From.Key}>{e.To.Key}",
                Class = cls,
                SourceKey = e.From.Key,
                TargetKey = e.To.Key,
                Properties = new Dictionary<string, object?>
                {
                    ["Confidence"] = Math.Round(e.Confidence, 4),
                    ["Rule"] = e.Rule,
                    ["Status"] = e.Status.ToString().ToLowerInvariant(),
                    ["Evidence"] = EvidenceJson(e),
                    ["Ambiguous"] = e.Ambiguous,
                },
            });
        }
        en.RelationshipClasses.AddRange(classes.Values.OrderBy(c => c.Name, StringComparer.Ordinal));
        return en;
    }

    /// <summary><c>hostedBy</c> → <c>HostedBy</c> (EC-safe).</summary>
    public static string ClassName(string relation)
    {
        var sb = new StringBuilder();
        bool up = true;
        foreach (char c in relation)
        {
            if (!char.IsLetterOrDigit(c)) { up = true; continue; }
            sb.Append(up ? char.ToUpperInvariant(c) : c);
            up = false;
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string EvidenceJson(Edge e)
    {
        var sw = new StringWriter(CultureInfo.InvariantCulture);
        var w = new DgnKit.Logic.Json.JsonWriter(sw, indent: false);
        w.Value(e.Evidence.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
        return sw.ToString();
    }
}
