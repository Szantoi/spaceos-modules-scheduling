using System;
using System.Collections.Generic;
using NodaTime;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>A local time range inside a working day (start inclusive, end exclusive).</summary>
/// <param name="Start">Local start time.</param>
/// <param name="End">Local end time; must be later than <paramref name="Start"/>.</param>
public readonly record struct LocalTimeRange(LocalTime Start, LocalTime End)
{
    /// <summary>Length ignoring any calendar effects.</summary>
    public Duration NominalLength => Period.Between(Start, End).ToDuration();
}

/// <summary>
/// A recurring shift on one weekday: a local span minus its breaks.
/// </summary>
/// <remarks>
/// Local time, deliberately: a shift is "07:00 to 16:00 in the workshop", not an instant.
/// The zone (and therefore DST) is applied when the shift is projected onto a date — see
/// <see cref="WorkingCalendar"/>.
/// </remarks>
public sealed record ShiftDefinition
{
    /// <summary>Day of week the shift recurs on.</summary>
    public required IsoDayOfWeek Weekday { get; init; }

    /// <summary>The shift span in local time.</summary>
    public required LocalTimeRange Span { get; init; }

    /// <summary>Unpaid or non-schedulable interruptions inside the span.</summary>
    public IReadOnlyList<LocalTimeRange> Breaks { get; init; } = [];

    /// <summary>Validates the shape a shift must have to be schedulable.</summary>
    /// <exception cref="ArgumentException">
    /// The span is empty or inverted, a break falls outside it, or two breaks overlap.
    /// </exception>
    public void Validate()
    {
        if (Span.End <= Span.Start)
        {
            throw new ArgumentException($"{Weekday}: the shift span must end after it starts.", nameof(Span));
        }

        foreach (var pause in Breaks)
        {
            if (pause.End <= pause.Start)
            {
                throw new ArgumentException($"{Weekday}: a break must end after it starts.", nameof(Breaks));
            }
            if (pause.Start < Span.Start || pause.End > Span.End)
            {
                throw new ArgumentException($"{Weekday}: a break falls outside the shift span.", nameof(Breaks));
            }
        }

        for (var index = 0; index < Breaks.Count; index++)
        {
            for (var other = index + 1; other < Breaks.Count; other++)
            {
                if (Breaks[index].Start < Breaks[other].End && Breaks[other].Start < Breaks[index].End)
                {
                    throw new ArgumentException($"{Weekday}: two breaks overlap.", nameof(Breaks));
                }
            }
        }
    }
}
