using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>One operation node in a dependency network.</summary>
public sealed record OperationNode(string Id);

/// <summary>A precedence edge between two operations.</summary>
public sealed record DependencyEdge
{
    /// <summary>Id of the operation that must come first.</summary>
    public required string PredecessorId { get; init; }

    /// <summary>Id of the operation constrained by this edge.</summary>
    public required string SuccessorId { get; init; }

    /// <summary>
    /// Raw relation code as imported. Kept as text so an unknown value becomes a
    /// reported issue rather than a parse exception at the import boundary.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>Delay (positive) or overlap (negative) applied to the derived bound.</summary>
    public decimal? LagMinutes { get; init; }

    /// <summary>Legacy partial-start fraction in (0, 1].</summary>
    public decimal? ReleaseThresholdFraction { get; init; }
}

/// <summary>Why a dependency network was rejected.</summary>
public enum DependencyGraphIssueCode
{
    /// <summary>An operation id was empty or whitespace.</summary>
    BlankOperationId,

    /// <summary>The same operation id appeared more than once.</summary>
    DuplicateOperationId,

    /// <summary>An edge referenced a predecessor that is not in the node set.</summary>
    UnknownPredecessor,

    /// <summary>An edge referenced a successor that is not in the node set.</summary>
    UnknownSuccessor,

    /// <summary>An operation depended on itself.</summary>
    SelfDependency,

    /// <summary>The relation code was not one of FS, SS, FF, SF.</summary>
    InvalidDependencyType,

    /// <summary>
    /// A raw lag value could not be parsed. Raised at the import boundary: once parsed
    /// into a decimal, a non-finite lag is unrepresentable.
    /// </summary>
    InvalidLag,

    /// <summary>The partial-release fraction was outside the (0, 1] range.</summary>
    InvalidReleaseThreshold,

    /// <summary>The same predecessor/successor/relation triple appeared twice.</summary>
    DuplicateDependency,

    /// <summary>The network contains a cycle, so no topological order exists.</summary>
    CircularDependency,
}

/// <summary>A single validation issue, carrying the offending node or edge.</summary>
public sealed record DependencyGraphIssue
{
    /// <summary>What went wrong.</summary>
    public required DependencyGraphIssueCode Code { get; init; }

    /// <summary>The offending operation, for node-level issues.</summary>
    public string? OperationId { get; init; }

    /// <summary>The offending edge, for edge-level issues.</summary>
    public DependencyEdge? Edge { get; init; }
}

/// <summary>
/// Validation outcome. <see cref="TopologicalOrder"/> is null whenever any issue was
/// found — a partially valid ordering would invite scheduling on a broken network.
/// </summary>
public sealed record DependencyGraphValidation
{
    /// <summary>Everything that is wrong with the network; empty when it is valid.</summary>
    public required IReadOnlyList<DependencyGraphIssue> Issues { get; init; }

    /// <summary>Deterministic execution order, or null when any issue was found.</summary>
    public IReadOnlyList<string>? TopologicalOrder { get; init; }

    /// <summary>True when the network carries no issues.</summary>
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Validates a dependency network and produces a deterministic topological order
/// (ADR-069 §4; Doorstar-confirmed M1 scope, 2026-07-28).
/// </summary>
/// <remarks>
/// The order is deterministic — ready nodes are taken in ordinal order — so the same
/// network always yields the same schedule and a revision hash stays reproducible.
/// </remarks>
public static class DependencyGraph
{
    private static readonly IReadOnlyDictionary<string, DependencyType> RelationCodes =
        new Dictionary<string, DependencyType>(StringComparer.Ordinal)
        {
            ["FS"] = DependencyType.FinishToStart,
            ["SS"] = DependencyType.StartToStart,
            ["FF"] = DependencyType.FinishToFinish,
            ["SF"] = DependencyType.StartToFinish,
        };

    /// <summary>Maps a wire relation code ("FS", "SS", "FF", "SF") to its enum value.</summary>
    public static bool TryParseRelationCode(string code, out DependencyType type) =>
        RelationCodes.TryGetValue(code ?? string.Empty, out type);

    /// <summary>Validates nodes and edges, then orders the network.</summary>
    public static DependencyGraphValidation Validate(
        IReadOnlyList<OperationNode> nodes,
        IReadOnlyList<DependencyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var issues = new List<DependencyGraphIssue>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.BlankOperationId, OperationId = node.Id });
            }
            else if (!nodeIds.Add(node.Id))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.DuplicateOperationId, OperationId = node.Id });
            }
        }

        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!nodeIds.Contains(edge.PredecessorId))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.UnknownPredecessor, Edge = edge });
            }
            if (!nodeIds.Contains(edge.SuccessorId))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.UnknownSuccessor, Edge = edge });
            }
            if (string.Equals(edge.PredecessorId, edge.SuccessorId, StringComparison.Ordinal))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.SelfDependency, Edge = edge });
            }
            if (!TryParseRelationCode(edge.Type, out _))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.InvalidDependencyType, Edge = edge });
            }
            // No lag check here: decimal cannot hold NaN/Infinity, so a non-finite lag is
            // unrepresentable once parsed. InvalidLag stays in the issue vocabulary because
            // the import boundary raises it when a raw wire value fails to parse at all.
            if (edge.ReleaseThresholdFraction.HasValue &&
                (edge.ReleaseThresholdFraction.Value <= 0m || edge.ReleaseThresholdFraction.Value > 1m))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.InvalidReleaseThreshold, Edge = edge });
            }

            // NUL separator: an operation id may legitimately contain spaces or dashes, so a
            // printable separator would let two different edges collide into one key.
            var key = string.Join('\0', edge.PredecessorId, edge.SuccessorId, edge.Type);
            if (!edgeKeys.Add(key))
            {
                issues.Add(new DependencyGraphIssue { Code = DependencyGraphIssueCode.DuplicateDependency, Edge = edge });
            }
        }

        if (issues.Count > 0)
        {
            return new DependencyGraphValidation { Issues = issues, TopologicalOrder = null };
        }

        var order = TrySort(nodeIds, edges);
        if (order is null)
        {
            return new DependencyGraphValidation
            {
                Issues = [new DependencyGraphIssue { Code = DependencyGraphIssueCode.CircularDependency }],
                TopologicalOrder = null,
            };
        }

        return new DependencyGraphValidation { Issues = [], TopologicalOrder = order };
    }

    /// <summary>Kahn's algorithm with an ordinal-sorted ready set for determinism.</summary>
    private static List<string>? TrySort(HashSet<string> nodeIds, IReadOnlyList<DependencyEdge> edges)
    {
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var incoming = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (!successors.TryGetValue(edge.PredecessorId, out var list))
            {
                list = [];
                successors[edge.PredecessorId] = list;
            }
            list.Add(edge.SuccessorId);
            incoming[edge.SuccessorId] += 1;
        }

        var ready = new List<string>(nodeIds.Where(id => incoming[id] == 0).OrderBy(id => id, StringComparer.Ordinal));
        var order = new List<string>(nodeIds.Count);

        while (ready.Count > 0)
        {
            var current = ready[0];
            ready.RemoveAt(0);
            order.Add(current);

            if (!successors.TryGetValue(current, out var nextNodes)) { continue; }
            foreach (var successor in nextNodes)
            {
                if (--incoming[successor] != 0) { continue; }
                ready.Add(successor);
                ready.Sort(StringComparer.Ordinal);
            }
        }

        return order.Count == nodeIds.Count ? order : null;
    }

    private static bool IsFinite(decimal value) => true; // decimal cannot hold NaN/Infinity
}
