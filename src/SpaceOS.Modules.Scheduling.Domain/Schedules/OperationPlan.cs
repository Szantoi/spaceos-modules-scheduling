using System;
using System.Collections.Generic;

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

    /// <summary>
    /// The Kernel work this operation schedules: project → epic → task.
    /// </summary>
    /// <remarks>
    /// Required and complete, not optional: an operation that cannot be traced back to the
    /// work it serves is unusable in the hierarchy the customer actually navigates. The scope
    /// is identity only — it is never evidence that a caller may see or change that work.
    /// </remarks>
    public required KernelWorkScope Scope { get; init; }

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

    /// <summary>
    /// Revision number of the operation standard the times were derived from; null when the
    /// operation was placed without one.
    /// </summary>
    /// <remarks>
    /// Part of the plan's identity, not decoration: the same minutes derived from a different
    /// norm revision are a different claim about the work, and a consumer comparing two
    /// proposals must be able to see that from the hash alone.
    /// </remarks>
    public int? StandardRevision { get; init; }

    /// <summary>
    /// Opaque upstream provenance (order key + revision, calculation revision, process-row
    /// revision), stored and reflected back unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The platform NEVER resolves these values (PLAN-03 M3 contract input). They are the
    /// consumer's lineage proof, and interpreting them here would quietly make Doorstar's
    /// internal identifiers part of this module's domain — exactly the coupling ADR-067
    /// exists to prevent.
    /// </para>
    /// <para>
    /// Opaque is not the same as unbounded: keys and values are length-capped and the block
    /// is count-capped, because an un-validated pass-through map is a storage channel anyone
    /// holding a token could fill.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> SourceRevisions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Maximum number of provenance entries carried by one operation.</summary>
    public const int MaxSourceRevisionEntries = 32;

    /// <summary>Maximum length of a provenance key or value.</summary>
    public const int MaxSourceRevisionLength = 256;

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

        if (StandardRevision is <= 0)
        {
            throw new ArgumentException(
                $"Operation '{OperationId}' carries standard revision {StandardRevision}; revisions start at 1.",
                nameof(StandardRevision));
        }

        ValidateSourceRevisions();
    }

    private void ValidateSourceRevisions()
    {
        if (SourceRevisions.Count > MaxSourceRevisionEntries)
        {
            throw new ArgumentException(
                $"Operation '{OperationId}' carries {SourceRevisions.Count} provenance entries; " +
                $"at most {MaxSourceRevisionEntries} are accepted.", nameof(SourceRevisions));
        }

        foreach (var (key, value) in SourceRevisions)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    $"Operation '{OperationId}' carries a provenance entry with no key.", nameof(SourceRevisions));
            }

            // A null/blank value would round-trip as "the consumer told us nothing" while
            // still occupying a lineage slot -- worse than an absent entry, because it looks
            // like an answer.
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Operation '{OperationId}' carries provenance key '{key}' with a blank value.",
                    nameof(SourceRevisions));
            }

            if (key.Length > MaxSourceRevisionLength || value.Length > MaxSourceRevisionLength)
            {
                throw new ArgumentException(
                    $"Operation '{OperationId}' provenance entry '{key}' exceeds " +
                    $"{MaxSourceRevisionLength} characters.", nameof(SourceRevisions));
            }
        }
    }
}
