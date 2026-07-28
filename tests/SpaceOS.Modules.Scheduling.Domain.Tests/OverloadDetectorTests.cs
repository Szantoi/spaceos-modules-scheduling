using System;
using System.Collections.Generic;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Overload detection (ADR-069 §6, R5): when is a resource oversubscribed, and by how much.
/// </summary>
public sealed class OverloadDetectorTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly KernelWorkScope Scope = KernelWorkScope.Create(
        ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb")),
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    private static DateTimeOffset At(int hour) => new(2026, 7, 29, hour, 0, 0, TimeSpan.Zero);

    private static CapacityReservation Reservation(int startHour, int endHour, decimal quantity = 1m) =>
        CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, "cnc-1", Scope, At(startHour), At(endHour), quantity,
            At(0), TimeSpan.FromHours(48));

    [Fact]
    public void Demand_within_capacity_reports_nothing()
    {
        var spans = OverloadDetector.Detect([Reservation(8, 12), Reservation(8, 12)], capacity: 2m);

        Assert.Empty(spans);
    }

    [Fact]
    public void Demand_above_capacity_reports_the_overlapping_period_only()
    {
        // 08-12 and 10-14 on a capacity of 1: only 10-12 is actually oversubscribed.
        var spans = OverloadDetector.Detect([Reservation(8, 12), Reservation(10, 14)], capacity: 1m);

        var span = Assert.Single(spans);
        Assert.Equal(At(10), span.StartUtc);
        Assert.Equal(At(12), span.EndUtc);
        Assert.Equal(2m, span.PeakDemand);
        Assert.Equal(1m, span.PeakExcess);
    }

    [Fact]
    public void Contiguous_overload_is_merged_and_reports_the_peak()
    {
        // 08-16 (1) + 09-15 (1) + 10-11 (1) on capacity 1: over from 09 to 15, worst at 10-11.
        var spans = OverloadDetector.Detect(
            [Reservation(8, 16), Reservation(9, 15), Reservation(10, 11)], capacity: 1m);

        var span = Assert.Single(spans);
        Assert.Equal(At(9), span.StartUtc);
        Assert.Equal(At(15), span.EndUtc);
        Assert.Equal(3m, span.PeakDemand);
    }

    [Fact]
    public void Separate_overloads_stay_separate()
    {
        var spans = OverloadDetector.Detect(
            [Reservation(8, 10), Reservation(8, 10), Reservation(14, 16), Reservation(14, 16)],
            capacity: 1m);

        Assert.Equal(2, spans.Count);
        Assert.Equal(At(8), spans[0].StartUtc);
        Assert.Equal(At(14), spans[1].StartUtc);
    }

    [Fact]
    public void A_handover_at_the_same_instant_is_not_an_overlap()
    {
        // One ends exactly when the next starts. Half-open intervals: nobody is doubled up.
        var spans = OverloadDetector.Detect([Reservation(8, 12), Reservation(12, 16)], capacity: 1m);

        Assert.Empty(spans);
    }

    [Fact]
    public void Released_reservations_do_not_count()
    {
        // The schedule already resolved this one; reporting it would send a planner chasing
        // an overload that no longer exists.
        var released = Reservation(8, 12);
        released.Release();

        Assert.Empty(OverloadDetector.Detect([Reservation(8, 12), released], capacity: 1m));
    }

    [Fact]
    public void Fractional_quantities_accumulate()
    {
        // Fractional-FTE capacity: three half-people exceed a capacity of one.
        var spans = OverloadDetector.Detect(
            [Reservation(8, 12, 0.5m), Reservation(8, 12, 0.5m), Reservation(8, 12, 0.5m)],
            capacity: 1m);

        Assert.Equal(1.5m, Assert.Single(spans).PeakDemand);
    }

    [Fact]
    public void Exactly_at_capacity_is_not_overload()
    {
        Assert.Empty(OverloadDetector.Detect([Reservation(8, 12), Reservation(8, 12)], capacity: 2m));
    }

    [Fact]
    public void Zero_capacity_makes_any_reservation_an_overload()
    {
        // A closed resource with work booked on it: exactly what the planner must see.
        var span = Assert.Single(OverloadDetector.Detect([Reservation(8, 12)], capacity: 0m));

        Assert.Equal(1m, span.PeakExcess);
    }

    [Fact]
    public void Negative_capacity_is_a_caller_bug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverloadDetector.Detect(new List<CapacityReservation>(), capacity: -1m));
    }
}
