using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// One immutable calculation result inside a <see cref="ScheduleRun"/>.
/// </summary>
/// <remarks>
/// The operation set never changes after construction: a revision is a snapshot, and its
/// <see cref="ContentHash"/> is only meaningful if the content it names is frozen. A new
/// calculation produces a new revision, never an edit of an old one.
/// </remarks>
public sealed class ScheduleRevision
{
    private ScheduleRevision(
        Guid id,
        int sequence,
        IReadOnlyList<OperationPlan> operations,
        string contentHash,
        ScheduleRevisionState state,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Sequence = sequence;
        Operations = operations;
        ContentHash = contentHash;
        State = state;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Materialisation constructor for the persistence layer only.
    /// </summary>
    /// <remarks>
    /// EF cannot bind the real constructor, whose operations parameter is a navigation
    /// rather than a scalar property. Keeping this one private means only EF (which uses
    /// reflection) can reach it — application code still has to go through
    /// <see cref="Create"/> and its invariants.
    /// </remarks>
    private ScheduleRevision()
    {
        Operations = [];
        ContentHash = string.Empty;
    }

    /// <summary>Revision identity.</summary>
    public Guid Id { get; }

    /// <summary>Position in the run's revision chain, starting at 1.</summary>
    public int Sequence { get; }

    /// <summary>The scheduled operations. Immutable.</summary>
    public IReadOnlyList<OperationPlan> Operations { get; }

    /// <summary>Deterministic content hash — the revision's identity on the wire.</summary>
    public string ContentHash { get; }

    /// <summary>Where the revision sits in its lifecycle.</summary>
    public ScheduleRevisionState State { get; private set; }

    /// <summary>When the revision was calculated.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>True once the revision can no longer change state.</summary>
    public bool IsTerminal => State is ScheduleRevisionState.Discarded or ScheduleRevisionState.Superseded;

    internal static ScheduleRevision Create(
        Guid id,
        int sequence,
        IReadOnlyList<OperationPlan> operations,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Revision sequence starts at 1.");
        }

        foreach (var operation in operations)
        {
            operation.Validate();
        }

        var duplicate = operations
            .GroupBy(operation => operation.OperationId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Operation '{duplicate.Key}' appears more than once in the revision.", nameof(operations));
        }

        var snapshot = operations.ToArray();
        return new ScheduleRevision(
            id, sequence, snapshot, RevisionHasher.ComputeHash(snapshot),
            ScheduleRevisionState.Proposal, createdAtUtc);
    }

    internal void TransitionTo(ScheduleRevisionState target)
    {
        if (!CanTransitionTo(target))
        {
            throw new InvalidOperationException(
                $"Revision {Sequence} cannot move from {State} to {target}.");
        }

        State = target;
    }

    /// <summary>Whether the lifecycle permits a move to <paramref name="target"/>.</summary>
    public bool CanTransitionTo(ScheduleRevisionState target) => State switch
    {
        ScheduleRevisionState.Proposal =>
            target is ScheduleRevisionState.Shadow
                or ScheduleRevisionState.Published
                or ScheduleRevisionState.Discarded,

        // A shadow revision may be promoted straight to Published: that is the whole point
        // of shadowing -- evaluate next to the live plan, then adopt it.
        ScheduleRevisionState.Shadow =>
            target is ScheduleRevisionState.Published or ScheduleRevisionState.Discarded,

        ScheduleRevisionState.Published => target is ScheduleRevisionState.Superseded,

        _ => false,
    };
}
