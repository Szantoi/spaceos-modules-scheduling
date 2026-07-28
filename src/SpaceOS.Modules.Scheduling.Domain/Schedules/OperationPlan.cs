using System;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// One scheduled operation inside a revision: what runs where, and between which minutes.
/// </summary>
/// <remarks>
/// Times are on the normalised minute timeline, not wall-clock dates. The calendar layer
/// (M4) converts to and from local time with the tenant's IANA zone; keeping the domain on
/// a numeric timeline means a DST transition cannot silently shift a stored plan.
/// </remarks>
public sealed record OperationPlan
{
    /// <summary>Stable operation identifier, unique inside the revision.</summary>
    public required string OperationId { get; init; }

    /// <summary>Resource the operation is assigned to.</summary>
    public required string ResourceKey { get; init; }

    /// <summary>Planned start on the normalised minute timeline.</summary>
    public required decimal StartMinute { get; init; }

    /// <summary>Planned finish on the normalised minute timeline.</summary>
    public required decimal FinishMinute { get; init; }

    /// <summary>
    /// False when the operation is only a placeholder — for example its standard was
    /// incomplete (see <c>EffortEstimate.EligibleForAutomaticPlanning</c>). Such an
    /// operation is visible in the plan but was not placed automatically.
    /// </summary>
    public bool AutomaticallyPlanned { get; init; } = true;

    /// <summary>Validates the invariants an operation must satisfy to enter a revision.</summary>
    /// <exception cref="ArgumentException">An id is blank or the interval is inverted.</exception>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(OperationId))
        {
            throw new ArgumentException("A planned operation needs a non-blank operation id.", nameof(OperationId));
        }

        if (string.IsNullOrWhiteSpace(ResourceKey))
        {
            throw new ArgumentException(
                $"Operation '{OperationId}' needs a non-blank resource key.", nameof(ResourceKey));
        }

        // Zero-length is allowed (a milestone), inverted is not.
        if (FinishMinute < StartMinute)
        {
            throw new ArgumentException(
                $"Operation '{OperationId}' finishes ({FinishMinute}) before it starts ({StartMinute}).",
                nameof(FinishMinute));
        }
    }
}
