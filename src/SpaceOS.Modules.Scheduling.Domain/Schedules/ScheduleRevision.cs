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
    // A LIST, never the array the snapshot arrives as: EF materialises a navigation by ADDING
    // to whatever instance the entity already holds, and an array is fixed-size -- reading a
    // revision back then throws "Collection was of a fixed size". The list is private and only
    // ever populated at construction, so the revision stays as immutable as it claims to be.
    private readonly List<OperationPlan> _operations = [];

    private ScheduleRevision(
        Guid id,
        int sequence,
        IReadOnlyList<OperationPlan> operations,
        IReadOnlyList<PlannedDependency> dependencies,
        IReadOnlyDictionary<string, int> calendarRevisions,
        string contentHash,
        ScheduleRevisionState state,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Sequence = sequence;
        _operations.AddRange(operations);
        Dependencies = dependencies;
        CalendarRevisions = calendarRevisions;
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
        Dependencies = [];
        CalendarRevisions = new Dictionary<string, int>(StringComparer.Ordinal);
        ContentHash = string.Empty;
    }

    /// <summary>Revision identity.</summary>
    public Guid Id { get; }

    /// <summary>Position in the run's revision chain, starting at 1.</summary>
    public int Sequence { get; }

    /// <summary>The scheduled operations. Immutable.</summary>
    public IReadOnlyList<OperationPlan> Operations => _operations;

    /// <summary>The precedence network as it was resolved into this revision. Immutable.</summary>
    public IReadOnlyList<PlannedDependency> Dependencies { get; }

    /// <summary>
    /// Calendar revision per resource the plan was computed against — the plan is only
    /// reproducible with it.
    /// </summary>
    public IReadOnlyDictionary<string, int> CalendarRevisions { get; }

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
        IReadOnlyList<PlannedDependency> dependencies,
        IReadOnlyDictionary<string, int> calendarRevisions,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(calendarRevisions);
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

        // Isolated(): each operation gets its OWN scope instance -- see KernelWorkScope.
        var snapshot = operations.Select(operation => operation with { Scope = operation.Scope.Isolated() }).ToArray();
        var edges = dependencies.ToArray();
        var known = snapshot.Select(operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            edge.Validate();

            // A dangling edge is a corrupt plan, not a warning: the consumer would be shown a
            // constraint against an operation that is not in the proposal, and could neither
            // verify nor act on it.
            var missing = !known.Contains(edge.PredecessorOperationId) ? edge.PredecessorOperationId
                : !known.Contains(edge.SuccessorOperationId) ? edge.SuccessorOperationId
                : null;
            if (missing is not null)
            {
                throw new ArgumentException(
                    $"Dependency {edge.PredecessorOperationId}->{edge.SuccessorOperationId} references " +
                    $"operation '{missing}', which is not in the revision.", nameof(dependencies));
            }
        }

        var duplicateEdge = edges
            .GroupBy(edge => (edge.PredecessorOperationId, edge.SuccessorOperationId, edge.Relation))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEdge is not null)
        {
            throw new ArgumentException(
                $"Dependency {duplicateEdge.Key.PredecessorOperationId}->" +
                $"{duplicateEdge.Key.SuccessorOperationId} ({duplicateEdge.Key.Relation}) appears more than once.",
                nameof(dependencies));
        }

        // Every scheduled resource must have its calendar revision pinned. Without it the plan
        // cannot be reproduced later, and "reproducible" is what a published revision promises.
        var unpinned = snapshot
            .Select(operation => operation.ResourceKey)
            .FirstOrDefault(resourceKey => !calendarRevisions.ContainsKey(resourceKey));
        if (unpinned is not null)
        {
            throw new ArgumentException(
                $"Resource '{unpinned}' is scheduled but its calendar revision is not pinned.",
                nameof(calendarRevisions));
        }

        var pins = new Dictionary<string, int>(calendarRevisions, StringComparer.Ordinal);
        return new ScheduleRevision(
            id, sequence, snapshot, edges, pins,
            RevisionHasher.ComputeHash(snapshot, edges, pins),
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
