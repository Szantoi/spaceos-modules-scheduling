using System;
using System.Collections.Generic;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The revision hash is an IDENTITY Doorstar quotes back, so additive contract fields must not
/// move it for plans that do not use them.
/// </summary>
/// <remarks>
/// <para>
/// Root's condition on the contract round (2026-07-29): "a plan carrying lagKind=working must
/// hash byte-for-byte identically to one where the field is not present at all". Without this
/// test that is an assumption, not a property — and it is exactly the assumption Doorstar
/// builds on when it re-quotes a hash.
/// </para>
/// <para>
/// The pin below was measured BEFORE the additive fields existed. If a future change moves it,
/// that is not a test to update: it means every stored hash in every consumer just became
/// wrong, and the change needs a deliberate, announced migration.
/// </para>
/// </remarks>
public sealed class RevisionHashCompatibilityTests
{
    /// <summary>Hash of <see cref="ReferencePlan"/> as measured before the additive fields.</summary>
    private const string PinnedHash = "f3297940ecab2290a2b077458f80005d9f1ee6b771dd2b99d37d23bbdf8ad691";

    private static readonly ProjectRef Project = ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb"));
    private static readonly KernelWorkScope Scope = KernelWorkScope.Create(
        Project,
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    private static OperationPlan Operation(string id, decimal start, decimal finish) => new()
    {
        OperationId = id,
        Scope = Scope,
        ResourceKey = "r1",
        StartMinute = start,
        FinishMinute = finish,
        AutomaticallyPlanned = true,
    };

    /// <summary>A small, fully specified plan — the fixture the pin is measured on.</summary>
    private static (IReadOnlyList<OperationPlan> Operations, IReadOnlyDictionary<string, int> Calendars)
        ReferencePlan() => (
            [Operation("a", 0m, 60m), Operation("b", 60m, 120m)],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1 });

    private static PlannedDependency Edge() => new()
    {
        PredecessorOperationId = "a",
        SuccessorOperationId = "b",
        Relation = DependencyType.FinishToStart,
        LagMinutes = 0m,
        EarliestStartMinute = 60m,
        StartSource = BoundSource.Dependency,
        Warnings = [],
    };

    [Fact]
    public void The_reference_plan_still_hashes_to_its_pin()
    {
        var (operations, calendars) = ReferencePlan();

        var hash = RevisionHasher.ComputeHash(operations, [Edge()], calendars);

        Assert.Equal(PinnedHash, hash);
    }

    [Fact]
    public void An_explicit_default_hashes_identically_to_an_absent_field()
    {
        // Root's condition, made literal: writing out the default must be indistinguishable
        // from not writing it at all. This is what lets us say "the existing plans' hashes do
        // not move" as a property rather than a hope.
        var (operations, calendars) = ReferencePlan();

        var explicitDefault = Edge() with
        {
            LagKind = LagKind.WorkingTime,
            ReleaseThresholdFraction = null,
        };

        Assert.Equal(
            PinnedHash,
            RevisionHasher.ComputeHash(operations, [explicitDefault], calendars));
    }

    [Fact]
    public void A_release_threshold_changes_the_hash()
    {
        // The other half of the bargain: if the field travels on the wire, it MUST reach the
        // hash — otherwise two different agreements share one identity.
        var (operations, calendars) = ReferencePlan();

        var withRelease = Edge() with { ReleaseThresholdFraction = 0.5m };

        Assert.NotEqual(
            PinnedHash,
            RevisionHasher.ComputeHash(operations, [withRelease], calendars));
    }

    [Fact]
    public void Two_different_release_thresholds_hash_differently()
    {
        // "We let it go at 0.5" and "at 0.8" are different agreements, and the dates can
        // coincide today while the plans mean different things.
        var (operations, calendars) = ReferencePlan();

        var half = RevisionHasher.ComputeHash(
            operations, [Edge() with { ReleaseThresholdFraction = 0.5m }], calendars);
        var most = RevisionHasher.ComputeHash(
            operations, [Edge() with { ReleaseThresholdFraction = 0.8m }], calendars);

        Assert.NotEqual(half, most);
    }

    [Fact]
    public void An_elapsed_time_lag_changes_the_hash()
    {
        var (operations, calendars) = ReferencePlan();

        var elapsed = Edge() with { LagKind = LagKind.ElapsedTime };

        Assert.NotEqual(
            PinnedHash,
            RevisionHasher.ComputeHash(operations, [elapsed], calendars));
    }

    [Fact]
    public void A_lag_kind_cannot_be_confused_with_a_release_threshold()
    {
        // Why each additive value carries a label in the canonical form: without one, a plan
        // holding only a lag kind and a plan holding only a release fraction could serialise
        // to the same bytes and collide.
        var (operations, calendars) = ReferencePlan();

        var lagOnly = RevisionHasher.ComputeHash(
            operations, [Edge() with { LagKind = LagKind.ElapsedTime }], calendars);
        var releaseOnly = RevisionHasher.ComputeHash(
            operations, [Edge() with { ReleaseThresholdFraction = 0.5m }], calendars);

        Assert.NotEqual(lagOnly, releaseOnly);
    }
}
