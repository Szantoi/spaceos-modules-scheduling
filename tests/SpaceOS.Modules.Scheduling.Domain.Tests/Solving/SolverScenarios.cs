using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Solving;

namespace SpaceOS.Modules.Scheduling.Domain.Tests.Solving;

/// <summary>
/// Request builders shared by every solver test, in this assembly and in the adapter's.
/// </summary>
/// <remarks>
/// The port only pays off if the reference strategy and the optimiser can be measured on the
/// SAME cases (ADR-070 D1). That requires one place where a case is written down; two copies
/// would drift and the comparison would quietly stop meaning anything.
/// </remarks>
public static class SolverScenarios
{
    /// <summary>The project every scenario operation belongs to.</summary>
    public static readonly ProjectRef Project = ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb"));

    /// <summary>The Kernel work scope every scenario operation carries.</summary>
    public static readonly KernelWorkScope Scope = KernelWorkScope.Create(
        Project,
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    /// <summary>A fixed instant, so nothing in a scenario depends on the wall clock.</summary>
    public static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

    /// <summary>One operation to place.</summary>
    public static SolverOperation Operation(
        string id,
        decimal duration = 60m,
        string resource = "r1",
        decimal? fixedStart = null,
        bool eligible = true) => new()
        {
            OperationId = id,
            Scope = Scope,
            ResourceKey = resource,
            DurationMinutes = duration,
            FixedStartMinute = fixedStart,
            EligibleForAutomaticPlanning = eligible,
        };

    /// <summary>One precedence edge.</summary>
    public static SolverDependency Edge(
        string predecessor,
        string successor,
        DependencyType relation = DependencyType.FinishToStart,
        decimal lag = 0m,
        decimal? release = null,
        LagKind lagKind = LagKind.WorkingTime) => new()
        {
            PredecessorOperationId = predecessor,
            SuccessorOperationId = successor,
            Relation = relation,
            LagMinutes = lag,
            ReleaseThresholdFraction = release,
            LagKind = lagKind,
        };

    /// <summary>A request whose resources are derived from the operations.</summary>
    public static SchedulingRequest Request(
        IReadOnlyList<SolverOperation> operations,
        IReadOnlyList<SolverDependency>? dependencies = null,
        decimal capacity = 1m) => new()
        {
            Operations = operations,
            Dependencies = dependencies ?? [],
            Resources = [.. operations
                .Select(operation => operation.ResourceKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(key => new SolverResource(key, capacity, 1))],
        };

    /// <summary>The plan of one operation.</summary>
    public static OperationPlan Placed(SchedulingSolution solution, string operationId) =>
        solution.Operations.Single(operation => string.Equals(
            operation.OperationId, operationId, StringComparison.Ordinal));

    /// <summary>Builds the revision this solution would become, and returns its content hash.</summary>
    /// <remarks>
    /// The hash — not the plan object — is what Doorstar quotes back, so determinism is
    /// asserted on it rather than on an in-memory comparison that could differ in ways the
    /// contract never sees.
    /// </remarks>
    public static string HashOf(SchedulingSolution solution)
    {
        var run = ScheduleRun.Open(Guid.NewGuid(), Guid.NewGuid(), Project, Now);
        return run.AddProposal(
            Guid.NewGuid(), solution.Operations, solution.CalendarRevisions, Now, solution.Dependencies)
            .ContentHash;
    }
}
