using System;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Dated calendar exceptions (ADR-069 §4) and their effect on working-time calculations.
/// </summary>
/// <remarks>
/// The load-bearing case is the release threshold spanning a shutdown: without exceptions the
/// calculator counts a closed day as productive and releases the successor against output
/// that was never produced.
/// </remarks>
public sealed class CalendarExceptionTests
{
    private const string Budapest = "Europe/Budapest";

    private static LocalTimeRange Range(int startHour, int endHour) =>
        new(new LocalTime(startHour, 0), new LocalTime(endHour, 0));

    private static DayRange Day(int startHour, int endHour) => new(startHour * 60, endHour * 60);

    /// <summary>Wednesday and Thursday 08:00-16:00, no breaks: 480 minutes each.</summary>
    private static WorkingCalendar Calendar(params CalendarException[] exceptions) =>
        new(Budapest,
        [
            new ShiftDefinition { Weekday = IsoDayOfWeek.Wednesday, Span = Range(8, 16) },
            new ShiftDefinition { Weekday = IsoDayOfWeek.Thursday, Span = Range(8, 16) },
        ],
        exceptions);

    private static readonly DateOnly WednesdayDate = new(2026, 7, 29);
    private static readonly LocalDate Wednesday = new(2026, 7, 29);
    private static readonly LocalDate Thursday = new(2026, 7, 30);

    private static Instant Local(int day, int hour, int minute = 0) =>
        new LocalDateTime(2026, 7, day, hour, minute)
            .InZoneLeniently(DateTimeZoneProviders.Tzdb[Budapest])
            .ToInstant();

    [Fact]
    public void A_full_day_closure_removes_all_working_time()
    {
        var calendar = Calendar(CalendarException.Create(
            Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure, reason: "plant shutdown"));

        Assert.Equal(0m, calendar.NetMinutesOn(Wednesday));
        Assert.Empty(calendar.IntervalsOn(Wednesday));
    }

    [Fact]
    public void A_partial_maintenance_span_removes_only_its_own_hours()
    {
        var calendar = Calendar(CalendarException.Create(
            Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Maintenance, Day(10, 12)));

        Assert.Equal(360m, calendar.NetMinutesOn(Wednesday)); // 480 - 120
        Assert.Equal(2, calendar.IntervalsOn(Wednesday).Count); // 08-10 and 12-16
    }

    [Fact]
    public void Overtime_adds_working_time_beyond_the_shift()
    {
        var calendar = Calendar(CalendarException.Create(
            Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Overtime, Day(16, 18)));

        Assert.Equal(600m, calendar.NetMinutesOn(Wednesday)); // 480 + 120
    }

    [Fact]
    public void Overtime_survives_a_closure_on_the_same_day()
    {
        // A closure says "normally nobody works"; approved overtime is a deliberate statement
        // that someone WILL. The explicit decision must win over the blanket one.
        var calendar = Calendar(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure),
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Overtime, Day(9, 13)));

        Assert.Equal(240m, calendar.NetMinutesOn(Wednesday));
    }

    [Fact]
    public void Overlapping_overtime_is_not_counted_twice()
    {
        var calendar = Calendar(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Overtime, Day(15, 18)));

        // 15:00-16:00 is already inside the shift; only 16:00-18:00 is new.
        Assert.Equal(600m, calendar.NetMinutesOn(Wednesday));
    }

    [Fact]
    public void The_release_threshold_skips_a_closed_day()
    {
        // THE case this whole feature exists for. Wednesday is closed, so the job's working
        // time is Thursday's 480 minutes only. Half of it is 240 minutes -> Thursday 12:00.
        // Without exceptions the calculator would see 960 minutes and answer Wednesday 16:00 —
        // releasing against output produced on a day the plant never opened.
        var calculator = new WorkingTimeReleaseCalculator(Calendar(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure)));

        var release = calculator.CalculateReleaseInstant(Local(29, 8), Local(30, 16), 0.5m);

        Assert.Equal(Local(30, 12), release);
    }

    [Fact]
    public void The_release_threshold_uses_overtime_hours_too()
    {
        // 480 + 120 = 600 minutes; half is 300 -> 08:00 + 300 min = 13:00.
        var calculator = new WorkingTimeReleaseCalculator(Calendar(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Overtime, Day(16, 18))));

        var release = calculator.CalculateReleaseInstant(Local(29, 8), Local(29, 18), 0.5m);

        Assert.Equal(Local(29, 13), release);
    }

    [Fact]
    public void A_day_fully_closed_leaves_no_release_point_at_all()
    {
        var calculator = new WorkingTimeReleaseCalculator(Calendar(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure)));

        Assert.Throws<InvalidOperationException>(
            () => calculator.CalculateReleaseInstant(Local(29, 8), Local(29, 16), 0.5m));
    }

    [Fact]
    public void An_exception_on_another_date_changes_nothing()
    {
        var calendar = Calendar(CalendarException.Create(
            Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure));

        Assert.Equal(480m, calendar.NetMinutesOn(Thursday));
    }

    [Fact]
    public void Overtime_without_a_span_is_refused()
    {
        var exception = Assert.Throws<ArgumentException>(() => CalendarException.Create(
            Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Overtime));

        Assert.Contains("explicit span", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approved_revision_refuses_new_exceptions()
    {
        // Changing an approved revision would rewrite history for every plan computed
        // against it — exactly what revisioning exists to prevent.
        var revision = ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), Guid.NewGuid(), "cnc-1", 1, Budapest, 1m, CapacityPolicy.Integer,
            DateTimeOffset.UnixEpoch, [new RecurringShift(3, new DayRange(480, 960), [])]);
        revision.Approve();

        Assert.Throws<InvalidOperationException>(() => revision.AddException(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure)));
    }

    [Fact]
    public void The_same_kind_cannot_be_added_twice_for_one_date()
    {
        var revision = ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), Guid.NewGuid(), "cnc-1", 1, Budapest, 1m, CapacityPolicy.Integer,
            DateTimeOffset.UnixEpoch, [new RecurringShift(3, new DayRange(480, 960), [])]);

        revision.AddException(CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure));

        Assert.Throws<InvalidOperationException>(() => revision.AddException(
            CalendarException.Create(Guid.NewGuid(), WednesdayDate, CalendarExceptionKind.Closure)));
    }
}
