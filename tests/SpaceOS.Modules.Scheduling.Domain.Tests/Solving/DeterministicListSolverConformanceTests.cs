using SpaceOS.Modules.Scheduling.Domain.Solving;

namespace SpaceOS.Modules.Scheduling.Domain.Tests.Solving;

/// <summary>The reference strategy, measured against the shared solver conformance suite.</summary>
/// <remarks>
/// The reference is the baseline the CP-SAT adapter is compared to, so it has to pass the
/// same suite first — otherwise a later disagreement between the two could not be attributed
/// to either of them.
/// </remarks>
public sealed class DeterministicListSolverConformanceTests : SchedulingSolverConformanceTests
{
    /// <inheritdoc />
    protected override ISchedulingSolver CreateSolver() => new DeterministicListSolver();
}
