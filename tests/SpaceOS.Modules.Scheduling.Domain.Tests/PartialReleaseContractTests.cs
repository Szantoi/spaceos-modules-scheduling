using System;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The settled partial-release rule (business owner decision, 2026-07-28, ADR-069 §4).
/// </summary>
/// <remarks>
/// These replace the earlier policy-boundary tests, which existed only to prove the core
/// refused to assume an undecided semantics. The question is answered, so the tests now pin
/// the ANSWER instead of the refusal.
/// </remarks>
public sealed class PartialReleaseContractTests
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
    public void An_earlier_release_pulls_the_start_in_without_a_warning()
    {
        var bounds = DependencyBoundResolver.Resolve(Edge(partialRelease: 150m));

        Assert.Equal(150m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.PartialRelease, bounds.StartSource);
        Assert.Empty(bounds.Warnings);
    }

    [Fact]
    public void A_later_release_still_wins_but_must_warn()
    {
        // The rule is unconditional override. Because that DELAYS work the dependency would
        // have allowed earlier, the proposal has to say so rather than leave a planner
        // wondering why the operation sits idle.
        var bounds = DependencyBoundResolver.Resolve(Edge(partialRelease: 260m));

        Assert.Equal(260m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.PartialRelease, bounds.StartSource);
        Assert.Contains(DependencyWarning.PartialReleaseDelaysStart, bounds.Warnings);
    }

    [Fact]
    public void A_release_exactly_on_the_dependency_bound_is_not_a_delay()
    {
        // Equal is not later; warning on it would train planners to ignore the warning.
        var bounds = DependencyBoundResolver.Resolve(Edge(partialRelease: 200m));

        Assert.Equal(200m, bounds.EarliestStartMinute);
        Assert.Empty(bounds.Warnings);
    }

    [Fact]
    public void A_fixed_start_still_outranks_the_release_and_suppresses_the_warning()
    {
        // With a fixed date the release never applied, so there is nothing to warn about.
        var bounds = DependencyBoundResolver.Resolve(Edge(partialRelease: 260m, fixedStart: 120m));

        Assert.Equal(120m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.FixedOverride, bounds.StartSource);
        Assert.Empty(bounds.Warnings);
    }

    [Fact]
    public void An_edge_without_a_release_needs_no_configuration_at_all()
    {
        // The old API demanded an explicit policy on every call because the rule was unknown.
        // With the rule settled that tax is gone: an ordinary edge just resolves.
        var bounds = DependencyBoundResolver.Resolve(Edge());

        Assert.Equal(200m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.Dependency, bounds.StartSource);
        Assert.Empty(bounds.Warnings);
    }

    [Fact]
    public void The_contract_label_records_that_the_rule_is_final()
    {
        Assert.Equal("doorstar-contract-v1 (final)", PartialReleaseContract.ContractLabel);
        Assert.DoesNotContain("not final", PartialReleaseContract.ContractLabel, StringComparison.OrdinalIgnoreCase);
    }
}
