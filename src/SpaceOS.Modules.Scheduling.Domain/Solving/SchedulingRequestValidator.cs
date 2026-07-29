using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;

namespace SpaceOS.Modules.Scheduling.Domain.Solving;

/// <summary>A request that passed validation, with the derived lookups every strategy needs.</summary>
/// <param name="Operations">Operations by id.</param>
/// <param name="TopologicalOrder">A deterministic order in which predecessors come first.</param>
/// <param name="Capacities">Capacity by resource key.</param>
public sealed record ValidatedSchedulingRequest(
    IReadOnlyDictionary<string, SolverOperation> Operations,
    IReadOnlyList<string> TopologicalOrder,
    IReadOnlyDictionary<string, decimal> Capacities);

/// <summary>
/// Decides whether a <see cref="SchedulingRequest"/> can be scheduled at all — for every
/// strategy, not just one.
/// </summary>
/// <remarks>
/// Shared deliberately: "this request is impossible" must not be a per-solver opinion. If the
/// reference refused a cycle and the CP-SAT adapter merely returned an odd plan for it, the
/// port would stop being a like-for-like comparison, and which answer a caller got would
/// depend on configuration.
/// </remarks>
public static class SchedulingRequestValidator
{
    /// <summary>Validates the request and derives the shared lookups.</summary>
    /// <exception cref="ArgumentException">
    /// The request is inconsistent: duplicate or negative-duration operations, an unknown or
    /// non-positive-capacity resource, or a dependency network that cannot be ordered.
    /// </exception>
    public static ValidatedSchedulingRequest Validate(SchedulingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        var resourceKeys = request.Resources.Select(resource => resource.ResourceKey).ToHashSet(StringComparer.Ordinal);
        if (resourceKeys.Count != request.Resources.Count)
        {
            throw new ArgumentException("A resource appears more than once in the request.", nameof(request));
        }

        foreach (var resource in request.Resources.Where(resource => resource.Capacity <= 0m))
        {
            // Capacity zero makes every operation on it unplaceable: a search would either run
            // forever looking for a slot that cannot exist, or quietly return an infeasible plan.
            throw new ArgumentException(
                $"Resource '{resource.ResourceKey}' has non-positive capacity {resource.Capacity}.", nameof(request));
        }

        var orphan = request.Operations.FirstOrDefault(operation => !resourceKeys.Contains(operation.ResourceKey));
        if (orphan is not null)
        {
            throw new ArgumentException(
                $"Operation '{orphan.OperationId}' runs on resource '{orphan.ResourceKey}', which is not in the request.",
                nameof(request));
        }

        var capacities = request.Resources.ToDictionary(
            resource => resource.ResourceKey, resource => resource.Capacity, StringComparer.Ordinal);

        ValidateFixedStarts(request, capacities);

        return new ValidatedSchedulingRequest(
            operations,
            TopologicalOrder(request, operations),
            capacities);
    }

    /// <summary>
    /// Refuses fixed starts that cannot all fit their resource at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two operations pinned to the same minute on a single-capacity resource is a
    /// CONTRADICTION IN THE REQUEST, not a scheduling problem — no strategy can honour both
    /// pins and the capacity. Deciding it here (business owner decision, 2026-07-29) keeps the
    /// strategies from disagreeing about it: previously the optimiser proved it unsatisfiable
    /// and threw, while the reference placed both and silently exceeded the capacity. Which
    /// answer a planner got depended on configuration, which is the worst of the three
    /// outcomes.
    /// </para>
    /// <para>
    /// Only FIXED starts are checked. Everything else is exactly what the solver is for: it
    /// may queue operations, and an "overloaded" resource with free starts is a plan, not a
    /// contradiction.
    /// </para>
    /// </remarks>
    private static void ValidateFixedStarts(
        SchedulingRequest request,
        IReadOnlyDictionary<string, decimal> capacities)
    {
        var pinned = request.Operations
            .Where(operation => operation.FixedStartMinute.HasValue)
            // A zero-length milestone occupies nothing, so it cannot contend for capacity.
            .Where(operation => operation.DurationMinutes > 0m)
            .OrderBy(operation => operation.FixedStartMinute!.Value)
            .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ToList();

        foreach (var group in pinned.GroupBy(operation => operation.ResourceKey, StringComparer.Ordinal))
        {
            // Whole units of capacity: a 2.5-capacity resource admits two concurrent
            // operations, and anything below one still admits one. Same rule the strategies
            // apply, so the validator cannot refuse a request they would have accepted.
            var concurrent = Math.Max(1m, Math.Floor(capacities[group.Key]));
            var pins = group.ToList();

            // The count can only rise at a start, so those are the only instants to sample.
            foreach (var candidate in pins)
            {
                var instant = candidate.FixedStartMinute!.Value;
                var overlapping = pins
                    .Where(operation => operation.FixedStartMinute!.Value <= instant
                        && operation.FixedStartMinute!.Value + operation.DurationMinutes > instant)
                    .ToList();

                if (overlapping.Count > concurrent)
                {
                    var names = string.Join(", ", overlapping.Select(operation => $"'{operation.OperationId}'"));
                    throw new ArgumentException(
                        $"Operations {names} are pinned to resource '{group.Key}' at minute {instant}, " +
                        $"but it can run {concurrent} at a time. A fixed start overrides the dependency " +
                        "network, never the resource's capacity — move a pin or raise the capacity.",
                        nameof(request));
                }
            }
        }
    }

    private static IReadOnlyList<string> TopologicalOrder(
        SchedulingRequest request,
        Dictionary<string, SolverOperation> operations)
    {
        // Reuses the graph validator rather than sorting again here: it already refuses
        // cycles, self-edges and dangling references with named issue codes, and its Kahn sort
        // is deterministic. A second sort would be a second definition of "valid".
        var validation = DependencyGraph.Validate(
            [.. operations.Keys.OrderBy(id => id, StringComparer.Ordinal).Select(id => new OperationNode(id))],
            [.. request.Dependencies.Select(dependency => new DependencyEdge
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
}
