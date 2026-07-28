using System;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The FS/SS/FF/SF + lag + fixed-override policy boundary (Doorstar-confirmed M1 scope).
/// </summary>
public sealed class DependencyBoundResolverTests
{
    private static DependencyBoundInput Edge(DependencyType type, decimal lag = 0m) => new()
    {
        Type = type,
        PredecessorStartMinute = 100m,
        PredecessorFinishMinute = 140m,
        LagMinutes = lag,
    };

    private static ResolvedDependencyBounds Resolve(DependencyBoundInput input) =>
        DependencyBoundResolver.Resolve(input, PartialReleasePolicy.Unspecified);

    [Theory]
    [InlineData(DependencyType.FinishToStart, 140)]
    [InlineData(DependencyType.StartToStart, 100)]
    public void Start_relations_bind_the_start_and_leave_the_finish_free(DependencyType type, decimal expectedStart)
    {
        var bounds = Resolve(Edge(type));

        Assert.Equal(expectedStart, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.Dependency, bounds.StartSource);
        Assert.Null(bounds.EarliestFinishMinute);
        Assert.Null(bounds.FinishSource);
    }

    [Theory]
    [InlineData(DependencyType.FinishToFinish, 140)]
    [InlineData(DependencyType.StartToFinish, 100)]
    public void Finish_relations_bind_the_finish_and_leave_the_start_free(DependencyType type, decimal expectedFinish)
    {
        var bounds = Resolve(Edge(type));

        Assert.Equal(expectedFinish, bounds.EarliestFinishMinute);
        Assert.Equal(BoundSource.Dependency, bounds.FinishSource);
        Assert.Null(bounds.EarliestStartMinute);
        Assert.Null(bounds.StartSource);
    }

    [Theory]
    [InlineData(DependencyType.FinishToStart, 15, 155)]
    [InlineData(DependencyType.StartToStart, 15, 115)]
    public void A_positive_lag_pushes_the_bound_out(DependencyType type, decimal lag, decimal expected)
    {
        Assert.Equal(expected, Resolve(Edge(type, lag)).EarliestStartMinute);
    }

    [Fact]
    public void A_negative_lag_is_a_lead_and_pulls_the_bound_in()
    {
        // Overlapping work is a legitimate shop-floor pattern, so a lead must not be
        // clamped to zero here; feasibility against the calendar is the scheduler's job.
        Assert.Equal(130m, Resolve(Edge(DependencyType.FinishToStart, -10m)).EarliestStartMinute);
    }

    [Fact]
    public void A_fixed_start_replaces_the_relation_bound_and_is_attributed_as_such()
    {
        var bounds = Resolve(Edge(DependencyType.FinishToStart) with { FixedStartMinute = 90m });

        Assert.Equal(90m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.FixedOverride, bounds.StartSource);
    }

    [Fact]
    public void A_fixed_finish_replaces_the_relation_bound_on_the_finish_branch()
    {
        var bounds = Resolve(Edge(DependencyType.FinishToFinish) with { FixedFinishMinute = 500m });

        Assert.Equal(500m, bounds.EarliestFinishMinute);
        Assert.Equal(BoundSource.FixedOverride, bounds.FinishSource);
    }

    [Fact]
    public void A_fixed_bound_applies_even_where_the_relation_produces_none()
    {
        // An FS edge yields no finish bound, but an operator-fixed finish still stands.
        var bounds = Resolve(Edge(DependencyType.FinishToStart) with { FixedFinishMinute = 300m });

        Assert.Equal(300m, bounds.EarliestFinishMinute);
        Assert.Equal(BoundSource.FixedOverride, bounds.FinishSource);
        Assert.Equal(140m, bounds.EarliestStartMinute);
        Assert.Equal(BoundSource.Dependency, bounds.StartSource);
    }

    [Fact]
    public void The_two_branches_are_resolved_independently()
    {
        var bounds = Resolve(Edge(DependencyType.StartToStart) with { FixedFinishMinute = 400m });

        Assert.Equal(BoundSource.Dependency, bounds.StartSource);
        Assert.Equal(BoundSource.FixedOverride, bounds.FinishSource);
    }

    [Fact]
    public void A_zero_length_predecessor_is_valid()
    {
        var bounds = Resolve(new DependencyBoundInput
        {
            Type = DependencyType.FinishToStart,
            PredecessorStartMinute = 100m,
            PredecessorFinishMinute = 100m,
        });

        Assert.Equal(100m, bounds.EarliestStartMinute);
    }

    [Fact]
    public void A_predecessor_finishing_before_it_starts_is_a_caller_error()
    {
        var input = new DependencyBoundInput
        {
            Type = DependencyType.FinishToStart,
            PredecessorStartMinute = 200m,
            PredecessorFinishMinute = 100m,
        };

        Assert.Throws<ArgumentException>(() => Resolve(input));
    }
}
