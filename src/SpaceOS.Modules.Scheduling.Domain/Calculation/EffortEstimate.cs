using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Calculation;

/// <summary>Which effort input was missing or non-positive.</summary>
public enum EffortInputField
{
    /// <summary>The quantity to produce was absent or non-positive.</summary>
    Volume,

    /// <summary>The per-unit standard time was absent or non-positive.</summary>
    UnitMinutes,

    /// <summary>The required headcount was absent or non-positive.</summary>
    Workforce,
}

/// <summary>
/// Result of the effort calculation. An incomplete estimate is explicit rather than
/// silently zero: the legacy workbook produced a bare 0, whereas a consumer can now
/// see WHY and keep the row out of automatic planning.
/// </summary>
public sealed class EffortEstimate
{
    private EffortEstimate(
        decimal estimatedDurationMinutes,
        decimal estimatedLabourMinutes,
        int plannedWorkingDays,
        IReadOnlyList<EffortInputField> missingFields)
    {
        EstimatedDurationMinutes = estimatedDurationMinutes;
        EstimatedLabourMinutes = estimatedLabourMinutes;
        PlannedWorkingDays = plannedWorkingDays;
        MissingFields = missingFields;
    }

    /// <summary>Elapsed time on the shop floor: volume × unit minutes.</summary>
    public decimal EstimatedDurationMinutes { get; }

    /// <summary>Person-minutes: elapsed × workforce.</summary>
    public decimal EstimatedLabourMinutes { get; }

    /// <summary>ceil(elapsed / working minutes per day) + extra days.</summary>
    public int PlannedWorkingDays { get; }

    /// <summary>Inputs that were absent or non-positive; empty when complete.</summary>
    public IReadOnlyList<EffortInputField> MissingFields { get; }

    /// <summary>
    /// False when any input is missing. A false value must keep the operation out of
    /// automatic planning — it is a fail-safe flag, not a rejection.
    /// </summary>
    public bool EligibleForAutomaticPlanning => MissingFields.Count == 0;

    internal static EffortEstimate Complete(decimal durationMinutes, decimal labourMinutes, int plannedWorkingDays) =>
        new(durationMinutes, labourMinutes, plannedWorkingDays, []);

    internal static EffortEstimate Incomplete(int plannedWorkingDays, IEnumerable<EffortInputField> missingFields) =>
        new(0m, 0m, plannedWorkingDays, missingFields.ToArray());
}
