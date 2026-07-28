using System;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Resource-time reservations (ADR-069 §4): held → confirmed/released/expired, with a TTL
/// on the held state, following the Inventory precedent.
/// </summary>
public sealed class CapacityReservationTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    private static readonly KernelWorkScope Scope = KernelWorkScope.Create(
        ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb")),
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    private static CapacityReservation Hold(
        string resourceKey = "cnc-1",
        int startHour = 9,
        int endHour = 11,
        decimal quantity = 1m,
        TimeSpan? ttl = null) =>
        CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, resourceKey, Scope,
            Now.AddHours(startHour - 8), Now.AddHours(endHour - 8),
            quantity, Now, ttl ?? TimeSpan.FromMinutes(15));

    [Fact]
    public void A_new_reservation_is_held_and_occupies_capacity()
    {
        var reservation = Hold();

        Assert.Equal(CapacityReservationState.Held, reservation.State);
        Assert.True(reservation.OccupiesCapacity);
        Assert.False(reservation.IsTerminal);
        Assert.Equal(Now.AddMinutes(15), reservation.ExpiresAtUtc);
    }

    [Fact]
    public void A_confirmed_reservation_no_longer_expires()
    {
        var reservation = Hold();
        reservation.Confirm();

        // Long past the original TTL: a confirmed reservation must never disappear behind
        // the operator's back.
        var exception = Assert.Throws<InvalidOperationException>(() => reservation.Expire(Now.AddDays(1)));

        Assert.Contains("Confirmed", exception.Message, StringComparison.Ordinal);
        Assert.True(reservation.OccupiesCapacity);
    }

    [Fact]
    public void An_unconfirmed_hold_expires_once_its_ttl_has_passed()
    {
        var reservation = Hold();

        reservation.Expire(Now.AddMinutes(15));

        Assert.Equal(CapacityReservationState.Expired, reservation.State);
        Assert.False(reservation.OccupiesCapacity);
        Assert.True(reservation.IsTerminal);
    }

    [Fact]
    public void A_hold_cannot_be_expired_before_its_ttl()
    {
        // Otherwise capacity a caller still believes it holds would be freed under it.
        var reservation = Hold();

        var exception = Assert.Throws<InvalidOperationException>(() => reservation.Expire(Now.AddMinutes(14)));

        Assert.Contains("valid until", exception.Message, StringComparison.Ordinal);
        Assert.Equal(CapacityReservationState.Held, reservation.State);
    }

    [Fact]
    public void A_released_reservation_frees_its_capacity_and_is_terminal()
    {
        var reservation = Hold();
        reservation.Release();

        Assert.False(reservation.OccupiesCapacity);
        Assert.Throws<InvalidOperationException>(reservation.Release);
        Assert.Throws<InvalidOperationException>(reservation.Confirm);
    }

    [Fact]
    public void A_confirmed_reservation_can_still_be_released()
    {
        var reservation = Hold();
        reservation.Confirm();

        reservation.Release();

        Assert.Equal(CapacityReservationState.Released, reservation.State);
    }

    [Fact]
    public void Only_a_held_reservation_can_be_confirmed()
    {
        var expired = Hold();
        expired.Expire(Now.AddHours(1));

        Assert.Throws<InvalidOperationException>(expired.Confirm);
    }

    [Fact]
    public void Overlapping_holds_on_the_same_resource_conflict()
    {
        var morning = Hold(startHour: 9, endHour: 11);
        var overlapping = Hold(startHour: 10, endHour: 12);

        Assert.True(morning.ConflictsWith(overlapping));
        Assert.True(overlapping.ConflictsWith(morning));
    }

    [Fact]
    public void Touching_intervals_do_not_conflict()
    {
        // The end is exclusive: 09:00-11:00 and 11:00-13:00 are back to back, not overlapping.
        var first = Hold(startHour: 9, endHour: 11);
        var second = Hold(startHour: 11, endHour: 13);

        Assert.False(first.ConflictsWith(second));
    }

    [Fact]
    public void Different_resources_never_conflict()
    {
        Assert.False(Hold(resourceKey: "cnc-1").ConflictsWith(Hold(resourceKey: "cnc-2")));
    }

    [Fact]
    public void A_released_reservation_no_longer_conflicts()
    {
        // Treating history as a conflict would block the slot forever.
        var released = Hold(startHour: 9, endHour: 11);
        released.Release();

        Assert.False(released.ConflictsWith(Hold(startHour: 10, endHour: 12)));
        Assert.False(Hold(startHour: 10, endHour: 12).ConflictsWith(released));
    }

    [Fact]
    public void A_hold_must_belong_to_a_tenant_and_a_resource()
    {
        Assert.Throws<ArgumentException>(() => CapacityReservation.Hold(
            Guid.NewGuid(), Guid.Empty, "cnc-1", Scope, Now, Now.AddHours(1), 1m, Now, TimeSpan.FromMinutes(5)));

        Assert.Throws<ArgumentException>(() => CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, "  ", Scope, Now, Now.AddHours(1), 1m, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void An_empty_or_inverted_interval_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, "cnc-1", Scope, Now, Now, 1m, Now, TimeSpan.FromMinutes(5)));

        Assert.Throws<ArgumentException>(() => CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, "cnc-1", Scope, Now.AddHours(2), Now, 1m, Now, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_quantity_is_refused(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, "cnc-1", Scope, Now, Now.AddHours(1), quantity, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_non_positive_ttl_is_refused()
    {
        // A hold born expired would silently free capacity the caller believes it holds.
        Assert.Throws<ArgumentException>(() => CapacityReservation.Hold(
            Guid.NewGuid(), TenantId, "cnc-1", Scope, Now, Now.AddHours(1), 1m, Now, TimeSpan.Zero));
    }
}
