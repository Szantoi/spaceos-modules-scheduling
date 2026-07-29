using SpaceOS.Modules.Scheduling.Domain.Solving;
using SpaceOS.Modules.Scheduling.Domain.Tests.Solving;

namespace SpaceOS.Modules.Scheduling.Solver.OrTools.Tests;

/// <summary>The CP-SAT adapter, measured against the SAME suite as the reference strategy.</summary>
/// <remarks>
/// Not a copy of the reference's tests: the identical class from the Domain test assembly runs
/// here against a different implementation. That is the whole argument for the port — the two
/// strategies answer to one definition of correct, and a disagreement shows up as a failing
/// case rather than as a plan somebody notices later.
/// </remarks>
public sealed class CpSatSchedulingSolverConformanceTests : SchedulingSolverConformanceTests
{
    /// <inheritdoc />
    protected override ISchedulingSolver CreateSolver() => new CpSatSchedulingSolver();
}
