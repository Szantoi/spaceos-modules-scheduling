using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;
using static SpaceOS.Modules.Scheduling.Domain.Tests.Solving.SolverScenarios;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Turning a working-minute plan into dates — and admitting when the dates no longer satisfy
/// the network the plan came from.
/// </summary>
public sealed class ScheduleMaterialiserTests
{
    private const string Zone = "Europe/Budapest";

    /// <summary>Monday 2026-08-03, midnight local — axis origin for every case here.</summary>
    private static readonly Instant Origin = LocalInstant(2026, 8, 3, 0);

    private static Instant LocalInstant(int year, int month, int day, int hour) =>
        new LocalDateTime(year, month, day, hour, 0)
            .InZoneLeniently(DateTimeZoneProviders.Tzdb[Zone])
            .ToInstant();

    private static WorkingCalendar Calendar(IEnumerable<IsoDayOfWeek> days, int fromHour = 8, int toHour = 16) =>
        new(Zone, [.. days.Select(day => new ShiftDefinition
        {
            Weekday = day,
            Span = new LocalTimeRange(new LocalTime(fromHour, 0), new LocalTime(toHour, 0)),
        })]);

    private static readonly IsoDayOfWeek[] Weekdays =
    [
        IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday,
        IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday,
    ];

    private static readonly IsoDayOfWeek[] EveryDay =
    [
        IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Thursday,
        IsoDayOfWeek.Friday, IsoDayOfWeek.Saturday, IsoDayOfWeek.Sunday,
    ];

    private static Dictionary<string, WorkingTimeline> Timelines(
        params (string Resource, WorkingCalendar Calendar)[] calendars) =>
        calendars.ToDictionary(
            entry => entry.Resource,
            entry => new WorkingTimeline(entry.Calendar, Origin, Duration.FromDays(60)),
            StringComparer.Ordinal);

    private static MaterialisedOperation Dated(MaterialisedSchedule schedule, string operationId) =>
        schedule.Operations.Single(operation => string.Equals(
            operation.OperationId, operationId, StringComparison.Ordinal));

    [Fact]
    public void A_long_operation_spans_the_night_and_resumes_next_morning()
    {
        // Ten working hours in an eight-hour shift. Business owner decision (2026-07-29): the
        // work spans non-working time rather than being refused or crammed into one day.
        var request = Request([Operation("a", duration: 600m)]);
        var solution = new DeterministicListSolver().Solve(request);

        var schedule = ScheduleMaterialiser.Materialise(
            request, solution, Timelines(("r1", Calendar(Weekdays))));

        var dated = Dated(schedule, "a");
        Assert.Equal(LocalInstant(2026, 8, 3, 8), dated.StartUtc);
        Assert.Equal(LocalInstant(2026, 8, 4, 10), dated.FinishUtc);
        Assert.Empty(schedule.Diagnostics);
    }

    [Fact]
    public void A_milestone_keeps_its_instant_instead_of_moving_to_the_next_window()
    {
        var request = Request([Operation("a"), Operation("m", duration: 0m)]);
        var solution = new DeterministicListSolver().Solve(request);

        var schedule = ScheduleMaterialiser.Materialise(
            request, solution, Timelines(("r1", Calendar(Weekdays))));

        var milestone = Dated(schedule, "m");
        Assert.Equal(milestone.StartUtc, milestone.FinishUtc);
    }

    [Fact]
    public void A_dependency_on_one_shared_calendar_survives_the_projection()
    {
        // Same calendar on both resources: the working-minute order and the real-time order
        // are the same order, so nothing can break.
        var request = Request([Operation("a"), Operation("b", resource: "r2")], [Edge("a", "b")]);
        var solution = new DeterministicListSolver().Solve(request);

        var schedule = ScheduleMaterialiser.Materialise(
            request,
            solution,
            Timelines(("r1", Calendar(Weekdays)), ("r2", Calendar(Weekdays))));

        Assert.Empty(schedule.Diagnostics);
        Assert.True(Dated(schedule, "b").StartUtc >= Dated(schedule, "a").FinishUtc);
    }

    [Fact]
    public void A_dependency_broken_by_differing_calendars_is_reported_not_hidden()
    {
        // The trap this check exists for. 'a' runs on a Monday-only resource, 'b' on one that
        // works around the clock. The solver put 'b' after 'a' on the WORKING-minute axis —
        // correctly, by its own measure — but 480 working minutes mean Monday 16:00 on one
        // calendar and Monday 08:00 on the other. In real time 'b' would start while 'a' is
        // still running.
        //
        // Silently moving 'b' would hide that the plan no longer satisfies its own network;
        // the planner has to see it, because the fix is a calendar decision, not a solver one.
        var request = Request(
            [Operation("a", duration: 480m), Operation("b", duration: 60m, resource: "r2")],
            [Edge("a", "b")]);
        var solution = new DeterministicListSolver().Solve(request);

        var schedule = ScheduleMaterialiser.Materialise(
            request,
            solution,
            Timelines(
                ("r1", Calendar([IsoDayOfWeek.Monday])),
                // 00:00-23:00 every day: a local time of 24:00 does not exist, and the hour
                // left out changes nothing about what this case proves.
                ("r2", Calendar(EveryDay, fromHour: 0, toHour: 23))));

        var diagnostic = Assert.Single(schedule.Diagnostics);
        Assert.Equal(MaterialisationCode.PrecedenceBrokenAcrossCalendars, diagnostic.Code);
        Assert.Equal("a", diagnostic.PredecessorOperationId);
        Assert.Equal("b", diagnostic.SuccessorOperationId);
        Assert.True(Dated(schedule, "b").StartUtc < Dated(schedule, "a").FinishUtc);
    }

    [Fact]
    public void A_fixed_start_is_not_reported_again_by_the_projection()
    {
        // The solver already said the planner overruled the network. Repeating it here would
        // turn one decision into two warnings about the same thing.
        var request = Request(
            [Operation("a"), Operation("b", resource: "r2", fixedStart: 0m)],
            [Edge("a", "b")]);
        var solution = new DeterministicListSolver().Solve(request);

        var schedule = ScheduleMaterialiser.Materialise(
            request,
            solution,
            Timelines(("r1", Calendar(Weekdays)), ("r2", Calendar(Weekdays))));

        Assert.Empty(schedule.Diagnostics);
    }

    [Fact]
    public void A_resource_without_a_calendar_is_refused_rather_than_dated_by_guesswork()
    {
        var request = Request([Operation("a"), Operation("b", resource: "r2")]);
        var solution = new DeterministicListSolver().Solve(request);

        var error = Assert.Throws<ArgumentException>(() => ScheduleMaterialiser.Materialise(
            request, solution, Timelines(("r1", Calendar(Weekdays)))));

        Assert.Contains("r2", error.Message, StringComparison.Ordinal);
    }
}
