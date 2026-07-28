using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Dependency network validation and ordering — the Doorstar-confirmed M1 scope.
/// </summary>
public sealed class DependencyGraphTests
{
    private static OperationNode[] Nodes(params string[] ids) => [.. ids.Select(id => new OperationNode(id))];

    private static DependencyEdge Edge(string predecessor, string successor, string type = "FS") => new()
    {
        PredecessorId = predecessor,
        SuccessorId = successor,
        Type = type,
    };

    [Fact]
    public void Orders_a_valid_network_deterministically()
    {
        var result = DependencyGraph.Validate(
            Nodes("cut", "assemble", "finish"),
            [Edge("cut", "assemble"), Edge("assemble", "finish")]);

        Assert.True(result.IsValid);
        Assert.Equal(["cut", "assemble", "finish"], result.TopologicalOrder);
    }

    [Fact]
    public void Independent_nodes_are_ordered_ordinally_so_a_revision_hash_stays_reproducible()
    {
        var forward = DependencyGraph.Validate(Nodes("b", "a", "c"), []);
        var shuffled = DependencyGraph.Validate(Nodes("c", "b", "a"), []);

        Assert.Equal(["a", "b", "c"], forward.TopologicalOrder);
        Assert.Equal(forward.TopologicalOrder, shuffled.TopologicalOrder);
    }

    [Fact]
    public void Detects_a_cycle_and_refuses_to_return_a_partial_order()
    {
        var result = DependencyGraph.Validate(
            Nodes("a", "b", "c"),
            [Edge("a", "b"), Edge("b", "c"), Edge("c", "a")]);

        Assert.False(result.IsValid);
        Assert.Equal(DependencyGraphIssueCode.CircularDependency, result.Issues.Single().Code);
        Assert.Null(result.TopologicalOrder);
    }

    [Fact]
    public void A_network_with_any_issue_yields_no_order_at_all()
    {
        var result = DependencyGraph.Validate(Nodes("a"), [Edge("a", "ghost")]);

        Assert.Null(result.TopologicalOrder);
        Assert.Contains(result.Issues, issue => issue.Code == DependencyGraphIssueCode.UnknownSuccessor);
    }

    [Fact]
    public void Reports_blank_and_duplicate_operation_ids()
    {
        var result = DependencyGraph.Validate(Nodes("a", "a", " "), []);

        Assert.Contains(result.Issues, issue => issue.Code == DependencyGraphIssueCode.DuplicateOperationId);
        Assert.Contains(result.Issues, issue => issue.Code == DependencyGraphIssueCode.BlankOperationId);
    }

    [Fact]
    public void Reports_a_self_dependency()
    {
        var result = DependencyGraph.Validate(Nodes("a"), [Edge("a", "a")]);

        Assert.Contains(result.Issues, issue => issue.Code == DependencyGraphIssueCode.SelfDependency);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("fs")]
    [InlineData("")]
    public void Reports_an_unknown_relation_code_instead_of_throwing(string type)
    {
        var result = DependencyGraph.Validate(Nodes("a", "b"), [Edge("a", "b", type)]);

        Assert.Contains(result.Issues, issue => issue.Code == DependencyGraphIssueCode.InvalidDependencyType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Reports_a_release_threshold_outside_the_zero_to_one_range(double threshold)
    {
        var edge = new DependencyEdge
        {
            PredecessorId = "a",
            SuccessorId = "b",
            Type = "FS",
            ReleaseThresholdFraction = (decimal)threshold,
        };

        var result = DependencyGraph.Validate(Nodes("a", "b"), [edge]);

        Assert.Contains(result.Issues, issue => issue.Code == DependencyGraphIssueCode.InvalidReleaseThreshold);
    }

    [Fact]
    public void Accepts_a_full_release_threshold_of_one()
    {
        var edge = new DependencyEdge
        {
            PredecessorId = "a",
            SuccessorId = "b",
            Type = "FS",
            ReleaseThresholdFraction = 1m,
        };

        Assert.True(DependencyGraph.Validate(Nodes("a", "b"), [edge]).IsValid);
    }

    [Fact]
    public void Reports_a_duplicated_edge_but_accepts_two_different_relations_between_the_same_pair()
    {
        var duplicated = DependencyGraph.Validate(Nodes("a", "b"), [Edge("a", "b"), Edge("a", "b")]);
        Assert.Contains(duplicated.Issues, issue => issue.Code == DependencyGraphIssueCode.DuplicateDependency);

        var distinct = DependencyGraph.Validate(Nodes("a", "b"), [Edge("a", "b", "FS"), Edge("a", "b", "SS")]);
        Assert.True(distinct.IsValid);
    }

    [Fact]
    public void Ids_containing_the_key_separator_characters_do_not_collide()
    {
        // "a b" -> "c" and "a" -> "b c" would share a key under a space separator.
        var result = DependencyGraph.Validate(
            Nodes("a b", "c", "a", "b c"),
            [Edge("a b", "c"), Edge("a", "b c")]);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == DependencyGraphIssueCode.DuplicateDependency);
    }

    [Theory]
    [InlineData("FS", DependencyType.FinishToStart)]
    [InlineData("SS", DependencyType.StartToStart)]
    [InlineData("FF", DependencyType.FinishToFinish)]
    [InlineData("SF", DependencyType.StartToFinish)]
    public void Maps_every_wire_relation_code(string code, DependencyType expected)
    {
        Assert.True(DependencyGraph.TryParseRelationCode(code, out var type));
        Assert.Equal(expected, type);
    }
}
