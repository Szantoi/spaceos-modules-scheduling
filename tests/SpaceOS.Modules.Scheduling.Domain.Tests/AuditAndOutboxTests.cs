using System;
using System.Linq;
using System.Reflection;
using SpaceOS.Modules.Scheduling.Domain.Audit;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Append-only audit and the transactional outbox (ADR-069 §4).
/// </summary>
public sealed class AuditAndOutboxTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    private static SchedulingAuditEntry Entry(string actor = "anna.kovacs") =>
        SchedulingAuditEntry.Record(
            Guid.NewGuid(), TenantId, SchedulingAuditAction.RevisionPublished,
            "run-1/revision-2", actor, Now, correlationId: "corr-1", note: "published from shadow");

    private static OutboxMessage Message() =>
        OutboxMessage.Enqueue(
            Guid.NewGuid(), TenantId, "scheduling.revision-published", "{\"revision\":2}", Now, "corr-1");

    [Fact]
    public void An_audit_entry_records_who_did_what_to_which_subject()
    {
        var entry = Entry();

        Assert.Equal(SchedulingAuditAction.RevisionPublished, entry.Action);
        Assert.Equal("run-1/revision-2", entry.SubjectId);
        Assert.Equal("anna.kovacs", entry.Actor);
        Assert.Equal("corr-1", entry.CorrelationId);
    }

    [Fact]
    public void An_audit_entry_exposes_no_way_to_change_it()
    {
        // Append-only is the whole point: an editable trail answers no question worth asking.
        // Asserting on the API surface rather than on behaviour, because there is deliberately
        // no behaviour to assert.
        var settable = typeof(SchedulingAuditEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .ToArray();

        Assert.True(settable.Length == 0, $"These audit properties are writable: {string.Join(", ", settable)}");

        // IsSpecialName filters out the property getters, which are methods too — the first
        // version of this assertion failed on its own accessors rather than on a real mutator.
        var mutators = typeof(SchedulingAuditEntry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();

        Assert.True(mutators.Length == 0, $"These audit methods could mutate state: {string.Join(", ", mutators)}");
    }

    [Fact]
    public void An_audit_entry_must_name_its_actor()
    {
        // "Someone changed the plan" is not an audit trail; a worker records its own name.
        var exception = Assert.Throws<ArgumentException>(() => Entry(actor: "   "));

        Assert.Contains("actor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_audit_entry_must_belong_to_a_tenant_and_name_a_subject()
    {
        Assert.Throws<ArgumentException>(() => SchedulingAuditEntry.Record(
            Guid.NewGuid(), Guid.Empty, SchedulingAuditAction.RunOpened, "run-1", "worker", Now));

        Assert.Throws<ArgumentException>(() => SchedulingAuditEntry.Record(
            Guid.NewGuid(), TenantId, SchedulingAuditAction.RunOpened, " ", "worker", Now));
    }

    [Fact]
    public void A_new_outbox_message_is_pending()
    {
        var message = Message();

        Assert.True(message.IsPending);
        Assert.Null(message.DispatchedAtUtc);
        Assert.Equal(0, message.FailedAttempts);
    }

    [Fact]
    public void A_dispatched_message_stops_being_pending()
    {
        var message = Message();

        message.MarkDispatched(Now.AddSeconds(5));

        Assert.False(message.IsPending);
        Assert.Equal(Now.AddSeconds(5), message.DispatchedAtUtc);
    }

    [Fact]
    public void A_message_refuses_to_be_dispatched_twice()
    {
        // Delivery is at-least-once, so a double send must SURFACE rather than be absorbed:
        // silently resetting the history would hide a broken dispatcher.
        var message = Message();
        message.MarkDispatched(Now);

        var exception = Assert.Throws<InvalidOperationException>(() => message.MarkDispatched(Now.AddMinutes(1)));

        Assert.Contains("already dispatched", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_attempt_keeps_the_message_pending_and_counts()
    {
        var message = Message();

        message.RecordFailure("connection refused");
        message.RecordFailure("timeout");

        Assert.True(message.IsPending);
        Assert.Equal(2, message.FailedAttempts);
        Assert.Equal("timeout", message.LastError);
    }

    [Fact]
    public void A_dispatched_message_cannot_fail_afterwards()
    {
        var message = Message();
        message.MarkDispatched(Now);

        Assert.Throws<InvalidOperationException>(() => message.RecordFailure("late error"));
    }

    [Fact]
    public void A_message_must_carry_a_type_and_a_payload()
    {
        // An empty payload would dispatch happily and tell the consumer nothing.
        Assert.Throws<ArgumentException>(() => OutboxMessage.Enqueue(
            Guid.NewGuid(), TenantId, "  ", "{}", Now));

        Assert.Throws<ArgumentException>(() => OutboxMessage.Enqueue(
            Guid.NewGuid(), TenantId, "scheduling.event", "   ", Now));
    }

    [Fact]
    public void A_message_must_belong_to_a_tenant()
    {
        Assert.Throws<ArgumentException>(() => OutboxMessage.Enqueue(
            Guid.NewGuid(), Guid.Empty, "scheduling.event", "{}", Now));
    }
}
