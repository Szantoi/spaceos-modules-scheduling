using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;
using static SpaceOS.Modules.Scheduling.Domain.Tests.Solving.SolverScenarios;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Reconciling lags that run on the clock rather than on the shift plan.
/// </summary>
/// <remarks>
/// Business owner decision (2026-07-29): a curing or drying time is real elapsed time — "48
/// hours, weekend included" — while an organisational lag waits for the next shift. These
/// cases are the difference between the two, measured in days of plan.
/// </remarks>
public sealed class CalendarAwareSchedulerTests
{
    private const string Zone = "Europe/Budapest";

    /// <summary>Friday 2026-08-07, midnight local.</summary>
    private static readonly Instant Origin = LocalInstant(2026, 8, 7, 0);

    private static Instant LocalInstant(int year, int month, int day, int hour) =>
        new LocalDateTime(year, month, day, hour, 0)
            .InZoneLeniently(DateTimeZoneProviders.Tzdb[Zone])
            .ToInstant();

    /// <summary>Monday–Friday, 08:00–16:00 local.</summary>
    private static WorkingCalendar WeekdayCalendar() =>
        new(Zone, [.. new[]
        {
            IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday,
            IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday,
        }.Select(day => new ShiftDefinition
        {
            Weekday = day,
            Span = new LocalTimeRange(new LocalTime(8, 0), new LocalTime(16, 0)),
        })]);

    private static Dictionary<string, WorkingTimeline> Timelines() =>
        new(StringComparer.Ordinal)
        {
            ["r1"] = new WorkingTimeline(WeekdayCalendar(), Origin, Duration.FromDays(120)),
            ["r2"] = new WorkingTimeline(WeekdayCalendar(), Origin, Duration.FromDays(120)),
        };

    private static CalendarAwareSchedule Run(SchedulingRequest request, int maximumIterations = 5) =>
        new CalendarAwareScheduler(new DeterministicListSolver(), maximumIterations)
            .Run(request, Timelines());

    /// <summary>A Friday day-shift job, then a 48-hour physical process on another resource.</summary>
    private static SchedulingRequest CuringRequest(LagKind lagKind) => Request(
        [Operation("a", duration: 480m), Operation("b", duration: 60m, resource: "r2")],
        [Edge("a", "b", lag: 2880m, lagKind: lagKind)]);

    /// <summary>A short Friday-morning job, then 24 hours of elapsed lag that overlaps the shift.</summary>
    private static SchedulingRequest OverlappingLagRequest() => Request(
        [Operation("a", duration: 120m), Operation("b", duration: 60m, resource: "r2")],
        [Edge("a", "b", lag: 1440m, lagKind: LagKind.ElapsedTime)]);

    private static Instant StartOf(CalendarAwareSchedule schedule, string operationId) =>
        schedule.Dates.Operations
            .Single(operation => string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
            .StartUtc;

    [Fact]
    public void A_curing_time_runs_through_the_weekend()
    {
        // 'a' finishes Friday 16:00; 48 hours of curing are done Sunday 16:00, so the work can
        // resume when the shop next opens — Monday 08:00. The weekend counts, because the
        // material does not know it is the weekend.
        var schedule = Run(CuringRequest(LagKind.ElapsedTime));

        Assert.Equal(LocalInstant(2026, 8, 10, 8), StartOf(schedule, "b"));
        Assert.Empty(schedule.Diagnostics);
    }

    [Fact]
    public void The_same_number_read_as_working_time_costs_eight_working_days()
    {
        // The contrast that justifies the field. 2880 WORKING minutes on an 8-hour shift is
        // six working days: counted from Friday 16:00 they run out on Monday the 17th at
        // 16:00, so the successor starts Tuesday the 18th — eight days later than the physical
        // process actually needs.
        var elapsed = Run(CuringRequest(LagKind.ElapsedTime));
        var working = Run(CuringRequest(LagKind.WorkingTime));

        Assert.Equal(LocalInstant(2026, 8, 10, 8), StartOf(elapsed, "b"));
        Assert.Equal(LocalInstant(2026, 8, 18, 8), StartOf(working, "b"));
    }

    [Fact]
    public void The_reconciliation_settles_in_a_couple_of_passes()
    {
        // A lag that overlaps working time is the case that actually needs feedback: 'a' ends
        // Friday 10:00, and the 24 hours are up Saturday 10:00 — but six of those hours are
        // working hours, so the first pass (which assumed none) has to be corrected once.
        //
        // The requirement only ever moves later, so this converges rather than oscillating.
        var schedule = Run(OverlappingLagRequest());

        Assert.Equal(2, schedule.Iterations);
        Assert.Empty(schedule.Diagnostics);
        Assert.Equal(LocalInstant(2026, 8, 10, 8), StartOf(schedule, "b"));
    }

    [Fact]
    public void A_plan_without_elapsed_lags_is_solved_in_a_single_pass()
    {
        // Nothing to reconcile: the existing behaviour must not pay for the new field.
        var schedule = Run(Request(
            [Operation("a"), Operation("b", resource: "r2")],
            [Edge("a", "b", lag: 30m)]));

        Assert.Equal(1, schedule.Iterations);
        Assert.Empty(schedule.Diagnostics);
    }

    [Fact]
    public void An_elapsed_lag_that_could_not_settle_is_reported_not_hidden()
    {
        // Forced by capping the passes at one, on the case where the first pass IS wrong: the
        // lag overlaps working hours, so the zero-minute assumption releases 'b' six working
        // hours too early. Saying so is the whole point — a curing time that silently did not
        // apply is found when the material is already ruined.
        var schedule = Run(OverlappingLagRequest(), maximumIterations: 1);

        var diagnostic = Assert.Single(schedule.Diagnostics);
        Assert.Equal(MaterialisationCode.ElapsedLagNotSettled, diagnostic.Code);
        Assert.Equal("a", diagnostic.PredecessorOperationId);
        Assert.Equal("b", diagnostic.SuccessorOperationId);
    }

    [Fact]
    public void A_start_to_start_elapsed_lag_measures_from_the_predecessor_start()
    {
        // The relation decides WHICH instant the clock starts from; the lag kind decides how
        // the delay is counted. 'a' starts Friday 08:00, so 24 hours are up Saturday 08:00 and
        // 'b' takes the next opening, Monday 08:00.
        var request = Request(
            [Operation("a", duration: 480m), Operation("b", duration: 60m, resource: "r2")],
            [Edge("a", "b", DependencyType.StartToStart, lag: 1440m, lagKind: LagKind.ElapsedTime)]);

        Assert.Equal(LocalInstant(2026, 8, 10, 8), StartOf(Run(request), "b"));
    }
}
