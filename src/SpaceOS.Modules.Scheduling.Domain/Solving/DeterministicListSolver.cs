using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;

namespace SpaceOS.Modules.Scheduling.Domain.Solving;

/// <summary>
/// Places every operation as early as precedence and finite capacity allow (ADR-069 §5).
/// </summary>
/// <remarks>
/// <para>
/// This is the REFERENCE strategy: a list scheduler, not an optimiser. It answers "when can
/// this actually run" — earliest feasible start, honouring the precedence rules, the partial
/// release contract and the resource's parallel capacity. The CP-SAT adapter (ADR-070) will
/// optimise makespan on the same port and is measured against this one.
/// </para>
/// <para>
/// <b>Determinism is a requirement, not a happy accident</b> (ADR-070 D3). The revision hash
/// is computed from the content, and Doorstar quotes it back; two runs of the same input that
/// produced different-but-equal plans would look like a change and start an approval round
/// for nothing. Every ordering here is therefore explicit and ordinal: the topological order
/// comes from the deterministic Kahn sort, ties break on the operation id, and no dictionary
/// enumeration order is ever allowed to decide anything.
/// </para>
/// <para>
/// Placement is greedy and never backtracks. That is a real limitation — a greedy list
/// schedule can be worse than optimal — and it is the reason the port exists rather than
/// this class being the whole story.
/// </para>
/// </remarks>
public sealed class DeterministicListSolver : ISchedulingSolver
{
    /// <inheritdoc />
    public SchedulingSolution Solve(SchedulingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operations = Validate(request);
        var capacities = request.Resources.ToDictionary(
            resource => resource.ResourceKey, resource => resource.Capacity, StringComparer.Ordinal);

        var order = TopologicalOrder(request, operations);

        // Placed intervals per resource, so capacity can be checked without rescanning the
        // whole plan. Kept sorted by start for the scan below.
        var occupancy = request.Resources.ToDictionary(
            resource => resource.ResourceKey,
            _ => new List<(decimal Start, decimal Finish)>(),
            StringComparer.Ordinal);

        var placed = new Dictionary<string, OperationPlan>(StringComparer.Ordinal);
        var edges = new List<PlannedDependency>();
        var diagnostics = new List<SchedulingDiagnostic>();

        foreach (var operationId in order)
        {
            var operation = operations[operationId];
            var (earliest, resolved) = ResolveEarliestStart(request, operation, placed, diagnostics);

            var start = operation.FixedStartMinute
                ?? FirstFeasibleStart(earliest, operation, occupancy[operation.ResourceKey], capacities[operation.ResourceKey]);
            var finish = start + operation.DurationMinutes;

            if (!operation.EligibleForAutomaticPlanning)
            {
                diagnostics.Add(new SchedulingDiagnostic(
                    SchedulingDiagnosticCode.PlacedDespiteIncompleteStandard, operation.OperationId));
            }

            placed[operationId] = new OperationPlan
            {
                OperationId = operation.OperationId,
                Scope = operation.Scope,
                ResourceKey = operation.ResourceKey,
                StartMinute = start,
                FinishMinute = finish,
                AutomaticallyPlanned = operation.EligibleForAutomaticPlanning,
                StandardRevision = operation.StandardRevision,
                SourceRevisions = operation.SourceRevisions,
            };

            // A zero-length milestone occupies nothing: recording it would block a slot for an
            // instant that consumes no capacity.
            if (finish > start)
            {
                Insert(occupancy[operation.ResourceKey], (start, finish));
            }

            edges.AddRange(resolved);
        }

        return new SchedulingSolution
        {
            Operations = [.. order.Select(id => placed[id])],
            Dependencies = [.. edges
                .OrderBy(edge => edge.PredecessorOperationId, StringComparer.Ordinal)
                .ThenBy(edge => edge.SuccessorOperationId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Relation)],
            CalendarRevisions = request.Resources.ToDictionary(
                resource => resource.ResourceKey, resource => resource.CalendarRevision, StringComparer.Ordinal),
            Diagnostics = diagnostics,
            IsReproducible = true,
        };
    }

