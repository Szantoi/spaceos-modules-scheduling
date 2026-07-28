namespace SpaceOS.Modules.Scheduling.Domain.Calculation;

/// <summary>
/// One operation's effort inputs, already normalised to minutes by the importing
/// adapter (ADR-069 §4 "Számítási szemantika").
/// </summary>
/// <remarks>
/// Every field is nullable on purpose: an incomplete standard is a normal, expected
/// state that must stay schedulable-but-flagged, never an exception. The
/// <see cref="EffortCalculator"/> reports the gaps instead of rejecting the row.
/// </remarks>
public sealed record EffortInput
{
    /// <summary>Quantity to produce. Must be &gt; 0 to be complete.</summary>
    public decimal? Volume { get; init; }

    /// <summary>Standard time for a single unit, in minutes. Must be &gt; 0 to be complete.</summary>
    public decimal? UnitMinutes { get; init; }

    /// <summary>
    /// People required by the operation. Drives labour demand only — it deliberately
    /// does NOT shorten elapsed time (legacy-faithful semantics, ADR-069 §4).
    /// </summary>
    public decimal? Workforce { get; init; }

    /// <summary>Explicit whole-day allowance added after the day rounding.</summary>
    public int? ExtraDays { get; init; }

    /// <summary>
    /// Schedulable minutes in one working day. Defaults to
    /// <see cref="EffortCalculator.DefaultWorkingMinutesPerDay"/> (an eight-hour day).
    /// </summary>
    public decimal? WorkingMinutesPerDay { get; init; }
}
