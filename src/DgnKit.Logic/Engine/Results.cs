using System;
using System.Collections.Generic;
using System.Linq;
using DgnKit.Logic.Model;
using DgnKit.Logic.Rules;

namespace DgnKit.Logic.Engine
{
    public enum FindingStatus
    {
        /// <summary>The data breaks the rule.</summary>
        Violated,
        /// <summary>Open world: the required counterpart may be outside this data (another file, a reference, the data edge).</summary>
        Unresolved,
        /// <summary>The counterpart is implied by drafting convention (e.g. a sign symbol whose post is not drawn).</summary>
        Implicit,
        /// <summary>A soft expectation does not hold for this object (not an error).</summary>
        Unusual,
        /// <summary>A soft expectation holds far less often than its prior across a container (model-level heuristic).</summary>
        Anomaly,
    }

    /// <summary>One validation result, with the rule that produced it and a human-readable message.</summary>
    public sealed class Finding
    {
        public string Rule { get; internal set; } = "";
        public string Kind { get; internal set; } = "";
        public Severity Severity { get; internal set; }
        public FindingStatus Status { get; internal set; }
        public Node? Subject { get; internal set; }
        public Node? Object { get; internal set; }
        public Edge? Edge { get; internal set; }
        public string Message { get; internal set; } = "";
        public Dictionary<string, object?> Details { get; } = new(StringComparer.Ordinal);
        /// <summary>Source document / container the finding is about (for model-level findings).</summary>
        public string? Scope { get; internal set; }
        public override string ToString() => $"[{Severity}/{Status}] {Rule}: {Message}";
    }

    /// <summary>Per-rule counters (how often a rule was evaluated, fired, accepted, violated …).</summary>
    public sealed class RuleStat
    {
        public string Rule { get; internal set; } = "";
        public string Kind { get; internal set; } = "";
        public string Pack { get; internal set; } = "";
        public int Applicable { get; internal set; }
        public int Candidates { get; internal set; }
        public int Vetoed { get; internal set; }
        public int Accepted { get; internal set; }
        public int Rejected { get; internal set; }
        public int Ambiguous { get; internal set; }
        public int Satisfied { get; internal set; }
        public int Violated { get; internal set; }
        public int Unresolved { get; internal set; }
        public int Implicit { get; internal set; }
        /// <summary>Unknown: an assertion could not be evaluated (missing attribute).</summary>
        public int Unknown { get; internal set; }
        public double? Prior { get; internal set; }
        public double? ObservedRate => Applicable > 0 && (Kind == "expect" || Kind == "require") ? (double)Satisfied / Applicable : null;
    }

    public sealed class LogicOptions
    {
        /// <summary>Report each object that misses a soft expectation (status Unusual). Model-level anomalies are always reported.</summary>
        public bool ReportExpectInstances { get; set; } = true;
        /// <summary>Upper bound on spatial candidates examined per subject and rule (nearest first by box gap).</summary>
        public int MaxCandidatesPerSubject { get; set; } = 256;
        /// <summary>Minimum objects in a container before a model-level expectation anomaly is reported.</summary>
        public int AnomalyMinCount { get; set; } = 5;
        /// <summary>z-score below which the observed rate is an anomaly.</summary>
        public double AnomalyZ { get; set; } = -3;
    }

    /// <summary>Everything a run produced.</summary>
    public sealed class LogicResult
    {
        public LogicGraph Graph { get; internal set; } = null!;
        public RuleSet Rules { get; internal set; } = null!;
        public List<Finding> Findings { get; } = new();
        public Dictionary<string, RuleStat> Stats { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new();
        public Dictionary<string, object?> Info { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> TimingsMs { get; } = new(StringComparer.Ordinal);

        public IEnumerable<Node> Classified => Graph.Nodes.Where(n => n.Class != null);

        public Dictionary<string, int> ClassCounts() =>
            Classified.GroupBy(n => n.Class!.Name).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());

        public Dictionary<string, int> RelationCounts() =>
            Graph.Edges.GroupBy(e => e.Relation.Name).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());

        public int Count(Severity s) => Findings.Count(f => f.Severity == s);
    }
}
