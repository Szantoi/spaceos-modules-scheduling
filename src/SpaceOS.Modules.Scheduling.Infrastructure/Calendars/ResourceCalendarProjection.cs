using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Resources;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>
/// Turns a stored calendar revision into the working calendar the timeline is built from.
/// </summary>
/// <remarks>
/// The aggregate keeps shifts as minutes-since-midnight so the domain stays free of a time
/// library (ADR-070 D2); this layer is where those minutes become local times and, through the
/// zone, instants. Keeping the conversion in one place matters because a plan's dates are only
/// reproducible if every reader turns the same revision into the same calendar.
/// </remarks>
public static class ResourceCalendarProjection
{
    /// <summary>Builds the working calendar of one stored revision.</summary>
    /// <exception cref="ArgumentException">The revision carries an unknown IANA zone.</exception>
    public static WorkingCalendar ToWorkingCalendar(this ResourceCalendarRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        var shifts = revision.Shifts
            .OrderBy(shift => shift.IsoWeekday)
            .Select(shift => new ShiftDefinition
            {
                Weekday = (IsoDayOfWeek)shift.IsoWeekday,
                Span = new LocalTimeRange(ToLocalTime(shift.Shift.StartMinuteOfDay), ToLocalTime(shift.Shift.EndMinuteOfDay)),
                Breaks = [.. shift.Breaks
                    .OrderBy(pause => pause.StartMinuteOfDay)
                    .Select(pause => new LocalTimeRange(
                        ToLocalTime(pause.StartMinuteOfDay), ToLocalTime(pause.EndMinuteOfDay)))],
            })
            .ToArray();

        return new WorkingCalendar(revision.TimeZoneId, shifts, [.. revision.Exceptions]);
    }

    /// <summary>Minutes since midnight as a local time; 1440 is the end of the day.</summary>
    private static LocalTime ToLocalTime(int minuteOfDay) =>
        minuteOfDay >= 1440 ? LocalTime.MaxValue : new LocalTime(minuteOfDay / 60, minuteOfDay % 60);
}
