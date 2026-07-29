using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Resources;

/// <summary>An overload period attributed to the resource it happens on.</summary>
/// <param name="ResourceKey">The oversubscribed resource.</param>
/// <param name="Span">When it is over, and by how much at the peak.</param>
public sealed record ResourceOverload(string ResourceKey, OverloadSpan Span);

/// <summary>
/// Answers what a planner asks before publishing: would this plan collide with work that is
/// already committed?
/// </summary>
/// <remarks>
/// <para>
/// The plan's own operations respect capacity — the solver saw to that. What it could not see
/// is everything ALREADY reserved on those resources by other runs and other work. That is the
/// collision this reports, and it is the only reading of "capacity conflict" that tells a
/// planner something they did not already know.
/// </para>
/// <para>
/// It runs the SAME detector as the overload endpoint (root's condition): one definition of
/// "overloaded", so the proposal view and the resource view cannot disagree about the same
/// afternoon.
/// </para>
/// </remarks>
public static class ProposalOverloadAnalyzer
{
    /// <summary>Detects conflicts per resource.</summary>
    /// <param name="demandsByResource">Every demand on each resource: the plan's and the committed.</param>
    /// <param name="capacities">Capacity per resource, from the calendar revision the plan is pinned to.</param>
    /// <exception cref="ArgumentException">A resource has demand but no known capacity.</exception>
    public static IReadOnlyList<ResourceOverload> Detect(
        IReadOnlyDictionary<string, IReadOnlyList<CapacityDemand>> demandsByResource,
        IReadOnlyDictionary<string, decimal> capacities)
    {
        ArgumentNullException.ThrowIfNull(demandsByResource);
        ArgumentNullException.ThrowIfNull(capacities);

        var conflicts = new List<ResourceOverload>();

        // Ordinal ordering: this list is read by a human and compared between reads.
        foreach (var (resourceKey, demands) in demandsByResource.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!capacities.TryGetValue(resourceKey, out var capacity))
            {
                // Refusing beats assuming a capacity: "no conflict" computed against an unknown
                // limit is the most dangerous answer this could give.
                throw new ArgumentException(
                    $"Resource '{resourceKey}' carries demand but no capacity was supplied. " +
                    "A conflict report against an unknown limit would be worthless.",
                    nameof(capacities));
            }

            conflicts.AddRange(OverloadDetector
                .Detect(demands, capacity)
                .Select(span => new ResourceOverload(resourceKey, span)));
        }

        return conflicts;
    }
}
