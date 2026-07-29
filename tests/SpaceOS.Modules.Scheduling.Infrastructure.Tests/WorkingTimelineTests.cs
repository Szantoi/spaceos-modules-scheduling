using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// The working-minute axis: the bridge between what the solver decides and what a date means.
/// </summary>
/// <remarks>
/// Business owner decision (2026-07-29): an operation's duration is WORKING time and may span
/// non-working time. These cases are that rule made concrete — an overnight gap, a weekend,
/// and the two days a year when a local hour does not exist or exists twice.
/// </remarks>
public sealed class WorkingTimelineTests
{
    private const string Zone = "Europe/Budapest";

    /// <summary>Monday–Friday, 08:00–16:00 local, no breaks.</summary>
    private static WorkingCalendar WeekdayCalendar(IReadOnlyList<CalendarExceptionSpec>? exceptions = null)
    {
        var shifts = new List<ShiftDefinition>();
        foreach (var weekday in new[]
                 {
                     IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday,
                     IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday,
                 })
        {
            shifts.Add(new ShiftDefinition
            {
                Weekday = weekday,
                Span = new LocalTimeRange(new LocalTime(8, 0), new LocalTime(16, 0)),
            });
        }

        return new WorkingCalendar(Zone, shifts, CalendarExceptionSpec.Materialise(exceptions));
    }

    private static Instant LocalInstant(int year, int month, int day, int hour, int minute = 0) =>
        new LocalDateTime(year, month, day, hour, minute)
            .InZoneLeniently(DateTimeZoneProviders.Tzdb[Zone])
            .ToInstant();

    private static WorkingTimeline Timeline(WorkingCalendar calendar, Instant origin, int days = 30) =>
        new(calendar, origin, Duration.FromDays(days));

    [Fact]
    public void Minute_zero_is_the_first_working_instant_not_the_origin()
    {
        // The origin is 03:00 on a Monday — the middle of the night. Work starts at 08:00, and
        // a plan that claimed otherwise would promise a shift nobody is there for.
        var timeline = Timeline(WeekdayCalendar(), LocalInstant(2026, 8, 3, 3));

        Assert.Equal(LocalInstant(2026, 8, 3, 8), timeline.AtWorkingMinute(0m));
    }

    [Fact]
    public void A_full_shift_finishes_on_its_own_day_but_the_next_work_starts_tomorrow()
    {
        // The same axis position, two different answers — and both are right. Eight working
        // hours from Monday 08:00 are COMPLETE at Monday 16:00; work that BEGINS after them
        // can only begin when the resource works again, Tuesday 08:00.
        var timeline = Timeline(WeekdayCalendar(), LocalInstant(2026, 8, 3, 8));

        Assert.Equal(LocalInstant(2026, 8, 3, 16), timeline.EndAtWorkingMinute(480m));
        Assert.Equal(LocalInstant(2026, 8, 4, 8), timeline.AtWorkingMinute(480m));
    }

    [Fact]
    public void Work_beyond_a_shift_continues_the_next_morning()
    {
        // Ten working hours in an eight-hour shift: two hours land on Tuesday. This is the
        // decision made explicit — the machine stops for the night and resumes.
        var timeline = Timeline(WeekdayCalendar(), LocalInstant(2026, 8, 3, 8));

        Assert.Equal(LocalInstant(2026, 8, 4, 10), timeline.AtWorkingMinute(600m));
    }

    [Fact]
    public void A_weekend_is_skipped_rather_than_worked_through()
    {
        // Friday 08:00 + 9 working hours: one hour past Friday's shift, so Monday 09:00.
        var timeline = Timeline(WeekdayCalendar(), LocalInstant(2026, 8, 7, 8));

        Assert.Equal(LocalInstant(2026, 8, 10, 9), timeline.AtWorkingMinute(540m));
    }