    private static Dictionary<string, SolverOperation> Validate(SchedulingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Operations);
        ArgumentNullException.ThrowIfNull(request.Resources);

        var operations = new Dictionary<string, SolverOperation>(StringComparer.Ordinal);
        foreach (var operation in request.Operations)
        {
            if (operation.DurationMinutes < 0m)
            {
                throw new ArgumentException(
                    $"Operation '{operation.OperationId}' has a negative duration.", nameof(request));
            }

            if (!operations.TryAdd(operation.OperationId, operation))
            {
                throw new ArgumentException(
                    $"Operation '{operation.OperationId}' appears more than once.", nameof(request));
            }
        }

        var resources = request.Resources.Select(resource => resource.ResourceKey).ToHashSet(StringComparer.Ordinal);
        if (resources.Count != request.Resources.Count)
        {
            throw new ArgumentException("A resource appears more than once in the request.", nameof(request));
        }

        foreach (var resource in request.Resources.Where(resource => resource.Capacity <= 0m))
        {
            // Capacity zero would make every operation on it unplaceable, and the loop below
            // would search forever for a slot that cannot exist.
            throw new ArgumentException(
                $"Resource '{resource.ResourceKey}' has non-positive capacity {resource.Capacity}.", nameof(request));
        }

        var orphan = request.Operations.FirstOrDefault(operation => !resources.Contains(operation.ResourceKey));
        if (orphan is not null)
        {
            throw new ArgumentException(
                $"Operation '{orphan.OperationId}' runs on resource '{orphan.ResourceKey}', which is not in the request.",
                nameof(request));
        }

