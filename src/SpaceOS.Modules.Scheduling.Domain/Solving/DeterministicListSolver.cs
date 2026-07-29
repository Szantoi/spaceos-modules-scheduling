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
/// release contract and the resource's parallel capacity. The CP-SAT adapter (ADR-070)
/// optimises makespan on the same port and is measured against this one.
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

        var validated = SchedulingRequestValidator.Validate(request);

        // Placed intervals per resource, so capacity can be checked without rescanning the
        // whole plan. Kept sorted by start for the scan below.
        var occupancy = request.Resources.ToDictionary(
            resource => resource.ResourceKey,
            _ => new List<(decimal Start, decimal Finish)>(),
            StringComparer.Ordinal);

        var placed = new Dictionary<string, OperationPlan>(StringComparer.Ordinal);
        var diagnostics = new List<SchedulingDiagnostic>();

        foreach (var operationId in validated.TopologicalOrder)
        {
            var operation = validated.Operations[operationId];
            var earliest = EarliestStart(request, operation, placed);

            var start = operation.FixedStartMinute
                ?? FirstFeasibleStart(
                    earliest,
                    operation,
                    occupancy[operation.ResourceKey],
                    validated.Capacities[operation.ResourceKey]);
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
        }

        var (edges, edgeDiagnostics) = DependencyProjection.Project(request, validated.Operations, placed);

        return new SchedulingSolution
        {
            Operations = [.. validated.TopologicalOrder.Select(id => placed[id])],
            Dependencies = edges,
            CalendarRevisions = request.Resources.ToDictionary(
                resource => resource.ResourceKey, resource => resource.CalendarRevision, StringComparer.Ordinal),
            Diagnostics = [.. diagnostics, .. edgeDiagnostics],
            IsReproducible = true,
        };
    }

    /// <summary>Combines every incoming edge into one earliest start.</summary>
    /// <remarks>
    /// Only the bound is derived here; what each edge RESOLVED to — its source and warnings —
    /// is projected once, after placement, by <see cref="DependencyProjection"/>, so the
    /// explanation cannot drift from the one the CP-SAT adapter produces.
    /// </remarks>
    private static decimal EarliestStart(
        SchedulingRequest request,
        SolverOperation operation,
        IReadOnlyDictionary<string, OperationPlan> placed)
    {
        var earliest = 0m;

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
                PartialReleaseMinute = DependencyProjection.PartialReleaseMinute(dependency, predecessor),
                FixedStartMinute = operation.FixedStartMinute,
            });

            if (resolved.EarliestStartMinute is { } bound)
            {
                earliest = Math.Max(earliest, bound);
            }

            // The finish branch is a real constraint, not decoration: an FF/SF edge bounds the
            // successor's FINISH, and since the duration is already fixed, that is a bound on
            // its start too. Taking only the start branch left FF/SF edges silently
            // unsatisfied — the plan claimed to honour a dependency it did not.
            if (resolved.EarliestFinishMinute is { } finishBound)
            {
                earliest = Math.Max(earliest, finishBound - operation.DurationMinutes);
            }
        }

        return earliest;
    }

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
