using System;
using System.Collections.Generic;

namespace SpaceOS.Modules.Scheduling.Domain.Calculation;

/// <summary>
/// Pure, IO-free effort calculation — the normative core of ADR-069 §4.
/// </summary>
/// <remarks>
/// <para>
///   elapsed = volume × unitMinutes<br/>
///   labour  = elapsed × workforce<br/>
///   days    = ceil(elapsed / workingMinutesPerDay) + extraDays
/// </para>
/// <para>
/// The workforce multiplies labour demand only; it never compresses elapsed time.
/// That is deliberate legacy-faithful behaviour, pinned by the compatibility vectors.
/// </para>
/// <para>
/// The distinction between a FLAG and a THROW matters: a missing standard is a data
/// state the shop floor genuinely has, so it produces an incomplete estimate. A
/// negative extra-day count or a non-positive working day is a caller/programming
/// error and throws.
/// </para>
/// </remarks>
public static class EffortCalculator
{
    /// <summary>The legacy eight-hour working day, in minutes.</summary>
    public const decimal DefaultWorkingMinutesPerDay = 8 * 60;

    /// <summary>Calculates elapsed time, labour demand and planned working days.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Working minutes per day is not positive, or extra days is negative.
    /// </exception>
    public static EffortEstimate Calculate(EffortInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var workingMinutesPerDay = input.WorkingMinutesPerDay ?? DefaultWorkingMinutesPerDay;
        if (workingMinutesPerDay <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), workingMinutesPerDay, "WorkingMinutesPerDay must be positive.");
        }

        var extraDays = input.ExtraDays ?? 0;
        if (extraDays < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input), extraDays, "ExtraDays must not be negative.");
        }

        var missingFields = new List<EffortInputField>();
        if (!IsPositive(input.Volume)) { missingFields.Add(EffortInputField.Volume); }
        if (!IsPositive(input.UnitMinutes)) { missingFields.Add(EffortInputField.UnitMinutes); }
        if (!IsPositive(input.Workforce)) { missingFields.Add(EffortInputField.Workforce); }

        if (missingFields.Count > 0)
        {
            // Zero duration, but the extra-day allowance still stands: it is an explicit
            // operator statement, independent of the missing standard.
            return EffortEstimate.Incomplete(extraDays, missingFields);
        }

        var durationMinutes = input.Volume!.Value * input.UnitMinutes!.Value;
        var labourMinutes = durationMinutes * input.Workforce!.Value;
        var plannedWorkingDays = checked((int)Math.Ceiling(durationMinutes / workingMinutesPerDay) + extraDays);

        return EffortEstimate.Complete(durationMinutes, labourMinutes, plannedWorkingDays);
    }

    private static bool IsPositive(decimal? value) => value.HasValue && value.Value > 0m;
}
