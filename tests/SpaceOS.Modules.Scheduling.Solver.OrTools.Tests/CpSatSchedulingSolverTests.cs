using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using static SpaceOS.Modules.Scheduling.Domain.Tests.Solving.SolverScenarios;

namespace SpaceOS.Modules.Scheduling.Solver.OrTools.Tests;

/// <summary>
/// What the adapter must do that the shared conformance suite cannot express: be at least as
/// good as the reference, stay reproducible, and be honest when it is not.
/// </summary>
public sealed class CpSatSchedulingSolverTests
{
    private static decimal Makespan(SchedulingSolution solution) =>
        solution.Operations.Max(operation => operation.FinishMinute);

    /// <summary>
    /// The case the port was built for: greed strands work behind a choice that looked free.
    /// </summary>
    /// <remarks>
    /// One machine takes a long job and a short one; the short job releases a follow-up on a
    /// second machine. Placing the long job first — which a list scheduler walking the
    /// topological order does — delays everything behind it. If this test ever showed the two
    /// strategies level, the optimiser would be costing native binaries for nothing.
    /// </remarks>
    [Fact]
    public void The_optimiser_finds_the_shorter_plan_the_greedy_reference_cannot()
    {
        var request = Request(
            [
                Operation("a", duration: 100m),
                Operation("b", duration: 10m),
                Operation("c", duration: 50m, resource: "r2"),
            ],
            [Edge("b", "c")]);

        var reference = Makespan(new DeterministicListSolver().Solve(request));
        var optimised = Makespan(new CpSatSchedulingSolver().Solve(request));

        Assert.Equal(160m, reference);
        Assert.Equal(110m, optimised);
    }

    [Fact]
    public void The_optimiser_is_never_worse_than_the_reference_on_the_contended_case()
    {
        var request = Request(
            [
                Operation("a", duration: 60m),
                Operation("b", duration: 30m),
                Operation("c", duration: 45m),
                Operation("d", duration: 90m),
                Operation("e", duration: 15m, resource: "r2"),
                Operation("f", duration: 75m, resource: "r2"),
            ],
            [Edge("a", "d"), Edge("b", "c", DependencyType.StartToStart, lag: 10m), Edge("e", "f")],
            capacity: 2m);

        var reference = Makespan(new DeterministicListSolver().Solve(request));
        var optimised = Makespan(new CpSatSchedulingSolver().Solve(request));

        Assert.True(
            optimised <= reference,
            $"The optimiser returned {optimised} against the reference's {reference}.");
    }

    [Fact]
    public void The_same_input_twice_produces_identical_start_minutes()
    {
        // The conformance suite asserts this on the revision hash; here on the plan itself, so
        // a determinism break is visible as the minutes that moved rather than as a digest.
        var request = ContendedRequest();

        var first = new CpSatSchedulingSolver().Solve(request);
        var second = new CpSatSchedulingSolver().Solve(request);

        Assert.Equal(
            first.Operations.Select(operation => (operation.OperationId, operation.StartMinute)),
            second.Operations.Select(operation => (operation.OperationId, operation.StartMinute)));
    }

    [Fact]
    public void The_default_profile_reports_itself_reproducible()
    {
        Assert.True(new CpSatSchedulingSolver().Solve(ContendedRequest()).IsReproducible);
    }

    [Fact]
    public void A_parallel_search_admits_that_its_plan_is_not_reproducible()
    {
        // Opt-in speed at the cost of a stable identity. The flag is the whole safeguard:
        // Doorstar quotes the revision hash back, and a hash that can change for an unchanged
        // input must not be presented as an identity.
        var solver = new CpSatSchedulingSolver(new CpSatSolverOptions
        {
            AllowParallelSearch = true,
            ParallelSearchWorkers = 4,
        });

        Assert.False(solver.Solve(ContendedRequest()).IsReproducible);
    }

    [Fact]
    public void A_duration_off_the_solver_grid_is_reserved_conservatively()
    {
        // 0.005 minutes cannot be represented at hundredths, and rounding DOWN would let two
        // operations share a slot they do not fit in. Rounding up costs a fraction of a second
        // of idle time; rounding down would produce a plan that cannot be executed.
        var request = Request([Operation("a", duration: 0.005m), Operation("b", duration: 0.005m)]);

        var solution = new CpSatSchedulingSolver().Solve(request);

        var first = Placed(solution, "a");
        var second = Placed(solution, "b");

        Assert.True(
            second.StartMinute >= first.FinishMinute,
            $"'b' starts at {second.StartMinute}, before 'a' finishes at {first.FinishMinute}.");
    }

    private static SchedulingRequest ContendedRequest() => Request(
        [
            Operation("a", duration: 60m),
            Operation("b", duration: 30m),
            Operation("c", duration: 45m, resource: "r2"),
            Operation("d", duration: 90m, resource: "r2"),
        ],
        [Edge("a", "b"), Edge("a", "c", release: 0.5m), Edge("c", "d", DependencyType.StartToStart, lag: 10m)],
        capacity: 2m);
}
