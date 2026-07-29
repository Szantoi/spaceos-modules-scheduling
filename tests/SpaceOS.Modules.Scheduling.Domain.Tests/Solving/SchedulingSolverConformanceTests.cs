using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using Xunit;
using static SpaceOS.Modules.Scheduling.Domain.Tests.Solving.SolverScenarios;

namespace SpaceOS.Modules.Scheduling.Domain.Tests.Solving;

/// <summary>
/// What EVERY <see cref="ISchedulingSolver"/> must satisfy, whatever strategy it uses.
/// </summary>
/// <remarks>
/// <para>
/// This is the point of the port (ADR-070 D1): the reference list scheduler and the CP-SAT
/// optimiser are measured on the same cases. The two will NOT produce the same plan — an
/// optimiser is allowed to find a shorter one, and that freedom is why it exists. So the
/// assertions here are INVARIANTS, not expected start minutes: precedence holds, capacity is
/// never exceeded, an overridden start is reported, and the same input yields the same hash.
/// A concrete strategy's own numbers stay in its own test class.
/// </para>
/// <para>
/// Precedence is checked through <see cref="DependencyBoundResolver"/> rather than by
/// restating the rules here. The resolver is the single definition of what an edge means
/// (ADR-069 §4); a second copy in the test would eventually disagree with it, and the test
/// would then be enforcing a rule the product does not have.
/// </para>
/// </remarks>
public abstract class SchedulingSolverConformanceTests
{
    /// <summary>The strategy under test.</summary>
    protected abstract ISchedulingSolver CreateSolver();

    private SchedulingSolution Solve(SchedulingRequest request) => CreateSolver().Solve(request);

    [Theory]
    [InlineData(DependencyType.FinishToStart, 0)]
    [InlineData(DependencyType.FinishToStart, 30)]
    [InlineData(DependencyType.StartToStart, 0)]
    [InlineData(DependencyType.StartToStart, 15)]
    [InlineData(DependencyType.FinishToFinish, 0)]
    [InlineData(DependencyType.FinishToFinish, 20)]
    [InlineData(DependencyType.StartToFinish, 0)]
    [InlineData(DependencyType.StartToFinish, 45)]
    public void Every_relation_is_satisfied_in_the_produced_plan(DependencyType relation, int lag)
    {
        // Separate resources, so capacity cannot be the reason a bound happens to hold.
        var request = Request(
            [Operation("a"), Operation("b", resource: "r2")],
            [Edge("a", "b", relation, lag: lag)]);

        AssertSolutionIsSound(request, Solve(request));
    }

    [Fact]
    public void A_partial_release_bound_is_satisfied()
    {
        var request = Request(
            [Operation("a"), Operation("b", resource: "r2")],
            [Edge("a", "b", release: 0.5m)]);

        AssertSolutionIsSound(request, Solve(request));
    }

    [Fact]
    public void A_late_partial_release_is_reported_rather_than_silently_applied()
    {
        // The settled rule: the release wins even when later than the precedence bound
        // (start-to-start, because under FS the bound is the finish and a fraction can never
        // exceed it). Whichever strategy runs, the planner must see WHY the work sits idle.
        var request = Request(
            [Operation("a", duration: 100m), Operation("b", resource: "r2")],
            [Edge("a", "b", DependencyType.StartToStart, release: 0.9m)]);

        var solution = Solve(request);

        AssertSolutionIsSound(request, solution);
        Assert.Contains(
            solution.Diagnostics,
            diagnostic => diagnostic.Code == SchedulingDiagnosticCode.PartialReleaseDelaysStart);
        Assert.Contains(
            DependencyWarning.PartialReleaseDelaysStart,
            solution.Dependencies.Single().Warnings);
    }

    [Fact]
    public void A_fixed_start_is_honoured_exactly_and_reported()
    {
        // The planner overruled the network: the plan no longer satisfies the edge it was
        // built from, so silence here would be a lie of omission.
        var request = Request(
            [Operation("a"), Operation("b", resource: "r2", fixedStart: 10m)],
            [Edge("a", "b")]);

        var solution = Solve(request);

        AssertSolutionIsSound(request, solution);
        Assert.Equal(10m, Placed(solution, "b").StartMinute);
        Assert.Contains(
            solution.Diagnostics,
            diagnostic => diagnostic.Code == SchedulingDiagnosticCode.FixedStartOverridesPrecedence);
    }

