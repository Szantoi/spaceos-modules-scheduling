using System;
using System.Collections.Generic;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Resource calendar revisions (ADR-069 §4/§5): a calendar change must never silently
/// rewrite the calendar an existing plan was computed against.
/// </summary>
public sealed class ResourceCalendarRevisionTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset From = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static DayRange Range(int startHour, int startMinute, int endHour, int endMinute) =>
        new((startHour * 60) + startMinute, (endHour * 60) + endMinute);

    private static RecurringShift Weekday(int isoWeekday) =>
        new(isoWeekday, Range(7, 0, 16, 0), [Range(9, 0, 9, 20), Range(12, 0, 12, 30), Range(14, 0, 14, 10)]);

    private static ResourceCalendarRevision Create(
        IReadOnlyList<RecurringShift>? shifts = null,
        decimal capacity = 1m,
        CapacityPolicy policy = CapacityPolicy.Integer) =>
        ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), TenantId, "cnc-1", 1, "Europe/Budapest", capacity, policy, From,
            shifts ?? [Weekday(3)]);

    [Fact]
    public void The_documented_pattern_yields_four_hundred_and_eighty_nominal_minutes()
    {
        var calendar = Create();

        Assert.Equal(480, calendar.NominalNetMinutesOn(3));
        Assert.Equal(0, calendar.NominalNetMinutesOn(6)); // Saturday: no shift
    }

    [Fact]
    public void A_draft_is_not_schedulable_until_it_is_approved()
    {
        var calendar = Create();
        Assert.False(calendar.IsApproved);

        calendar.Approve();
        Assert.True(calendar.IsApproved);

        // Double approval is a workflow error, not an idempotent no-op: it usually means two
        // reviewers acted on stale state.
        Assert.Throws<InvalidOperationException>(calendar.Approve);
    }

    [Fact]
    public void A_revision_stays_open_ended_until_a_newer_one_takes_over()
    {
        var calendar = Create();
        Assert.Null(calendar.EffectiveToUtc);

        calendar.CloseAt(From.AddDays(30));
        Assert.Equal(From.AddDays(30), calendar.EffectiveToUtc);
    }

    [Fact]
    public void A_revision_cannot_end_before_it_starts()
    {
        Assert.Throws<ArgumentException>(() => Create().CloseAt(From.AddDays(-1)));
    }

    [Fact]
    public void A_calendar_must_carry_its_iana_zone()
    {
        // Without the zone a local shift cannot be placed on the absolute timeline at all.
        var exception = Assert.Throws<ArgumentException>(() => ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), TenantId, "cnc-1", 1, "   ", 1m, CapacityPolicy.Integer, From, [Weekday(1)]));

        Assert.Contains("IANA time zone", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_calendar_must_belong_to_a_tenant_and_a_resource()
    {
        Assert.Throws<ArgumentException>(() => ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), Guid.Empty, "cnc-1", 1, "Europe/Budapest", 1m, CapacityPolicy.Integer, From, []));

        Assert.Throws<ArgumentException>(() => ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), TenantId, " ", 1, "Europe/Budapest", 1m, CapacityPolicy.Integer, From, []));
    }

    [Fact]
    public void Fractional_capacity_is_refused_under_the_integer_policy()
    {
        // Rounding it away would quietly change how much work the resource can absorb.
        var exception = Assert.Throws<ArgumentException>(
            () => Create(capacity: 1.5m, policy: CapacityPolicy.Integer));

        Assert.Contains("fractional", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fractional_capacity_is_allowed_when_the_policy_says_so()
    {
        var calendar = Create(capacity: 1.5m, policy: CapacityPolicy.FractionalFte);

        Assert.Equal(1.5m, calendar.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_capacity_is_refused(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(capacity: capacity));
    }

    [Fact]
    public void Two_shifts_on_the_same_weekday_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Create([Weekday(1), Weekday(1)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void An_out_of_range_weekday_is_refused(int isoWeekday)
    {
        Assert.Throws<ArgumentException>(
            () => Create([new RecurringShift(isoWeekday, Range(7, 0, 16, 0), [])]));
    }

    [Fact]
    public void A_shift_must_stay_inside_one_day()
    {
        Assert.Throws<ArgumentException>(
            () => Create([new RecurringShift(1, new DayRange(1380, 1500), [])]));
    }

    [Fact]
    public void A_break_outside_its_shift_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => Create([new RecurringShift(1, Range(7, 0, 16, 0), [Range(17, 0, 17, 30)])]));
    }

    [Fact]
    public void Overlapping_breaks_are_refused()
    {
        Assert.Throws<ArgumentException>(
            () => Create([new RecurringShift(1, Range(7, 0, 16, 0), [Range(9, 0, 9, 30), Range(9, 15, 9, 45)])]));
    }

    [Fact]
    public void An_empty_shift_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => Create([new RecurringShift(1, Range(7, 0, 7, 0), [])]));
    }
}
