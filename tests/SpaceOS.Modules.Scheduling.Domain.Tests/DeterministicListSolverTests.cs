using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The reference scheduling strategy (ADR-069 §5) and — above all — its DETERMINISM
/// (ADR-070 D3).
/// </summary>
/// <remarks>
/// The determinism cases are the gate the CP-SAT adapter will have to pass too: the revision
/// hash is quoted back by Doorstar, so the same input must produce the same plan, whatever
/// order the caller happened to hand the work over in.
/// </remarks>
public sealed class DeterministicListSolverTests
{
    private static readonly ProjectRef TestProject = ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb"));
    private static readonly KernelWorkScope TestScope = KernelWorkScope.Create(
        TestProject,
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

    private static SolverOperation Operation(
        string id,
        decimal duration = 60m,
        string resource = "r1",
        decimal? fixedStart = null,
        bool eligible = true) => new()
    {
        OperationId = id,
        Scope = TestScope,
        ResourceKey = resource,
        DurationMinutes = duration,
        FixedStartMinute = fixedStart,
        EligibleForAutomaticPlanning = eligible,
    };

    private static SolverDependency Edge(
        string predecessor,
        string successor,
        DependencyType relation = DependencyType.FinishToStart,
        decimal lag = 0m,
        decimal? release = null) => new()
    {
        PredecessorOperationId = predecessor,
        SuccessorOperationId = successor,
        Relation = relation,
        LagMinutes = lag,
        ReleaseThresholdFraction = release,
    };

    private static SchedulingRequest Request(
        IReadOnlyList<SolverOperation> operations,
        IReadOnlyList<SolverDependency>? dependencies = null,
        decimal capacity = 1m) => new()
    {
        Operations = operations,
        Dependencies = dependencies ?? [],
        Resources = [.. operations
            .Select(operation => operation.ResourceKey)
            .Distinct(StringComparer.Ordinal)
            .Select(key => new SolverResource(key, capacity, 1))],
    };

    private static SchedulingSolution Solve(SchedulingRequest request) =>
        new DeterministicListSolver().Solve(request);

    private static OperationPlan Placed(SchedulingSolution solution, string operationId) =>
        solution.Operations.Single(operation => string.Equals(
            operation.OperationId, operationId, StringComparison.Ordinal));

    [Fact]
    public void Independent_operations_on_one_resource_are_queued_not_overlapped()
    {
        var solution = Solve(Request([Operation("a"), Operation("b")]));

        Assert.Equal(0m, Placed(solution, "a").StartMinute);
        Assert.Equal(60m, Placed(solution, "b").StartMinute);
    }

    [Fact]
    public void Capacity_two_lets_two_operations_run_at_once()
    {
        var solution = Solve(Request([Operation("a"), Operation("b")], capacity: 2m));

        Assert.Equal(0m, Placed(solution, "a").StartMinute);
        Assert.Equal(0m, Placed(solution, "b").StartMinute);
    }

    [Fact]
    public void The_third_operation_waits_for_the_first_free_slot()
    {
        // Capacity 2, three 60-minute jobs: the third starts when the earliest one finishes.
        var solution = Solve(Request([Operation("a"), Operation("b"), Operation("c")], capacity: 2m));

        Assert.Equal(60m, Placed(solution, "c").StartMinute);
    }

    [Fact]
    public void Different_resources_do_not_compete()
    {
        var solution = Solve(Request([Operation("a"), Operation("b", resource: "r2")]));

        Assert.Equal(0m, Placed(solution, "a").StartMinute);
        Assert.Equal(0m, Placed(solution, "b").StartMinute);
    }

    [Fact]
    public void Finish_to_start_with_lag_is_honoured()
    {
        var solution = Solve(Request(
            [Operation("a"), Operation("b", resource: "r2")],
            [Edge("a", "b", lag: 30m)]));

        Assert.Equal(90m, Placed(solution, "b").StartMinute);
    }

    [Fact]
    public void Start_to_start_lets_the_successor_begin_before_the_predecessor_finishes()
    {
        var solution = Solve(Request(
            [Operation("a"), Operation("b", resource: "r2")],
            [Edge("a", "b", DependencyType.StartToStart, lag: 15m)]));

        Assert.Equal(15m, Placed(solution, "b").StartMinute);
    }

    [Fact]
    public void A_partial_release_starts_the_successor_mid_predecessor()
    {
        // Half of a 60-minute predecessor: the successor may start at minute 30.
        var solution = Solve(Request(
            [Operation("a"), Operation("b", resource: "r2")],
            [Edge("a", "b", release: 0.5m)]));

        Assert.Equal(30m, Placed(solution, "b").StartMinute);
    }

    [Fact]
    public void A_late_partial_release_overrides_the_dependency_and_is_reported()
    {
        // The FINAL contract rule: the release WINS even when it lands LATER than the
        // precedence bound, and the plan says so rather than silently moving the date.
        //
        // Start-to-start, not finish-to-start: under FS the bound is the predecessor's
        // FINISH, so a release fraction can never be later than it — the delaying case only
        // exists where the relation would have allowed an earlier start.
        var solution = Solve(Request(
            [Operation("a", duration: 100m), Operation("b", resource: "r2")],
            [Edge("a", "b", DependencyType.StartToStart, release: 0.9m)]));

        Assert.Equal(90m, Placed(solution, "b").StartMinute);
        Assert.Contains(
            solution.Diagnostics,
            diagnostic => diagnostic.Code == SchedulingDiagnosticCode.PartialReleaseDelaysStart);
        Assert.Contains(
            DependencyWarning.PartialReleaseDelaysStart,
            solution.Dependencies.Single().Warnings);
    }

    [Fact]
    public void A_fixed_start_overrides_precedence_and_is_reported()
    {
        // The planner overruled the network. That is allowed — and must be visible, because
        // the resulting plan no longer satisfies the dependency it was built from.
        var solution = Solve(Request(
            [Operation("a"), Operation("b", resource: "r2", fixedStart: 10m)],
            [Edge("a", "b")]));

        Assert.Equal(10m, Placed(solution, "b").StartMinute);
        Assert.Contains(
            solution.Diagnostics,
            diagnostic => diagnostic.Code == SchedulingDiagnosticCode.FixedStartOverridesPrecedence);
    }

    [Fact]
    public void An_ineligible_operation_is_still_placed_but_flagged()
    {
        // The work happens whether or not its norm was complete; pretending otherwise would
        // hand the planner a schedule that ignores real occupancy.
        var solution = Solve(Request([Operation("a", eligible: false), Operation("b")]));

        Assert.False(Placed(solution, "a").AutomaticallyPlanned);
        Assert.Equal(60m, Placed(solution, "b").StartMinute);
        Assert.Contains(
            solution.Diagnostics,
            diagnostic => diagnostic.Code == SchedulingDiagnosticCode.PlacedDespiteIncompleteStandard);
    }

    [Fact]
    public void A_milestone_never_waits_for_capacity()
    {
        var solution = Solve(Request([Operation("a"), Operation("m", duration: 0m)]));

        Assert.Equal(0m, Placed(solution, "m").StartMinute);
        Assert.Equal(0m, Placed(solution, "m").FinishMinute);
    }

    [Fact]
    public void A_cycle_is_refused_rather_than_partially_scheduled()
    {
        var request = Request(
            [Operation("a"), Operation("b")],
            [Edge("a", "b"), Edge("b", "a")]);

        Assert.Throws<ArgumentException>(() => Solve(request));
    }

    [Fact]
    public void An_operation_on_an_unknown_resource_is_refused()
    {
        var request = new SchedulingRequest
        {
            Operations = [Operation("a", resource: "ghost")],
            Resources = [new SolverResource("r1", 1m, 1)],
        };

        Assert.Throws<ArgumentException>(() => Solve(request));
    }

    [Fact]
    public void Zero_capacity_is_refused_instead_of_searching_forever()
    {
        var request = new SchedulingRequest
        {
            Operations = [Operation("a")],
            Resources = [new SolverResource("r1", 0m, 1)],
        };

        Assert.Throws<ArgumentException>(() => Solve(request));
    }

    [Fact]
    public void The_same_input_twice_produces_the_same_revision_hash()
    {
        // ADR-070 D3, the gate itself: Doorstar quotes the hash back, so an unchanged input
        // must never look like a changed plan.
        var request = Request(
            [Operation("a"), Operation("b"), Operation("c", resource: "r2")],
            [Edge("a", "b"), Edge("a", "c", release: 0.5m)]);

        Assert.Equal(HashOf(Solve(request)), HashOf(Solve(request)));
    }

    [Fact]
    public void The_order_the_work_arrives_in_does_not_change_the_plan()
    {
        // The caller's enumeration order carries no meaning. If it moved the hash, every
        // re-import of the same work would look like a new plan.
        var operations = new[] { Operation("a"), Operation("b"), Operation("c", resource: "r2") };
        var edges = new[] { Edge("a", "b"), Edge("a", "c", release: 0.5m) };

        var forward = Solve(Request(operations, edges));
        var reversed = Solve(Request([.. operations.Reverse()], [.. edges.Reverse()]));

        Assert.Equal(HashOf(forward), HashOf(reversed));
    }

    [Fact]
    public void The_reference_strategy_reports_itself_as_reproducible()
    {
        // It has no search to be non-deterministic about. The flag exists for the opt-in
        // parallel CP-SAT search, which must NOT claim a stable identity.
        Assert.True(Solve(Request([Operation("a")])).IsReproducible);
    }

    /// <summary>Builds the revision the solution would become, and returns its hash.</summary>
    private static string HashOf(SchedulingSolution solution)
    {
        var run = ScheduleRun.Open(Guid.NewGuid(), Guid.NewGuid(), TestProject, Now);
        return run.AddProposal(
            Guid.NewGuid(), solution.Operations, solution.CalendarRevisions, Now, solution.Dependencies)
            .ContentHash;
    }
}