    [Fact]
    public void An_ineligible_operation_is_placed_and_flagged()
    {
        // The work happens whether or not its norm was complete; leaving it out would make
        // the plan understate real occupancy.
        var request = Request([Operation("a", eligible: false), Operation("b")]);

        var solution = Solve(request);

        AssertSolutionIsSound(request, solution);
        Assert.False(Placed(solution, "a").AutomaticallyPlanned);
        Assert.Contains(
            solution.Diagnostics,
            diagnostic => diagnostic.Code == SchedulingDiagnosticCode.PlacedDespiteIncompleteStandard);
    }

    [Fact]
    public void A_milestone_consumes_no_capacity()
    {
        var request = Request([Operation("a"), Operation("m", duration: 0m)]);

        var solution = Solve(request);

        AssertSolutionIsSound(request, solution);
        Assert.Equal(0m, Placed(solution, "m").StartMinute);
    }

    [Fact]
    public void Capacity_is_respected_under_contention()
    {
        // Eight operations, two resources, mixed durations and a precedence chain: the shape
        // where a greedy placement and an optimiser genuinely differ, so the invariants —
        // not the minutes — are what both must satisfy.
        var request = Request(
            [
                Operation("a", duration: 60m),
                Operation("b", duration: 30m),
                Operation("c", duration: 45m),
                Operation("d", duration: 90m),
                Operation("e", duration: 15m, resource: "r2"),
                Operation("f", duration: 75m, resource: "r2"),
                Operation("g", duration: 20m, resource: "r2"),
                Operation("h", duration: 50m, resource: "r2"),
            ],
            [
                Edge("a", "d"),
                Edge("b", "c", DependencyType.StartToStart, lag: 10m),
                Edge("e", "h", lag: 5m),
                Edge("f", "g", release: 0.25m),
            ],
            capacity: 2m);

        AssertSolutionIsSound(request, Solve(request));
    }

    [Fact]
    public void A_cycle_is_refused_rather_than_partially_scheduled()
    {
        var request = Request([Operation("a"), Operation("b")], [Edge("a", "b"), Edge("b", "a")]);

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
    public void Fixed_starts_that_exceed_capacity_are_refused_by_every_strategy()
    {
        // Business owner decision (2026-07-29): two pins on the same minute of a
        // single-capacity resource is a contradiction in the REQUEST, so it is refused before
        // any search runs. Previously the two strategies disagreed — the optimiser proved it
        // unsatisfiable, the reference placed both and exceeded the capacity — which made the
        // answer depend on configuration. This case exists to keep them from drifting apart
        // again: neither may get far enough to have an opinion.
        var request = Request([Operation("a", fixedStart: 0m), Operation("b", fixedStart: 0m)]);

        Assert.Throws<ArgumentException>(() => Solve(request));
    }

    [Fact]
    public void Non_positive_capacity_is_refused_instead_of_searching_forever()
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
        // ADR-070 D3. Doorstar quotes the hash back: an unchanged input that looked changed
        // would start an approval round over nothing.
        var request = ContentionScenario();

        Assert.Equal(HashOf(Solve(request)), HashOf(Solve(request)));
    }

    [Fact]
    public void The_order_the_work_arrives_in_does_not_change_the_plan()
    {
        // The caller's enumeration order carries no meaning, so it must not reach the hash.
        var forward = ContentionScenario();
        var reversed = new SchedulingRequest
        {
            Operations = [.. forward.Operations.Reverse()],
            Dependencies = [.. forward.Dependencies.Reverse()],
            Resources = [.. forward.Resources.Reverse()],
        };

        Assert.Equal(HashOf(Solve(forward)), HashOf(Solve(reversed)));
    }

    private static SchedulingRequest ContentionScenario() => Request(
        [
            Operation("a"),
            Operation("b"),
            Operation("c", resource: "r2"),
            Operation("d", duration: 30m, resource: "r2"),
        ],
        [Edge("a", "b"), Edge("a", "c", release: 0.5m), Edge("c", "d", DependencyType.StartToStart, lag: 10m)],
        capacity: 2m);

