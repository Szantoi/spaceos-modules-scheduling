using System;
using System.Globalization;
using System.Threading;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The revision hash is the plan's identity on the wire (Doorstar quotes it back), so it
/// must depend on content and on nothing else.
/// </summary>
public sealed class RevisionHasherTests
{
    private static readonly EpicRef TestEpic = EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666"));

    private static OperationPlan Operation(string id, decimal start = 0m, decimal finish = 60m, string resource = "r1") =>
        new() { OperationId = id, Epic = TestEpic, ResourceKey = resource, StartMinute = start, FinishMinute = finish };

    [Fact]
    public void Enumeration_order_does_not_change_the_hash()
    {
        var forward = RevisionHasher.ComputeHash([Operation("a"), Operation("b"), Operation("c")]);
        var reversed = RevisionHasher.ComputeHash([Operation("c"), Operation("b"), Operation("a")]);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void A_changed_minute_changes_the_hash()
    {
        Assert.NotEqual(
            RevisionHasher.ComputeHash([Operation("a", finish: 60m)]),
            RevisionHasher.ComputeHash([Operation("a", finish: 61m)]));
    }

    [Fact]
    public void Moving_an_operation_to_another_epic_changes_the_hash()
    {
        // The epic is part of what the plan SAYS about the work, not decoration: the same
        // times under a different epic is a different plan, and the consumer must see that.
        var moved = Operation("a") with
        {
            Epic = EpicRef.From(Guid.Parse("99999999-8888-4777-8666-555555555555")),
        };

        Assert.NotEqual(RevisionHasher.ComputeHash([Operation("a")]), RevisionHasher.ComputeHash([moved]));
    }

    [Fact]
    public void A_changed_resource_changes_the_hash()
    {
        Assert.NotEqual(
            RevisionHasher.ComputeHash([Operation("a", resource: "r1")]),
            RevisionHasher.ComputeHash([Operation("a", resource: "r2")]));
    }

    [Fact]
    public void Decimal_scale_does_not_change_the_hash()
    {
        // 60 and 60.00 are the same instant; a hash change here would look like a plan
        // change to the consumer and trigger a pointless re-review.
        Assert.Equal(
            RevisionHasher.ComputeHash([Operation("a", finish: 60m)]),
            RevisionHasher.ComputeHash([Operation("a", finish: 60.0000m)]));
    }

    [Fact]
    public void Field_boundaries_cannot_be_forged_by_a_crafted_id()
    {
        // Without length prefixes, "a|r1" as an id could reproduce the canonical form of
        // a different operation and collide deliberately.
        Assert.NotEqual(
            RevisionHasher.ComputeHash([Operation("a", resource: "r1")]),
            RevisionHasher.ComputeHash([Operation("a|r1", resource: string.Empty)]));
    }

    [Fact]
    public void The_automatic_planning_flag_is_part_of_the_identity()
    {
        var automatic = Operation("a");
        var manual = automatic with { AutomaticallyPlanned = false };

        Assert.NotEqual(RevisionHasher.ComputeHash([automatic]), RevisionHasher.ComputeHash([manual]));
    }

    [Fact]
    public void The_hash_is_culture_invariant()
    {
        // A comma decimal separator (hu-HU) must not produce a different hash than a dot.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = RevisionHasher.ComputeHash([Operation("a", finish: 12.5m)]);

            Thread.CurrentThread.CurrentCulture = new CultureInfo("hu-HU");
            var hungarian = RevisionHasher.ComputeHash([Operation("a", finish: 12.5m)]);

            Assert.Equal(invariant, hungarian);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void An_empty_revision_still_hashes()
    {
        Assert.False(string.IsNullOrWhiteSpace(RevisionHasher.ComputeHash([])));
    }

    [Fact]
    public void The_hash_is_lowercase_hex_sha256()
    {
        var hash = RevisionHasher.ComputeHash([Operation("a")]);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
