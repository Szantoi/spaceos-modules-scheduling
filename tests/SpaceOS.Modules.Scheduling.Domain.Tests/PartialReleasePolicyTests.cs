using System;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The partial-release semantics is an OPEN contract question (Doorstar root, 2026-07-28).
/// These tests pin the policy BOUNDARY: that the core refuses to assume a reading, and
/// that both candidate readings behave as documented.
/// </summary>
public sealed class PartialReleasePolicyTests
{
    private static DependencyBoundInput Edge(decimal? partialRelease = null, decimal? fixedStart = null) => new()
    {
        Type = DependencyType.FinishToStart,
        PredecessorStartMinute = 100m,
        PredecessorFinishMinute = 200m,
        PartialReleaseMinute = partialRelease,
        FixedStartMinute = fixedStart,
    };

    [Fact]
    public void Refuses_to_assume_a_semantics_when_a_release_is_present()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DependencyBoundResolver.Resolve(Edge(partialRelease: 150m), PartialReleasePolicy.Unspecified));

        Assert.Contains("not settled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unspecified_is_fine_when_no_release_is_involved()
    {
        // An unconfigured caller must still be able to resolve ordinary edges: the refusal
        // is scoped to the undecided rule, it is not a global tax.
        var bounds = DependencyBoundResolver.Resolve(Edge(), PartialReleasePolicy.Unspecified);

        Assert.Equal(200m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.Dependency, bounds.StartSource);
    }

    [Theory]
    [InlineData(150, 150, BoundSource.PartialRelease)]  // earlier than the FS bound (the fixture case)
    [InlineData(260, 260, BoundSource.PartialRelease)]  // LATER -- the reading the fixture cannot distinguish
    public void ReplacesDependencyBound_takes_the_release_unconditionally(
        decimal release, decimal expectedStart, BoundSource expectedSource)
    {
        var bounds = DependencyBoundResolver.Resolve(
            Edge(partialRelease: release), PartialReleasePolicy.ReplacesDependencyBound);

        Assert.Equal(expectedStart, bounds.EarliestStartMinute);
        Assert.Equal(expectedSource, bounds.StartSource);
    }

    [Theory]
    [InlineData(150, 150, BoundSource.PartialRelease)]  // may pull the start earlier
    [InlineData(260, 200, BoundSource.Dependency)]      // may NOT push it out
    public void MayOnlyAdvanceStart_ignores_a_release_that_is_later(
        decimal release, decimal expectedStart, BoundSource expectedSource)
    {
        var bounds = DependencyBoundResolver.Resolve(
            Edge(partialRelease: release), PartialReleasePolicy.MayOnlyAdvanceStart);

        Assert.Equal(expectedStart, bounds.EarliestStartMinute);
        Assert.Equal(expectedSource, bounds.StartSource);
    }

    [Fact]
    public void The_two_policies_differ_only_for_a_release_later_than_the_bound()
    {
        // This is exactly why the question is open: on the pinned fixture vector the two
        // readings are indistinguishable, so the gate alone cannot decide the contract.
        var earlier = Edge(partialRelease: 150m);
        Assert.Equal(
            DependencyBoundResolver.Resolve(earlier, PartialReleasePolicy.ReplacesDependencyBound).EarliestStartMinute,
            DependencyBoundResolver.Resolve(earlier, PartialReleasePolicy.MayOnlyAdvanceStart).EarliestStartMinute);

        var later = Edge(partialRelease: 260m);
        Assert.NotEqual(
            DependencyBoundResolver.Resolve(later, PartialReleasePolicy.ReplacesDependencyBound).EarliestStartMinute,
            DependencyBoundResolver.Resolve(later, PartialReleasePolicy.MayOnlyAdvanceStart).EarliestStartMinute);
    }

    [Fact]
    public void A_fixed_start_outranks_the_release_under_either_policy()
    {
        foreach (var policy in new[] { PartialReleasePolicy.ReplacesDependencyBound, PartialReleasePolicy.MayOnlyAdvanceStart })
        {
            var bounds = DependencyBoundResolver.Resolve(Edge(partialRelease: 150m, fixedStart: 120m), policy);

            Assert.Equal(120m, bounds.EarliestStartMinute);
            Assert.Equal(BoundSource.FixedOverride, bounds.StartSource);
        }
    }

    [Fact]
    public void The_threshold_conversion_refuses_rather_than_guessing()
    {
        // A plausible-looking linear split would silently produce wrong schedules; the seam
        // exists, the implementation waits for the Doorstar answer.
        IPartialReleaseCalculator calculator = new PendingContractReleaseCalculator();

        var exception = Assert.Throws<NotSupportedException>(
            () => calculator.CalculateReleaseMinute(100m, 200m, 0.5m));
        Assert.Contains("open contract question", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_baseline_policy_is_labelled_as_not_final()
    {
        Assert.Contains("not final", PartialReleaseContract.BaselineLabel, StringComparison.OrdinalIgnoreCase);
    }
}