    /// <summary>Every invariant a plan must satisfy, whoever produced it.</summary>
    private static void AssertSolutionIsSound(SchedulingRequest request, SchedulingSolution solution)
    {
        AssertEveryOperationIsPlacedOnce(request, solution);
        AssertPrecedenceIsSatisfied(request, solution);
        AssertCapacityIsRespected(request, solution);
    }

    private static void AssertEveryOperationIsPlacedOnce(
        SchedulingRequest request,
        SchedulingSolution solution)
    {
        Assert.Equal(request.Operations.Count, solution.Operations.Count);

        foreach (var operation in request.Operations)
        {
            var plan = Placed(solution, operation.OperationId);

            Assert.True(plan.StartMinute >= 0m, $"'{operation.OperationId}' starts before minute zero.");
            Assert.Equal(plan.StartMinute + operation.DurationMinutes, plan.FinishMinute);
            Assert.Equal(operation.ResourceKey, plan.ResourceKey);
        }
    }

    private static void AssertPrecedenceIsSatisfied(
        SchedulingRequest request,
        SchedulingSolution solution)
    {
        foreach (var dependency in request.Dependencies)
        {
            var successor = request.Operations.Single(operation => string.Equals(
                operation.OperationId, dependency.SuccessorOperationId, StringComparison.Ordinal));

            // A fixed start deliberately overrides the network — asserting the bound here
            // would contradict the rule the product just applied.
            if (successor.FixedStartMinute.HasValue)
            {
                continue;
            }

            var predecessorPlan = Placed(solution, dependency.PredecessorOperationId);
            var successorPlan = Placed(solution, dependency.SuccessorOperationId);

            var bounds = DependencyBoundResolver.Resolve(new DependencyBoundInput
            {
                Type = dependency.Relation,
                PredecessorStartMinute = predecessorPlan.StartMinute,
                PredecessorFinishMinute = predecessorPlan.FinishMinute,
                LagMinutes = dependency.LagMinutes,
                PartialReleaseMinute = dependency.ReleaseThresholdFraction is { } fraction
                    ? predecessorPlan.StartMinute
                        + ((predecessorPlan.FinishMinute - predecessorPlan.StartMinute) * fraction)
                    : null,
            });

            var edge = $"{dependency.PredecessorOperationId}->{dependency.SuccessorOperationId}"
                + $" ({dependency.Relation}, lag {dependency.LagMinutes})";

            if (bounds.EarliestStartMinute is { } earliestStart)
            {
                Assert.True(
                    successorPlan.StartMinute >= earliestStart,
                    $"{edge}: starts at {successorPlan.StartMinute}, earliest allowed {earliestStart}.");
            }

            if (bounds.EarliestFinishMinute is { } earliestFinish)
            {
                Assert.True(
                    successorPlan.FinishMinute >= earliestFinish,
                    $"{edge}: finishes at {successorPlan.FinishMinute}, earliest allowed {earliestFinish}.");
            }
        }
    }

    private static void AssertCapacityIsRespected(
        SchedulingRequest request,
        SchedulingSolution solution)
    {
        foreach (var resource in request.Resources)
        {
            // Zero-length milestones consume nothing, so they are not part of the count.
            var intervals = solution.Operations
                .Where(plan => string.Equals(plan.ResourceKey, resource.ResourceKey, StringComparison.Ordinal))
                .Where(plan => plan.FinishMinute > plan.StartMinute)
                .ToList();

            // The concurrent count can only rise at a start, so those are the only instants
            // worth sampling — checking them all is exact, not a spot check.
            foreach (var instant in intervals.Select(plan => plan.StartMinute).Distinct())
            {
                var concurrent = intervals.Count(plan =>
                    plan.StartMinute <= instant && plan.FinishMinute > instant);

                Assert.True(
                    concurrent <= resource.Capacity,
                    $"Resource '{resource.ResourceKey}' runs {concurrent} operations at minute "
                        + $"{instant}, capacity is {resource.Capacity}.");
            }
        }
    }
}
