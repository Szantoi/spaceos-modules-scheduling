using System;
using System.Collections.Generic;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;

namespace SpaceOS.Modules.Scheduling.Domain.Solving;

/// <summary>One operation to place, on the normalised minute timeline.</summary>
/// <remarks>
/// Duration arrives already computed (see <c>EffortCalculator</c>): the solver decides WHEN
/// work happens, never how long it takes. Keeping the two apart means a norm change and a
/// scheduling change are separately explainable — which is what a planner asks about first.
/// </remarks>
public sealed record SolverOperation
{
    /// <summary>Stable identifier, unique inside the request.</summary>
    public required string OperationId { get; init; }

    /// <summary>The Kernel work this operation serves.</summary>
    public required KernelWorkScope Scope { get; init; }

    /// <summary>Resource the operation must run on.</summary>
    public required string ResourceKey { get; init; }

    /// <summary>Elapsed duration in minutes; zero is a milestone.</summary>
    public required decimal DurationMinutes { get; init; }

    /// <summary>Revision of the norm the duration came from.</summary>
    public int? StandardRevision { get; init; }

    /// <summary>Opaque upstream lineage, carried through to the plan untouched.</summary>
    public IReadOnlyDictionary<string, string> SourceRevisions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>An externally fixed start that overrides every derived bound.</summary>
    public decimal? FixedStartMinute { get; init; }

    /// <summary>
    /// False when the operation may not be placed automatically — an incomplete standard,
    /// typically. It still occupies its resource, because the work happens either way.
    /// </summary>
    public bool EligibleForAutomaticPlanning { get; init; } = true;
}

/// <summary>One precedence constraint between two operations of the request.</summary>
public sealed record SolverDependency
{
    /// <summary>Predecessor operation id.</summary>
    public required string PredecessorOperationId { get; init; }

    /// <summary>Successor operation id.</summary>
    public required string SuccessorOperationId { get; init; }

    /// <summary>The precedence relation.</summary>
    public required DependencyType Relation { get; init; }

    /// <summary>Lag (positive) or lead (negative), in minutes.</summary>
    public decimal LagMinutes { get; init; }

    /// <summary>
    /// Whether <see cref="LagMinutes"/> counts working time or real elapsed time.
    /// </summary>
    /// <remarks>
    /// Defaults to working time, which is what every dependency meant before this existed —
    /// so an existing request keeps its behaviour exactly.
    /// </remarks>
    public LagKind LagKind { get; init; } = LagKind.WorkingTime;

    /// <summary>
    /// Fraction of the predecessor's duration after which the successor may start.
    /// </summary>
    /// <remarks>
    /// The FINAL rule (doorstar-contract-v1): a partial release OVERRIDES the precedence
    /// bound, even when it lands later — and when it does, the plan says so instead of
    /// quietly moving the date.
    /// </remarks>
    public decimal? ReleaseThresholdFraction { get; init; }
}

/// <summary>A resource and the capacity the solver may consume.</summary>
/// <param name="ResourceKey">Stable resource key.</param>
/// <param name="Capacity">How much work may run in parallel.</param>
/// <param name="CalendarRevision">Calendar revision the plan is pinned to.</param>
public sealed record SolverResource(string ResourceKey, decimal Capacity, int CalendarRevision);

/// <summary>Everything the solver needs, and nothing it does not.</summary>
public sealed record SchedulingRequest
{
    /// <summary>Operations to place.</summary>
    public required IReadOnlyList<SolverOperation> Operations { get; init; }

    /// <summary>Precedence constraints; may be empty.</summary>
    public IReadOnlyList<SolverDependency> Dependencies { get; init; } = [];

    /// <summary>Resources with their capacity and pinned calendar revision.</summary>
    public required IReadOnlyList<SolverResource> Resources { get; init; }
}

/// <summary>Why a solver could not place an operation, or placed it with a caveat.</summary>
public enum SchedulingDiagnosticCode
{
    /// <summary>The operation was placed where a fixed start demanded, ignoring precedence.</summary>
    FixedStartOverridesPrecedence,

    /// <summary>A partial release pushed the start later than the precedence bound.</summary>
    PartialReleaseDelaysStart,

    /// <summary>The operation was placed although its standard was incomplete.</summary>
    PlacedDespiteIncompleteStandard,
}

/// <summary>One remark about the solution the planner must be able to see.</summary>
/// <param name="Code">What happened.</param>
/// <param name="OperationId">Which operation it concerns.</param>
public sealed record SchedulingDiagnostic(SchedulingDiagnosticCode Code, string OperationId);

/// <summary>What the solver produced.</summary>
public sealed record SchedulingSolution
{
    /// <summary>The placed operations, ready to become a revision.</summary>
    public required IReadOnlyList<OperationPlan> Operations { get; init; }

    /// <summary>The resolved precedence network, with attribution and warnings.</summary>
    public required IReadOnlyList<PlannedDependency> Dependencies { get; init; }

    /// <summary>Calendar revision per resource — the plan is only reproducible with it.</summary>
    public required IReadOnlyDictionary<string, int> CalendarRevisions { get; init; }

    /// <summary>Remarks the planner must see.</summary>
    public IReadOnlyList<SchedulingDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// False when the search that produced this plan was not reproducible.
    /// </summary>
    /// <remarks>
    /// ADR-070 D3: parallel CP-SAT search is opt-in and may return a different — equally
    /// good — plan for the same input. Such a result must NOT pretend its content hash is a
    /// stable identity, because Doorstar quotes that hash back and a "changed" plan that is
    /// actually the same would start a pointless approval round.
    /// </remarks>
    public bool IsReproducible { get; init; } = true;
}

/// <summary>
/// The port every scheduling strategy implements (ADR-069 §5, ADR-070 D1).
/// </summary>
/// <remarks>
/// A PORT rather than a direct dependency on a solver library: the CP-SAT adapter lives in
/// the infrastructure layer with its native binaries, while the domain — and every test of
/// it — stays free of them. It also means the reference implementation and the optimiser can
/// be measured against the same cases, which is the only honest way to tell whether the
/// optimiser is actually better.
/// </remarks>
public interface ISchedulingSolver
{
    /// <summary>Places every operation of the request.</summary>
    /// <exception cref="ArgumentException">The request is inconsistent (cycle, unknown resource).</exception>
    SchedulingSolution Solve(SchedulingRequest request);
}
