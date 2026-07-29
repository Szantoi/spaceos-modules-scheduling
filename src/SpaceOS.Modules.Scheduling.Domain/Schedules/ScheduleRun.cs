using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Aggregate root: one scheduling run for a project, plus its immutable revision chain
/// (ADR-069 §4).
/// </summary>
/// <remarks>
/// <para>
/// The invariant this root exists to protect: <b>at most one Published revision</b>.
/// Publishing a new revision supersedes the previous one in the same operation, so there
/// is no instant — not even inside a transaction — when the shop floor could read two
/// active plans.
/// </para>
/// <para>
/// Tenant scoping is carried explicitly: the field mirrors the RLS predicate, so a domain
/// object can never be silently attached to the wrong tenant when persistence lands (M2).
/// </para>
/// </remarks>
public sealed class ScheduleRun
{
    private readonly List<ScheduleRevision> _revisions = [];

    private ScheduleRun(Guid id, Guid tenantId, ProjectRef project, DateTimeOffset createdAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        Project = project;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Run identity.</summary>
    public Guid Id { get; }

    /// <summary>Owning tenant; mirrors the RLS predicate.</summary>
    public Guid TenantId { get; }

    /// <summary>Opaque reference to the Kernel project this run plans.</summary>
    public ProjectRef Project { get; }

    /// <summary>When the run was opened.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>The revision chain, oldest first.</summary>
    public IReadOnlyList<ScheduleRevision> Revisions => _revisions;

    /// <summary>The active plan, or null when nothing has been published yet.</summary>
    public ScheduleRevision? PublishedRevision =>
        _revisions.SingleOrDefault(revision => revision.State == ScheduleRevisionState.Published);

    /// <summary>Opens a new run.</summary>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    public static ScheduleRun Open(Guid id, Guid tenantId, ProjectRef project, DateTimeOffset createdAtUtc)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A schedule run must belong to a tenant.", nameof(tenantId));
        }

        return new ScheduleRun(id, tenantId, project, createdAtUtc);
    }

    /// <summary>Adds a freshly calculated revision in <see cref="ScheduleRevisionState.Proposal"/>.</summary>
    /// <exception cref="ArgumentException">
    /// An operation belongs to a different project than this run.
    /// </exception>
    public ScheduleRevision AddProposal(
        Guid revisionId,
        IReadOnlyList<OperationPlan> operations,
        IReadOnlyDictionary<string, int> calendarRevisions,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<PlannedDependency>? dependencies = null,
        DateTimeOffset? timelineOriginUtc = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        // A run plans ONE project. An operation scoped to another project would silently make
        // the plan span two projects, and the published revision would misrepresent what was
        // scheduled — the kind of error that only surfaces on the shop floor.
        var foreign = operations.FirstOrDefault(operation => operation.Scope.Project != Project);
        if (foreign is not null)
        {
            throw new ArgumentException(
                $"Operation '{foreign.OperationId}' is scoped to project {foreign.Scope.Project}, " +
                $"but this run plans project {Project}.", nameof(operations));
        }

        // Dependencies are optional (a set of independent operations is a valid plan);
        // the calendar pins are not, because without them the revision is not reproducible.
        var revision = ScheduleRevision.Create(
            revisionId, _revisions.Count + 1, operations, dependencies ?? [], calendarRevisions,
            createdAtUtc, timelineOriginUtc);
        _revisions.Add(revision);
        return revision;
    }

    /// <summary>Moves a proposal into shadow evaluation; the published plan is untouched.</summary>
    public void MoveToShadow(Guid revisionId) =>
        Require(revisionId).TransitionTo(ScheduleRevisionState.Shadow);

    /// <summary>Abandons a revision that was never published.</summary>
    public void Discard(Guid revisionId) =>
        Require(revisionId).TransitionTo(ScheduleRevisionState.Discarded);

    /// <summary>
    /// Publishes a revision, superseding the current one in the same step.
    /// </summary>
    /// <returns>The revision that was superseded, or null when this is the first publication.</returns>
    /// <exception cref="InvalidOperationException">
    /// The revision's lifecycle does not allow publication.
    /// </exception>
    public ScheduleRevision? Publish(Guid revisionId)
    {
        var revision = Require(revisionId);

        // Check before mutating anything: a failed publication must leave the previously
        // published revision active, not superseded-with-nothing-in-its-place.
        if (!revision.CanTransitionTo(ScheduleRevisionState.Published))
        {
            throw new InvalidOperationException(
                $"Revision {revision.Sequence} cannot be published from state {revision.State}.");
        }

        var previous = PublishedRevision;
        previous?.TransitionTo(ScheduleRevisionState.Superseded);
        revision.TransitionTo(ScheduleRevisionState.Published);
        return previous;
    }

    private ScheduleRevision Require(Guid revisionId) =>
        _revisions.SingleOrDefault(revision => revision.Id == revisionId)
        ?? throw new InvalidOperationException($"Revision {revisionId} does not belong to run {Id}.");
}