        return operations;
    }

    private static IReadOnlyList<string> TopologicalOrder(
        SchedulingRequest request,
        Dictionary<string, SolverOperation> operations)
    {
        // Reuses the graph validator rather than sorting again here: it already refuses
        // cycles, self-edges and dangling references with named issue codes, and its Kahn
        // sort is deterministic. A second sort would be a second definition of "valid".
        var validation = DependencyGraph.Validate(
            [.. operations.Keys.OrderBy(id => id, StringComparer.Ordinal).Select(id => new OperationNode(id))],
            [.. request.Dependencies.Select(dependency => new Dependencies.DependencyEdge
            {
                PredecessorId = dependency.PredecessorOperationId,
                SuccessorId = dependency.SuccessorOperationId,
                Type = RelationCode(dependency.Relation),
                LagMinutes = dependency.LagMinutes,
                ReleaseThresholdFraction = dependency.ReleaseThresholdFraction,
            })]);

        if (!validation.IsValid || validation.TopologicalOrder is null)
        {
            var codes = string.Join(", ", validation.Issues.Select(issue => issue.Code).Distinct());
            throw new ArgumentException(
                $"The dependency network cannot be scheduled: {codes}.", nameof(request));
        }

        return validation.TopologicalOrder;
    }

    private static string RelationCode(DependencyType relation) => relation switch
    {
        DependencyType.FinishToStart => "FS",
        DependencyType.StartToStart => "SS",
        DependencyType.FinishToFinish => "FF",
        DependencyType.StartToFinish => "SF",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unmapped relation."),
    };

    /// <summary>
    /// Combines every incoming edge into one earliest start, and records what each edge
    /// resolved to.
    /// </summary>
    private static (decimal Earliest, List<PlannedDependency> Edges) ResolveEarliestStart(
        SchedulingRequest request,
        SolverOperation operation,
        IReadOnlyDictionary<string, OperationPlan> placed,
        List<SchedulingDiagnostic> diagnostics)
    {
        var earliest = 0m;
        var edges = new List<PlannedDependency>();

        var incoming = request.Dependencies
            .Where(dependency => string.Equals(
                dependency.SuccessorOperationId, operation.OperationId, StringComparison.Ordinal))
            .OrderBy(dependency => dependency.PredecessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Relation);

        foreach (var dependency in incoming)
        {
            // The topological order guarantees the predecessor is already placed.
            var predecessor = placed[dependency.PredecessorOperationId];

            var resolved = DependencyBoundResolver.Resolve(new DependencyBoundInput
            {
                Type = dependency.Relation,
                PredecessorStartMinute = predecessor.StartMinute,
                PredecessorFinishMinute = predecessor.FinishMinute,
                LagMinutes = dependency.LagMinutes,
                PartialReleaseMinute = PartialRelease(dependency, predecessor),
                FixedStartMinute = operation.FixedStartMinute,
            });

            if (resolved.EarliestStartMinute is { } bound)
            {
                earliest = Math.Max(earliest, bound);
            }

            edges.Add(new PlannedDependency
            {
                PredecessorOperationId = dependency.PredecessorOperationId,
                SuccessorOperationId = dependency.SuccessorOperationId,
                Relation = dependency.Relation,
                LagMinutes = dependency.LagMinutes,
                EarliestStartMinute = resolved.EarliestStartMinute,
                StartSource = resolved.StartSource,
                Warnings = resolved.Warnings,
            });

            if (resolved.StartSource == BoundSource.FixedOverride)
            {
                diagnostics.Add(new SchedulingDiagnostic(
                    SchedulingDiagnosticCode.FixedStartOverridesPrecedence, operation.OperationId));
            }

            if (resolved.Warnings.Contains(DependencyWarning.PartialReleaseDelaysStart))
            {
                diagnostics.Add(new SchedulingDiagnostic(
                    SchedulingDiagnosticCode.PartialReleaseDelaysStart, operation.OperationId));
            }
        }

        return (earliest, edges);
    }

    private static decimal? PartialRelease(SolverDependency dependency, OperationPlan predecessor) =>
        dependency.ReleaseThresholdFraction is { } fraction
            ? predecessor.StartMinute + ((predecessor.FinishMinute - predecessor.StartMinute) * fraction)
            : null;

    /// <summary>
    /// Finds the first instant at or after <paramref name="earliest"/> where the resource has
    /// room for one more concurrent operation.
    /// </summary>
    /// <remarks>
    /// Capacity is counted as SIMULTANEOUS operations, so the only instants where the count
    /// can drop are the finishes of already-placed work. Scanning those candidates is exact —
    /// no time step to tune, and no chance of stepping over a gap that would have fitted.
    /// </remarks>
    private static decimal FirstFeasibleStart(
        decimal earliest,
        SolverOperation operation,
        IReadOnlyList<(decimal Start, decimal Finish)> occupancy,
        decimal capacity)
    {
        if (operation.DurationMinutes <= 0m)
        {
            // A milestone consumes nothing, so capacity cannot keep it waiting.
            return earliest;
        }

        var candidates = new List<decimal> { earliest };
        candidates.AddRange(occupancy.Select(interval => interval.Finish).Where(finish => finish > earliest));
        candidates.Sort();

        foreach (var candidate in candidates)
        {
            var finish = candidate + operation.DurationMinutes;

            // Half-open intervals: work ending exactly when other work starts is a handover.
            var peak = occupancy.Count(interval => interval.Start < finish && interval.Finish > candidate);
            if (peak + 1 <= capacity)
            {
                return candidate;
            }
        }

        // Every candidate was full, so the last finish is when the resource frees up.
        return occupancy.Count == 0 ? earliest : occupancy.Max(interval => interval.Finish);
    }

    private static void Insert(List<(decimal Start, decimal Finish)> occupancy, (decimal Start, decimal Finish) interval)
    {
        var index = occupancy.FindIndex(existing => existing.Start > interval.Start);
        if (index < 0)
        {
            occupancy.Add(interval);
            return;
        }

        occupancy.Insert(index, interval);
    }
}
