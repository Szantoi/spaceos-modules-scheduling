using System;
using System.Collections.Generic;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// One precedence edge as it was RESOLVED into the revision (ADR-069 §6, proposal payload).
/// </summary>
/// <remarks>
/// <para>
/// The edge is stored with the revision rather than recomputed when the proposal is read.
/// Recomputation would answer with today's inputs — a calendar approved since, a standard
/// re-imported — and could disagree with the plan the consumer is looking at. A published
/// revision must say the same thing forever; that is the whole point of hashing it.
/// </para>
/// <para>
/// <see cref="EarliestStartMinute"/> and <see cref="StartSource"/> carry the attribution
/// Doorstar asked for: not just WHEN the successor may start, but WHICH rule decided it —
/// a fixed override, a partial release, or the precedence relation itself. Without the
/// attribution a planner cannot tell a schedule they may renegotiate from one they may not.
/// </para>
/// </remarks>
public sealed record PlannedDependency
{
    /// <summary>Predecessor operation, as identified inside the revision.</summary>
    public required string PredecessorOperationId { get; init; }

    /// <summary>Successor operation, as identified inside the revision.</summary>
    public required string SuccessorOperationId { get; init; }

    /// <summary>The precedence relation in force.</summary>
    public required DependencyType Relation { get; init; }

    /// <summary>Lag (positive) or lead (negative) applied to the relation, in minutes.</summary>
    public decimal LagMinutes { get; init; }

    /// <summary>Resolved earliest start, when any rule bound it.</summary>
    public decimal? EarliestStartMinute { get; init; }

    /// <summary>Which rule produced <see cref="EarliestStartMinute"/>; null when nothing bound it.</summary>
    public BoundSource? StartSource { get; init; }

    /// <summary>Conditions the consumer must see, e.g. a partial release delaying the start.</summary>
    public IReadOnlyList<DependencyWarning> Warnings { get; init; } = [];

    /// <summary>Validates the edge in isolation.</summary>
    /// <exception cref="ArgumentException">The edge is self-referential or unidentified.</exception>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(PredecessorOperationId) || string.IsNullOrWhiteSpace(SuccessorOperationId))
        {
            throw new ArgumentException("A dependency edge must identify both operations.");
        }

        // A self-edge is not a cycle to be reported later — it is nonsense at the point of
        // construction, and the graph validator would only ever confirm that.
        if (string.Equals(PredecessorOperationId, SuccessorOperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Operation '{PredecessorOperationId}' cannot depend on itself.");
        }

        // A source without a bound (or the reverse) is an incoherent answer: the consumer
        // would be told which rule decided a time that does not exist.
        if (EarliestStartMinute.HasValue != StartSource.HasValue)
        {
            throw new ArgumentException(
                $"Edge {PredecessorOperationId}->{SuccessorOperationId} must carry a start bound " +
                "and its source together, or neither.");
        }
    }
}
