using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The plan lifecycle (ADR-069 §4): Proposal → Shadow → Published → Superseded,
/// with Discarded reachable before publication.
/// </summary>
public sealed class ScheduleRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private static ScheduleRun NewRun() =>
        ScheduleRun.Open(Guid.NewGuid(), TenantId, ProjectRef.From(Guid.NewGuid()), Now);

    private static IReadOnlyList<OperationPlan> Operations(params string[] ids) =>
        [.. ids.Select((id, index) => new OperationPlan
        {
            OperationId = id,
            ResourceKey = "resource-1",
            StartMinute = index * 60,
            FinishMinute = (index * 60) + 45,
        })];

    [Fact]
    public void A_new_run_has_no_published_plan()
    {
        var run = NewRun();

        Assert.Empty(run.Revisions);
        Assert.Null(run.PublishedRevision);
    }

    [Fact]
    public void A_run_must_belong_to_a_tenant()
    {
        Assert.Throws<ArgumentException>(
            () => ScheduleRun.Open(Guid.NewGuid(), Guid.Empty, ProjectRef.From(Guid.NewGuid()), Now));
    }

    [Fact]
    public void An_empty_project_reference_is_not_a_reference()
    {
        Assert.Throws<ArgumentException>(() => ProjectRef.From(Guid.Empty));
    }

    [Fact]
    public void Revisions_are_numbered_in_order_and_start_as_proposals()
    {
        var run = NewRun();

        var first = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        var second = run.AddProposal(Guid.NewGuid(), Operations("a", "b"), Now);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.All(run.Revisions, revision => Assert.Equal(ScheduleRevisionState.Proposal, revision.State));
    }

    [Fact]
    public void Publishing_supersedes_the_previous_plan_in_the_same_step()
    {
        var run = NewRun();
        var first = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        var second = run.AddProposal(Guid.NewGuid(), Operations("a", "b"), Now);

        run.Publish(first.Id);
        var superseded = run.Publish(second.Id);

        Assert.Same(first, superseded);
        Assert.Equal(ScheduleRevisionState.Superseded, first.State);
        Assert.Equal(ScheduleRevisionState.Published, second.State);
    }

    [Fact]
    public void At_most_one_revision_is_ever_published()
    {
        var run = NewRun();
        var revisions = Enumerable.Range(0, 4)
            .Select(index => run.AddProposal(Guid.NewGuid(), Operations($"op{index}"), Now))
            .ToArray();

        foreach (var revision in revisions)
        {
            run.Publish(revision.Id);
            Assert.Single(run.Revisions.Where(item => item.State == ScheduleRevisionState.Published));
        }

        Assert.Same(revisions[^1], run.PublishedRevision);
    }

    [Fact]
    public void A_shadow_revision_does_not_disturb_the_published_plan()
    {
        var run = NewRun();
        var published = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        run.Publish(published.Id);

        var shadow = run.AddProposal(Guid.NewGuid(), Operations("a", "b"), Now);
        run.MoveToShadow(shadow.Id);

        Assert.Same(published, run.PublishedRevision);
        Assert.Equal(ScheduleRevisionState.Shadow, shadow.State);
    }

    [Fact]
    public void A_shadow_revision_can_be_promoted_to_published()
    {
        var run = NewRun();
        var shadow = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        run.MoveToShadow(shadow.Id);

        run.Publish(shadow.Id);

        Assert.Equal(ScheduleRevisionState.Published, shadow.State);
    }

    [Fact]
    public void A_discarded_revision_is_terminal()
    {
        var run = NewRun();
        var revision = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        run.Discard(revision.Id);

        Assert.True(revision.IsTerminal);
        Assert.Throws<InvalidOperationException>(() => run.Publish(revision.Id));
        Assert.Throws<InvalidOperationException>(() => run.MoveToShadow(revision.Id));
    }

    [Fact]
    public void A_superseded_revision_cannot_be_republished()
    {
        var run = NewRun();
        var first = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        var second = run.AddProposal(Guid.NewGuid(), Operations("b"), Now);
        run.Publish(first.Id);
        run.Publish(second.Id);

        Assert.Throws<InvalidOperationException>(() => run.Publish(first.Id));
    }

    [Fact]
    public void A_failed_publication_leaves_the_active_plan_untouched()
    {
        // The ordering matters: if Publish superseded the old revision before validating
        // the new one, this run would end up with NO active plan at all.
        var run = NewRun();
        var published = run.AddProposal(Guid.NewGuid(), Operations("a"), Now);
        run.Publish(published.Id);

        var discarded = run.AddProposal(Guid.NewGuid(), Operations("b"), Now);
        run.Discard(discarded.Id);

        Assert.Throws<InvalidOperationException>(() => run.Publish(discarded.Id));
        Assert.Same(published, run.PublishedRevision);
        Assert.Equal(ScheduleRevisionState.Published, published.State);
    }

    [Fact]
    public void A_revision_from_another_run_is_rejected()
    {
        var run = NewRun();
        var other = NewRun();
        var foreign = other.AddProposal(Guid.NewGuid(), Operations("a"), Now);

        Assert.Throws<InvalidOperationException>(() => run.Publish(foreign.Id));
    }

    [Fact]
    public void A_revision_snapshot_does_not_follow_later_edits_of_the_input_list()
    {
        var run = NewRun();
        var input = new List<OperationPlan>(Operations("a"));

        var revision = run.AddProposal(Guid.NewGuid(), input, Now);
        var hashAtCreation = revision.ContentHash;
        input.Add(Operations("b").Single());

        Assert.Single(revision.Operations);
        Assert.Equal(hashAtCreation, revision.ContentHash);
    }

    [Fact]
    public void The_same_operation_cannot_appear_twice_in_a_revision()
    {
        var run = NewRun();
        var duplicated = new List<OperationPlan>(Operations("a"));
        duplicated.Add(duplicated[0] with { ResourceKey = "resource-2" });

        Assert.Throws<ArgumentException>(() => run.AddProposal(Guid.NewGuid(), duplicated, Now));
    }

    [Fact]
    public void An_inverted_operation_interval_is_rejected()
    {
        var run = NewRun();
        var inverted = new[]
        {
            new OperationPlan
            {
                OperationId = "a", ResourceKey = "r", StartMinute = 100, FinishMinute = 40,
            },
        };

        Assert.Throws<ArgumentException>(() => run.AddProposal(Guid.NewGuid(), inverted, Now));
    }

    [Fact]
    public void A_zero_length_operation_is_allowed_as_a_milestone()
    {
        var run = NewRun();
        var milestone = new[]
        {
            new OperationPlan
            {
                OperationId = "gate", ResourceKey = "r", StartMinute = 100, FinishMinute = 100,
            },
        };

        var revision = run.AddProposal(Guid.NewGuid(), milestone, Now);
        Assert.Single(revision.Operations);
    }
}
