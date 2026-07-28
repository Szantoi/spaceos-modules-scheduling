using System;
using System.Collections.Generic;
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
    private static readonly KernelWorkScope TestScope = KernelWorkScope.Create(
        ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb")),
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    /// <summary>
    /// The hasher takes a revision's FULL content. These cases vary the operations only, so
    /// the edges and the calendar pins stay empty — deliberately, not by omission: a case
    /// that varied two things at once would not prove which one moved the hash.
    /// </summary>
    private static string Hash(IReadOnlyList<OperationPlan> operations) =>
        RevisionHasher.ComputeHash(operations, [], NoPins);

    private static readonly Dictionary<string, int> NoPins = new(StringComparer.Ordinal);

    private static OperationPlan Operation(
        string id, decimal start = 0m, decimal finish = 60m, string resource = "r1") =>
        new() { OperationId = id, Scope = TestScope, ResourceKey = resource, StartMinute = start, FinishMinute = finish };

    [Fact]
    public void Enumeration_order_does_not_change_the_hash()
    {
        var forward = Hash([Operation("a"), Operation("b"), Operation("c")]);
        var reversed = Hash([Operation("c"), Operation("b"), Operation("a")]);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void A_changed_minute_changes_the_hash()
    {
        Assert.NotEqual(
            Hash([Operation("a", finish: 60m)]),
            Hash([Operation("a", finish: 61m)]));
    }

    [Fact]
    public void Moving_an_operation_to_another_epic_changes_the_hash()
    {
        // The epic is part of what the plan SAYS about the work, not decoration: the same
        // times under a different epic is a different plan, and the consumer must see that.
        var moved = Operation("a") with
        {
            Scope = KernelWorkScope.Create(
                TestScope.Project,
                EpicRef.From(Guid.Parse("99999999-8888-4777-8666-555555555555")),
                TestScope.Task),
        };

        Assert.NotEqual(Hash([Operation("a")]), Hash([moved]));
    }

    [Fact]
    public void A_changed_resource_changes_the_hash()
    {
        Assert.NotEqual(
            Hash([Operation("a", resource: "r1")]),
            Hash([Operation("a", resource: "r2")]));
    }

    [Fact]
    public void Decimal_scale_does_not_change_the_hash()
    {
        // 60 and 60.00 are the same instant; a hash change here would look like a plan
        // change to the consumer and trigger a pointless re-review.
        Assert.Equal(
            Hash([Operation("a", finish: 60m)]),
            Hash([Operation("a", finish: 60.0000m)]));
    }

    [Fact]
    public void Field_boundaries_cannot_be_forged_by_a_crafted_id()
    {
        // Without length prefixes, "a|r1" as an id could reproduce the canonical form of
        // a different operation and collide deliberately.
        Assert.NotEqual(
            Hash([Operation("a", resource: "r1")]),
            Hash([Operation("a|r1", resource: string.Empty)]));
    }

    [Fact]
    public void The_automatic_planning_flag_is_part_of_the_identity()
    {
        var automatic = Operation("a");
        var manual = automatic with { AutomaticallyPlanned = false };

        Assert.NotEqual(Hash([automatic]), Hash([manual]));
    }

    [Fact]
    public void The_hash_is_culture_invariant()
    {
        // A comma decimal separator (hu-HU) must not produce a different hash than a dot.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = Hash([Operation("a", finish: 12.5m)]);

            Thread.CurrentThread.CurrentCulture = new CultureInfo("hu-HU");
            var hungarian = Hash([Operation("a", finish: 12.5m)]);

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
        Assert.False(string.IsNullOrWhiteSpace(Hash([])));
    }

    [Fact]
    public void The_hash_is_lowercase_hex_sha256()
    {
        var hash = Hash([Operation("a")]);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