    [Fact]
    public void An_hour_that_does_not_exist_cannot_be_worked()
    {
        // Spring transition, Europe/Budapest 2027-03-28: 02:00 jumps to 03:00. A shift running
        // 00:00-08:00 that day is seven REAL hours, so the eighth working hour of the week's
        // start falls on the next day. Clock arithmetic would be an hour out — exactly the bug
        // NodaTime is here to prevent.
        var calendar = new WorkingCalendar(Zone, [
            new ShiftDefinition
            {
                Weekday = IsoDayOfWeek.Sunday,
                Span = new LocalTimeRange(new LocalTime(0, 0), new LocalTime(8, 0)),
            },
        ]);

        var timeline = Timeline(calendar, LocalInstant(2027, 3, 28, 0));

        // 420 working minutes = the whole (shortened) day.
        Assert.Equal(LocalInstant(2027, 3, 28, 8), timeline.EndAtWorkingMinute(420m));
        Assert.Equal(420m, timeline.WorkingMinutesBetween(
            LocalInstant(2027, 3, 28, 0), LocalInstant(2027, 3, 28, 8)));
    }

    [Fact]
    public void An_hour_lived_twice_is_worked_twice()
    {
        // Autumn transition, 2026-10-25: 03:00 falls back to 02:00, so a 00:00-08:00 shift is
        // NINE real hours. The resource genuinely produces for nine hours that day.
        var calendar = new WorkingCalendar(Zone, [
            new ShiftDefinition
            {
                Weekday = IsoDayOfWeek.Sunday,
                Span = new LocalTimeRange(new LocalTime(0, 0), new LocalTime(8, 0)),
            },
        ]);

        var timeline = Timeline(calendar, LocalInstant(2026, 10, 25, 0));

        Assert.Equal(540m, timeline.WorkingMinutesBetween(
            LocalInstant(2026, 10, 25, 0), LocalInstant(2026, 10, 26, 0)));
    }

    [Fact]
    public void A_closure_removes_its_day_from_the_axis()
    {
        var calendar = WeekdayCalendar([new CalendarExceptionSpec(new DateOnly(2026, 8, 4), null)]);
        var timeline = Timeline(calendar, LocalInstant(2026, 8, 3, 8));

        // Tuesday is closed, so the ninth working hour is Wednesday morning.
        Assert.Equal(LocalInstant(2026, 8, 5, 9), timeline.AtWorkingMinute(540m));
    }

    [Fact]
    public void An_instant_outside_working_time_reports_the_work_done_so_far()
    {
        var timeline = Timeline(WeekdayCalendar(), LocalInstant(2026, 8, 3, 8));

        // Tuesday 03:00: Monday's shift is complete, Tuesday's has not started.
        Assert.Equal(480m, timeline.PositionOf(LocalInstant(2026, 8, 4, 3)));
    }

    [Fact]
    public void A_plan_past_the_horizon_is_refused_rather_than_clamped()
    {
        // Silently returning the horizon's end would hand the planner a date the resource
        // never actually reaches.
        var timeline = Timeline(WeekdayCalendar(), LocalInstant(2026, 8, 3, 8), days: 7);

        var error = Assert.Throws<InvalidOperationException>(() => timeline.AtWorkingMinute(100_000m));

        Assert.Contains("horizon", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_calendar_with_no_working_time_refuses_to_place_anything()
    {
        var calendar = new WorkingCalendar(Zone, [
            new ShiftDefinition
            {
                Weekday = IsoDayOfWeek.Sunday,
                Span = new LocalTimeRange(new LocalTime(8, 0), new LocalTime(16, 0)),
            },
        ]);

        // A Monday-to-Saturday horizon never reaches the only shift.
        var timeline = new WorkingTimeline(calendar, LocalInstant(2026, 8, 3, 8), Duration.FromDays(3));

        Assert.Throws<InvalidOperationException>(() => timeline.AtWorkingMinute(0m));
    }
}

/// <summary>A closure (whole day when the span is null), so tests read as intent.</summary>
/// <param name="Date">Local date closed.</param>
/// <param name="Span">Affected span; null closes the whole day.</param>
public sealed record CalendarExceptionSpec(DateOnly Date, DayRange? Span)
{
    /// <summary>Turns the specs into domain exceptions.</summary>
    public static IReadOnlyList<CalendarException> Materialise(IReadOnlyList<CalendarExceptionSpec>? specs) =>
        specs is null
            ? []
            : [.. specs.Select(spec => CalendarException.Create(
                Guid.NewGuid(), spec.Date, CalendarExceptionKind.Closure, spec.Span))];
}
