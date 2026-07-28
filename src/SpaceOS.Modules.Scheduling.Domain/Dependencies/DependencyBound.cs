using System;

namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>
/// One dependency edge evaluated against an already-scheduled predecessor.
/// </summary>
/// <remarks>
/// The time scale is a normalised minute timeline, not a wall-clock date: calendar,
/// timezone and DST conversion belong to the calendar layer (ADR-069 §5), so this
/// type stays free of any date arithmetic.
/// </remarks>
public sealed record DependencyBoundInput
{
    /// <summary>The precedence relation this edge expresses.</summary>
    public required DependencyType Type { get; init; }

    /// <summary>Predecessor start on the normalised minute timeline.</summary>
    public required decimal PredecessorStartMinute { get; init; }

    /// <summary>Predecessor finish on the normalised minute timeline.</summary>
    public required decimal PredecessorFinishMinute { get; init; }

    /// <summary>Lag in minutes; may be negative (lead).</summary>
    public decimal LagMinutes { get; init; }

    /// <summary>
    /// Minute at which the predecessor's partial output is released, already
    /// calendar-resolved by the calendar layer from the release threshold.
    /// </summary>
    public decimal? PartialReleaseMinute { get; init; }

    /// <summary>Operator-fixed start; overrides every derived start bound.</summary>
    public decimal? FixedStartMinute { get; init; }

    /// <summary>Operator-fixed finish; overrides every derived finish bound.</summary>
    public decimal? FixedFinishMinute { get; init; }
}

/// <summary>
/// Resolved lower bounds for a successor operation. A null bound means this edge
/// constrains the other end only (FF/SF place no start bound, FS/SS none on finish).
/// </summary>
public sealed record ResolvedDependencyBounds
{
    /// <summary>Earliest minute the successor may start; null when unconstrained by this edge.</summary>
    public decimal? EarliestStartMinute { get; init; }

    /// <summary>Earliest minute the successor may finish; null when unconstrained by this edge.</summary>
    public decimal? EarliestFinishMinute { get; init; }

    /// <summary>Which rule produced <see cref="EarliestStartMinute"/>.</summary>
    public BoundSource? StartSource { get; init; }

    /// <summary>Which rule produced <see cref="EarliestFinishMinute"/>.</summary>
    public BoundSource? FinishSource { get; init; }

    /// <summary>
    /// Conditions the consumer must be told about; empty when the bounds are unremarkable.
    /// </summary>
    public IReadOnlyList<DependencyWarning> Warnings { get; init; } = [];
}
