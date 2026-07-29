using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The shadow diff: what a planner is shown before deciding to publish (PLAN-03 M4).
/// </summary>
public sealed class ScheduleRevisionComparerTests
{
    private static readonly ProjectRef Project = ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb"));
    private static readonly KernelWorkScope Scope = KernelWorkScope.Create(
        Project,
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    private static OperationPlan Plan(
        string id,
        decimal start,
        decimal finish,
        string resource = "r1") => new()
        {
            OperationId = id,
            Scope = Scope,
            ResourceKey = resource,
            StartMinute = start,
            FinishMinute = finish,
            AutomaticallyPlanned = true,
        };

    private static ScheduleRevision Revision(
        IReadOnlyList<OperationPlan> operations,
        IReadOnlyDictionary<string, int>? calendars = null,
        IReadOnlyList<PlannedDependency>? dependencies = null)
    {
        var run = ScheduleRun.Open(Guid.NewGuid(), Guid.NewGuid(), Project, Now);
        return run.AddProposal(
            Guid.NewGuid(),
            operations,
            calendars ?? new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1 },
            Now,
            dependencies);
    }

    [Fact]
    public void An_identical_plan_shows_no_difference()
    {
        var operations = new[] { Plan("a", 0m, 60m), Plan("b", 60m, 120m) };

        var diff = ScheduleRevisionComparer.Compare(Revision(operations), Revision(operations));

        Assert.True(diff.IsEmpty);
        Assert.Equal(2, diff.UnchangedOperationCount);
        Assert.Equal(0m, diff.MakespanMinuteDelta);
    }

    [Fact]
    public void A_moved_operation_is_reported_with_how_far_it_moved()
    {
        var current = Revision([Plan("a", 0m, 60m), Plan("b", 60m, 120m)]);
        var candidate = Revision([Plan("a", 0m, 60m), Plan("b", 90m, 150m)]);

        var diff = ScheduleRevisionComparer.Compare(current, candidate);

        var shift = Assert.Single(diff.ShiftedOperations);
        Assert.Equal("b", shift.OperationId);
        Assert.Equal(30m, shift.StartMinuteDelta);
        Assert.Equal(30m, shift.FinishMinuteDelta);
        Assert.False(shift.MovedResource);
        Assert.Equal(1, diff.UnchangedOperationCount);
    }

    [Fact]
    public void An_operation_that_changed_resource_says_where_it_went()
    {
        // Timing alone would hide this: the same minutes on another machine is a different
        // instruction for the shop floor.
        //
        // Each revision pins only the calendars of the resources it actually uses — the domain
        // refuses a plan that schedules a resource without pinning its calendar, because such a
        // revision could not be reproduced.
        var current = Revision(
            [Plan("a", 0m, 60m, "r1")],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1 });
        var candidate = Revision(
            [Plan("a", 0m, 60m, "r2")],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r2"] = 1 });

        var shift = Assert.Single(ScheduleRevisionComparer.Compare(current, candidate).ShiftedOperations);

        Assert.True(shift.MovedResource);
        Assert.Equal("r1", shift.FromResourceKey);
        Assert.Equal("r2", shift.ToResourceKey);
        Assert.Equal(0m, shift.StartMinuteDelta);
    }

    [Fact]
    public void Added_and_removed_operations_are_listed_separately()
    {
        var current = Revision([Plan("a", 0m, 60m), Plan("gone", 60m, 90m)]);
        var candidate = Revision([Plan("a", 0m, 60m), Plan("fresh", 60m, 90m)]);

        var diff = ScheduleRevisionComparer.Compare(current, candidate);

        Assert.Equal(["fresh"], diff.AddedOperationIds);
        Assert.Equal(["gone"], diff.RemovedOperationIds);
    }

    [Fact]
    public void A_shorter_plan_reports_a_negative_makespan_delta()
    {
        // The number a planner looks at first: did this get better or worse?
        var current = Revision([Plan("a", 0m, 60m), Plan("b", 60m, 160m)]);
        var candidate = Revision([Plan("a", 0m, 60m), Plan("b", 60m, 110m)]);

        Assert.Equal(-50m, ScheduleRevisionComparer.Compare(current, candidate).MakespanMinuteDelta);
    }

    [Fact]
    public void A_changed_calendar_is_a_difference_even_when_every_minute_is_identical()
    {
        // The subtle one, and the reason this check exists at all. The plan is stored in
        // WORKING minutes; if the calendar underneath changes, the very same minute lands on a
        // different date. Reporting "nothing changed" would tell the planner they had a quiet
        // week while every promised date moved.
        var operations = new[] { Plan("a", 0m, 60m) };

        var current = Revision(operations, new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1 });
        var candidate = Revision(operations, new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 2 });

        var diff = ScheduleRevisionComparer.Compare(current, candidate);

        Assert.False(diff.IsEmpty, "a naptár-revízió változása önmagában is különbség");
        Assert.Equal(["r1"], diff.ResourcesWithChangedCalendar);
        Assert.Empty(diff.ShiftedOperations);
        Assert.Equal(1, diff.UnchangedOperationCount);
    }

    [Fact]
    public void A_resource_that_leaves_the_plan_counts_as_a_calendar_change()
    {
        var current = Revision(
            [Plan("a", 0m, 60m), Plan("b", 0m, 60m, "r2")],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1, ["r2"] = 1 });
        var candidate = Revision(
            [Plan("a", 0m, 60m)],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1 });

        Assert.Equal(["r2"], ScheduleRevisionComparer.Compare(current, candidate).ResourcesWithChangedCalendar);
    }

    [Fact]
    public void A_rebound_dependency_is_reported_even_when_the_dates_agree()
    {
        // The edge now binds through a partial release rather than the relation itself. The
        // minutes coincide today; the promise is different, and a later change to the
        // predecessor would move the successor differently.
        var operations = new[] { Plan("a", 0m, 60m), Plan("b", 60m, 120m) };

        var current = Revision(operations, dependencies: [Edge(BoundSource.Dependency)]);
        var candidate = Revision(operations, dependencies: [Edge(BoundSource.PartialRelease)]);

        var diff = ScheduleRevisionComparer.Compare(current, candidate);

        Assert.True(diff.DependencyNetworkChanged);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void The_output_order_does_not_depend_on_the_input_order()
    {
        // The diff is read by a human and compared between runs; enumeration order must not
        // reach it.
        var current = Revision([Plan("a", 0m, 60m), Plan("b", 0m, 60m), Plan("c", 0m, 60m)]);
        var forward = Revision([Plan("a", 10m, 70m), Plan("b", 10m, 70m), Plan("c", 10m, 70m)]);
        var reversed = Revision([Plan("c", 10m, 70m), Plan("b", 10m, 70m), Plan("a", 10m, 70m)]);

        var first = ScheduleRevisionComparer.Compare(current, forward);
        var second = ScheduleRevisionComparer.Compare(current, reversed);

        Assert.Equal(
            first.ShiftedOperations.Select(shift => shift.OperationId),
            second.ShiftedOperations.Select(shift => shift.OperationId));
    }

    private static PlannedDependency Edge(BoundSource source) => new()
    {
        PredecessorOperationId = "a",
        SuccessorOperationId = "b",
        Relation = DependencyType.FinishToStart,
        LagMinutes = 0m,
        EarliestStartMinute = 60m,
        StartSource = source,
        Warnings = [],
    };
}
