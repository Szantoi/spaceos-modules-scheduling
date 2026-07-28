using System;

namespace SpaceOS.Modules.Scheduling.Domain.Audit;

/// <summary>
/// One integration event waiting to leave the module (transactional outbox, ADR-069 §4).
/// </summary>
/// <remarks>
/// <para>
/// The message is written in the SAME transaction as the state change it describes. That is
/// the entire value of the pattern: without it a publish can succeed while the transaction
/// rolls back (an event about something that never happened), or the transaction can commit
/// while the publish fails (a change nobody downstream hears about).
/// </para>
/// <para>
/// Delivery is at-least-once, so consumers must be idempotent. Dispatching twice is treated
/// as a bug here — a message already dispatched refuses a second dispatch rather than
/// silently resetting its own history.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage(
        Guid id,
        Guid tenantId,
        string messageType,
        string payload,
        DateTimeOffset occurredAtUtc,
        string? correlationId)
    {
        Id = id;
        TenantId = tenantId;
        MessageType = messageType;
        Payload = payload;
        OccurredAtUtc = occurredAtUtc;
        CorrelationId = correlationId;
    }

    /// <summary>Materialisation constructor for the persistence layer only.</summary>
    /// <remarks>
    /// EF cannot bind the real constructor, so it needs this one. Private, so application
    /// code still has to go through the factory method and its invariants.
    /// </remarks>
    private OutboxMessage()
    {
    }

    /// <summary>Message identity; doubles as the idempotency key for consumers.</summary>
    public Guid Id { get; }

    /// <summary>Owning tenant; mirrors the RLS predicate.</summary>
    public Guid TenantId { get; }

    /// <summary>Wire type name of the event.</summary>
    public string MessageType { get; } = string.Empty;

    /// <summary>Serialised event body.</summary>
    public string Payload { get; } = string.Empty;

    /// <summary>When the described change happened.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Correlation id of the originating request, when there was one.</summary>
    public string? CorrelationId { get; }

    /// <summary>When it was successfully dispatched; null while pending.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    /// <summary>How many dispatch attempts failed so far.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Why the last attempt failed; null when none did.</summary>
    public string? LastError { get; private set; }

    /// <summary>True while the message still needs to be delivered.</summary>
    public bool IsPending => DispatchedAtUtc is null;

    /// <summary>Enqueues a message.</summary>
    /// <exception cref="ArgumentException">Tenant, type or payload is missing.</exception>
    public static OutboxMessage Enqueue(
        Guid id,
        Guid tenantId,
        string messageType,
        string payload,
        DateTimeOffset occurredAtUtc,
        string? correlationId = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("An outbox message must belong to a tenant.", nameof(tenantId));
        }
        if (string.IsNullOrWhiteSpace(messageType))
        {
            throw new ArgumentException("An outbox message must carry its type.", nameof(messageType));
        }
        if (string.IsNullOrWhiteSpace(payload))
        {
            // An empty payload would be dispatched happily and tell the consumer nothing.
            throw new ArgumentException("An outbox message must carry a payload.", nameof(payload));
        }

        return new OutboxMessage(id, tenantId, messageType, payload, occurredAtUtc, correlationId);
    }

    /// <summary>Marks the message delivered.</summary>
    /// <exception cref="InvalidOperationException">Already dispatched.</exception>
    public void MarkDispatched(DateTimeOffset dispatchedAtUtc)
    {
        if (DispatchedAtUtc is not null)
        {
            throw new InvalidOperationException(
                $"Message {Id} was already dispatched at {DispatchedAtUtc:O}; dispatching it " +
                "again would hide a double-send instead of surfacing it.");
        }

        DispatchedAtUtc = dispatchedAtUtc;
    }

    /// <summary>Records a failed attempt, keeping the message pending.</summary>
    /// <exception cref="InvalidOperationException">The message was already dispatched.</exception>
    public void RecordFailure(string error)
    {
        if (DispatchedAtUtc is not null)
        {
            throw new InvalidOperationException($"Message {Id} was already dispatched; it cannot fail afterwards.");
        }

        FailedAttempts += 1;
        LastError = error;
    }
}
