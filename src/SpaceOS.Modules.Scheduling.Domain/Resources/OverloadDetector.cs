using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Resources;

/// <summary>
/// A period during which committed demand on a resource exceeds its capacity (ADR-069 §6, R5).
/// </summary>
/// <param name="StartUtc">First instant of the overload.</param>
/// <param name="EndUtc">Exclusive end of the overload.</param>
/// <param name="PeakDemand">Highest simultaneous demand inside the period.</param>
/// <param name="Capacity">Capacity the demand was measured against.</param>
public sealed record OverloadSpan(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    decimal PeakDemand,
    decimal Capacity)
{
    /// <summary>How much demand exceeds capacity at the peak.</summary>
    public decimal PeakExcess => PeakDemand - Capacity;
}

/// <summary>One interval of demand on a resource, whatever produced it.</summary>
/// <param name="StartUtc">Inclusive start.</param>
/// <param name="EndUtc">Exclusive end.</param>
/// <param name="Quantity">How much capacity it consumes.</param>
public sealed record CapacityDemand(DateTimeOffset StartUtc, DateTimeOffset EndUtc, decimal Quantity);

/// <summary>
/// Finds the periods where demand oversubscribes a resource.
/// </summary>
/// <remarks>
/// <para>
/// This reports overload, it does not prevent it. Refusing the reservation that tips a
/// resource over would be the wrong call here: the shop floor legitimately overbooks and then
/// resolves it (an extra shift, a subcontractor, a later promise). What a planner needs is to
/// SEE it — silently dropping the last reservation would hide the very decision they have to
/// make.
/// </para>
/// <para>
/// Contiguous oversubscribed segments are merged and reported with their PEAK demand. A
/// planner asks "when am I over, and by how much at worst" — a segment-per-boundary list
/// would answer a question nobody asked and would grow with every reservation added.
/// </para>
/// </remarks>
public static class OverloadDetector
{
    /// <summary>Detects overload periods for one resource.</summary>
    /// <param name="reservations">Reservations for a single resource; non-occupying states are ignored.</param>
    /// <param name="capacity">Parallel capacity from the resource's calendar revision.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is negative.</exception>
    public static IReadOnlyList<OverloadSpan> Detect(
        IEnumerable<CapacityReservation> reservations,
        decimal capacity)
    {
        ArgumentNullException.ThrowIfNull(reservations);

        // Released and expired reservations hold nothing. Including them would report an
        // overload that the schedule has already resolved.
        return Detect(
            reservations
                .Where(reservation => reservation.OccupiesCapacity)
                .Select(reservation => new CapacityDemand(
                    reservation.StartUtc, reservation.EndUtc, reservation.Quantity)),
            capacity);
    }

    /// <summary>Detects overload periods from raw demand intervals.</summary>
    /// <remarks>
    /// The same sweep, one level lower, so a PLANNED operation can be measured against the same
    /// rule as a committed reservation. Two detectors would eventually disagree about what
    /// "overloaded" means, and the proposal view would contradict the overload endpoint.
    /// </remarks>
    /// <param name="demands">Demand intervals on a single resource.</param>
    /// <param name="capacity">Parallel capacity from the resource's calendar revision.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is negative.</exception>
    public static IReadOnlyList<OverloadSpan> Detect(
        IEnumerable<CapacityDemand> demands,
        decimal capacity)
    {
        ArgumentNullException.ThrowIfNull(demands);
        if (capacity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity cannot be negative.");
        }

        var occupying = demands.ToArray();
        if (occupying.Length == 0)
        {
            return [];
        }

        // Every start and end is a point where demand can change; between two adjacent points
        // demand is constant, so the whole timeline is covered by these segments alone.
        var boundaries = occupying
            .SelectMany(demand => new[] { demand.StartUtc, demand.EndUtc })
            .Distinct()
            .OrderBy(instant => instant)
            .ToArray();

        var spans = new List<OverloadSpan>();
        DateTimeOffset? openStart = null;
        DateTimeOffset openEnd = default;
        var peak = 0m;

        for (var index = 0; index < boundaries.Length - 1; index++)
        {
            var segmentStart = boundaries[index];
            var segmentEnd = boundaries[index + 1];

            // Half-open [start, end): a reservation ending exactly when another starts is a
            // handover, not an overlap — the same rule CapacityReservation.ConflictsWith uses.
            var demand = occupying
                .Where(item => item.StartUtc <= segmentStart && item.EndUtc > segmentStart)
                .Sum(item => item.Quantity);

            if (demand > capacity)
            {
                if (openStart is null)
                {
                    openStart = segmentStart;
                    peak = demand;
                }
                else
                {
                    peak = Math.Max(peak, demand);
                }

                openEnd = segmentEnd;
                continue;
            }

            if (openStart is not null)
            {
                spans.Add(new OverloadSpan(openStart.Value, openEnd, peak, capacity));
                openStart = null;
            }
        }

        if (openStart is not null)
        {
            spans.Add(new OverloadSpan(openStart.Value, openEnd, peak, capacity));
        }

        return spans;
    }
}
