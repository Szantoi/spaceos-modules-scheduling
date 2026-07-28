using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// What a revision must carry to be a REPRODUCIBLE answer: the resolved precedence network,
/// the per-resource calendar pin, and the opaque upstream lineage (PLAN-03 M3 contract input).
/// </summary>
/// <remarks>
/// The theme of every case here is the same: a published revision is quoted back by the
/// consumer, so anything that changes what the plan MEANS must change its hash, and anything
/// the consumer cannot verify must not be silently dropped or reinterpreted.
/// </remarks>
public sealed class RevisionProvenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly ProjectRef TestProject = ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb"));
    private static readonly KernelWorkScope TestScope = KernelWorkScope.Create(
        TestProject,
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    private static readonly IReadOnlyDictionary<string, int> Pins =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 4 };

    private static ScheduleRun NewRun() => ScheduleRun.Open(Guid.NewGuid(), TenantId, TestProject, Now);

    private static OperationPlan Operation(
        string id,
        int? standardRevision = null,
        IReadOnlyDictionary<string, string>? sourceRevisions = null) => new()
    {
        OperationId = id,
        Scope = TestScope,
        ResourceKey = "r1",
        StartMinute = 0m,
        FinishMinute = 60m,
        StandardRevision = standardRevision,
        SourceRevisions = sourceRevisions ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static PlannedDependency Edge(
        string predecessor = "a",
        string successor = "b",
        DependencyType relation = DependencyType.FinishToStart,
        decimal? earliestStart = null,
        BoundSource? source = null,
        params DependencyWarning[] warnings) => new()
    {
        PredecessorOperationId = predecessor,
        SuccessorOperationId = successor,
        Relation = relation,
        LagMinutes = 15m,
        EarliestStartMinute = earliestStart,
        StartSource = source,
        Warnings = warnings,
    };

    private static string HashOf(
        IReadOnlyList<OperationPlan> operations,
        IReadOnlyList<PlannedDependency>? edges = null,
        IReadOnlyDictionary<string, int>? pins = null) =>
        NewRun().AddProposal(Guid.NewGuid(), operations, pins ?? Pins, Now, edges).ContentHash;

    [Fact]
    public void A_revision_keeps_the_edges_and_the_calendar_pins_it_was_built_with()
    {
        var revision = NewRun().AddProposal(
            Guid.NewGuid(), [Operation("a"), Operation("b")], Pins, Now, [Edge()]);

        Assert.Equal("a", Assert.Single(revision.Dependencies).PredecessorOperationId);
        Assert.Equal(4, revision.CalendarRevisions["r1"]);
    }

    [Fact]
    public void An_edge_pointing_outside_the_revision_is_refused()
    {
        // The consumer would see a constraint against an operation that is not in the
        // proposal: unverifiable and unactionable, so it must not be storable.
        var run = NewRun();

        var exception = Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a")], Pins, Now, [Edge(successor: "ghost")]));

        Assert.Contains("ghost", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operation_cannot_depend_on_itself()
    {
        var run = NewRun();

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a")], Pins, Now, [Edge(predecessor: "a", successor: "a")]));
    }

    [Fact]
    public void The_same_edge_cannot_appear_twice()
    {
        var run = NewRun();

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a"), Operation("b")], Pins, Now, [Edge(), Edge()]));
    }

    [Fact]
    public void The_same_pair_may_carry_two_different_relations()
    {
        // FS and SS between the same operations are two distinct statements, both legitimate.
        var revision = NewRun().AddProposal(
            Guid.NewGuid(), [Operation("a"), Operation("b")], Pins, Now,
            [Edge(), Edge(relation: DependencyType.StartToStart)]);

        Assert.Equal(2, revision.Dependencies.Count);
    }

    [Fact]
    public void A_start_bound_without_its_source_is_refused()
    {
        var run = NewRun();

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a"), Operation("b")], Pins, Now,
            [Edge(earliestStart: 120m)]));
    }

    [Fact]
    public void A_scheduled_resource_without_a_calendar_pin_is_refused()
    {
        // Without the pin the plan cannot be recomputed later — and "reproducible" is exactly
        // what publishing a revision promises.
        var run = NewRun();

        var exception = Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a")], new Dictionary<string, int>(StringComparer.Ordinal), Now));

        Assert.Contains("r1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_an_edge_changes_the_hash()
    {
        var operations = new[] { Operation("a"), Operation("b") };

        Assert.NotEqual(
            HashOf(operations, [Edge()]),
            HashOf(operations, [Edge(relation: DependencyType.StartToStart)]));
    }

    [Fact]
    public void Adding_a_warning_changes_the_hash()
    {
        // A proposal that warns and one that does not are different answers, even with
        // identical times: the planner is being told something different.
        var operations = new[] { Operation("a"), Operation("b") };

        Assert.NotEqual(
            HashOf(operations, [Edge(earliestStart: 30m, source: BoundSource.PartialRelease)]),
            HashOf(operations, [Edge(earliestStart: 30m, source: BoundSource.PartialRelease,
                warnings: DependencyWarning.PartialReleaseDelaysStart)]));
    }

    [Fact]
    public void A_different_calendar_revision_changes_the_hash()
    {
        var operations = new[] { Operation("a") };

        Assert.NotEqual(
            HashOf(operations, pins: new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 4 }),
            HashOf(operations, pins: new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 5 }));
    }

    [Fact]
    public void A_different_standard_revision_changes_the_hash()
    {
        Assert.NotEqual(
            HashOf([Operation("a", standardRevision: 2)]),
            HashOf([Operation("a", standardRevision: 3)]));
    }

    [Fact]
    public void Different_upstream_lineage_changes_the_hash()
    {
        Assert.NotEqual(
            HashOf([Operation("a", sourceRevisions: Lineage("order-7", "3"))]),
            HashOf([Operation("a", sourceRevisions: Lineage("order-7", "4"))]));
    }

    [Fact]
    public void The_order_lineage_was_written_in_does_not_change_the_hash()
    {
        // The same lineage deserialised in another order is the same lineage; a hash that
        // moved with dictionary order would report false plan changes after every reload.
        var first = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orderRevision"] = "3",
            ["calculationRevision"] = "9",
        };
        var second = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["calculationRevision"] = "9",
            ["orderRevision"] = "3",
        };

        Assert.Equal(HashOf([Operation("a", sourceRevisions: first)]), HashOf([Operation("a", sourceRevisions: second)]));
    }

    [Fact]
    public void Lineage_is_reflected_back_exactly_as_given()
    {
        // The platform stores and returns; it never resolves. A normalised key or a trimmed
        // value would break the consumer's lineage proof.
        var lineage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orderKey"] = "  ORD-2026/07-XY  ",
            ["processRowRevision"] = "v2.1",
        };

        var revision = NewRun().AddProposal(Guid.NewGuid(), [Operation("a", sourceRevisions: lineage)], Pins, Now);

        Assert.Equal(lineage, Assert.Single(revision.Operations).SourceRevisions);
    }

    [Fact]
    public void A_blank_lineage_value_is_refused()
    {
        var run = NewRun();

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a", sourceRevisions: Lineage("orderKey", "   "))], Pins, Now));
    }

    [Fact]
    public void An_oversized_lineage_block_is_refused()
    {
        // Opaque is not unbounded: an unvalidated pass-through map is a storage channel for
        // anyone holding a token.
        var oversized = Enumerable.Range(0, OperationPlan.MaxSourceRevisionEntries + 1)
            .ToDictionary(index => $"key{index}", index => index.ToString(), StringComparer.Ordinal);
        var run = NewRun();

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a", sourceRevisions: oversized)], Pins, Now));
    }

    [Fact]
    public void An_oversized_lineage_value_is_refused()
    {
        var run = NewRun();
        var lineage = Lineage("orderKey", new string('x', OperationPlan.MaxSourceRevisionLength + 1));

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a", sourceRevisions: lineage)], Pins, Now));
    }

    [Fact]
    public void A_standard_revision_below_one_is_refused()
    {
        var run = NewRun();

        Assert.Throws<ArgumentException>(() => run.AddProposal(
            Guid.NewGuid(), [Operation("a", standardRevision: 0)], Pins, Now));
    }

    private static Dictionary<string, string> Lineage(string key, string value) =>
        new(StringComparer.Ordinal) { [key] = value };
}
