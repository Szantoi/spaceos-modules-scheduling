namespace SpaceOS.Modules.Scheduling.Domain.Standards;

/// <summary>
/// Why an imported operation standard could not be accepted as-is.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary mirrors the Doorstar import preflight (ADR-069 §4 names it as the
/// reference semantics), so the same defect is called the same thing on both sides of the
/// contract. A row is quarantined, never dropped: an operator has to be able to see what
/// arrived and why it was refused.
/// </para>
/// </remarks>
public enum StandardQuarantineReason
{
    /// <summary>The row carries no stable source key, so it cannot be reconciled on re-import.</summary>
    MissingSourceTaskKey,

    /// <summary>Two rows claim the same source key with different content.</summary>
    DuplicateSourceTaskKey,

    /// <summary>No unit time at all.</summary>
    MissingUnitTime,

    /// <summary>Unit time present but not positive.</summary>
    NonPositiveUnitTime,

    /// <summary>No workforce figure.</summary>
    MissingWorkforce,

    /// <summary>Workforce present but not positive.</summary>
    NonPositiveWorkforce,

    /// <summary>The relation code is not one of FS, SS, FF, SF.</summary>
    UnknownDependencyType,

    /// <summary>The partial-release threshold is outside the (0, 1] range.</summary>
    InvalidReleaseThreshold,

    /// <summary>The qualifier set does not identify a single standard.</summary>
    AmbiguousQualifierSet,
}
