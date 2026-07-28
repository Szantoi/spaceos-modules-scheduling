using System;

namespace SpaceOS.Modules.Scheduling.Domain.Audit;

/// <summary>What happened to a schedule.</summary>
public enum SchedulingAuditAction
{
    /// <summary>A run was opened.</summary>
    RunOpened,

    /// <summary>A revision was calculated.</summary>
    RevisionProposed,

    /// <summary>A revision entered shadow evaluation.</summary>
    RevisionShadowed,

    /// <summary>A revision was published and became the active plan.</summary>
    RevisionPublished,

    /// <summary>A revision was superseded by a newer publication.</summary>
    RevisionSuperseded,

    /// <summary>A revision was discarded before publication.</summary>
    RevisionDiscarded,

    /// <summary>Resource time was held.</summary>
    CapacityHeld,

    /// <summary>A hold was confirmed.</summary>
    CapacityConfirmed,

    /// <summary>A hold was released.</summary>
    CapacityReleased,

    /// <summary>A hold expired without confirmation.</summary>
    CapacityExpired,

    /// <summary>A standard revision was imported and accepted.</summary>
    StandardAccepted,

    /// <summary>A standard revision was imported and quarantined.</summary>
    StandardQuarantined,

    /// <summary>A calendar revision was approved.</summary>
    CalendarApproved,
}

/// <summary>
/// One append-only record of a scheduling decision (ADR-069 §4).
/// </summary>
/// <remarks>
/// <para>
/// Append-only is the whole point: an audit trail that can be edited answers no question
/// worth asking. The type therefore exposes no mutator at all — a correction is a NEW entry,
/// never a rewrite of an old one.
/// </para>
/// <para>
/// The entry records WHO did WHAT to WHICH subject, plus a correlation id so one operator
/// action can be followed across the module boundary. It deliberately carries no business
/// payload: the aggregates hold the data, this holds the decision.
/// </para>
/// </remarks>
public sealed class SchedulingAuditEntry
{
    private SchedulingAuditEntry(
        Guid id,
        Guid tenantId,
        SchedulingAuditAction action,
        string subjectId,
        string actor,
        string? correlationId,
        string? note,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        Action = action;
        SubjectId = subjectId;
        Actor = actor;
        CorrelationId = correlationId;
        Note = note;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>Materialisation constructor for the persistence layer only.</summary>
    /// <remarks>
    /// EF cannot bind the real constructor, so it needs this one. Private, so application
    /// code still has to go through the factory method and its invariants.
    /// </remarks>
    private SchedulingAuditEntry()
    {
    }

    /// <summary>Entry identity.</summary>
    public Guid Id { get; }

    /// <summary>Owning tenant; mirrors the RLS predicate.</summary>
    public Guid TenantId { get; }

    /// <summary>What happened.</summary>
    public SchedulingAuditAction Action { get; }

    /// <summary>The aggregate the action was about (run, revision, reservation, standard).</summary>
    public string SubjectId { get; } = string.Empty;

    /// <summary>Who acted — a user or a named background worker, never an empty value.</summary>
    public string Actor { get; } = string.Empty;

    /// <summary>Correlation id of the originating request, when there was one.</summary>
    public string? CorrelationId { get; }

    /// <summary>Optional human-readable context.</summary>
    public string? Note { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>Records an entry.</summary>
    /// <exception cref="ArgumentException">Tenant, subject or actor is missing.</exception>
    public static SchedulingAuditEntry Record(
        Guid id,
        Guid tenantId,
        SchedulingAuditAction action,
        string subjectId,
        string actor,
        DateTimeOffset occurredAtUtc,
        string? correlationId = null,
        string? note = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("An audit entry must belong to a tenant.", nameof(tenantId));
        }
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new ArgumentException("An audit entry must name its subject.", nameof(subjectId));
        }
        if (string.IsNullOrWhiteSpace(actor))
        {
            // "Someone changed the plan" is not an audit trail. A background worker records
            // its own name rather than leaving this blank.
            throw new ArgumentException(
                "An audit entry must name its actor; use the worker's name for automated actions.",
                nameof(actor));
        }

        return new SchedulingAuditEntry(
            id, tenantId, action, subjectId, actor, correlationId, note, occurredAtUtc);
    }
}
