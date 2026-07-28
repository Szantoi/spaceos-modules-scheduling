using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>A schedulable span on the absolute timeline.</summary>
/// <param name="Start">Inclusive start instant (UTC).</param>
/// <param name="End">Exclusive end instant (UTC).</param>
public readonly record struct WorkingInterval(Instant Start, Instant End)
{
    /// <summary>Length of the interval in real elapsed time.</summary>
    public Duration Length => End - Start;
}

/// <summary>
/// Projects local shift definitions onto real instants for a tenant's IANA time zone
/// (ADR-069 §5: storage in UTC, shifts interpreted locally, DST handled by the core).
/// </summary>
/// <remarks>
/// <para>
/// This is where the DST correctness lives, and why <c>DateTimeOffset</c> is not enough:
/// an offset knows nothing about the RULES that change it. On the spring transition a local
/// time can be missing entirely, and on the autumn one it exists twice — with a bare offset
/// both cases silently produce a wrong instant, and the day's net minutes come out an hour
/// off exactly in the week nobody is checking.
/// </para>
/// <para>
/// NodaTime types stay INSIDE this layer (ADR-070 D2): the wire format is an ISO-8601 UTC
/// string plus the IANA zone id, so the Doorstar client generation never sees them.
/// </para>
/// </remarks>
public sealed class WorkingCalendar
{
    private readonly DateTimeZone _zone;
    private readonly IReadOnlyList<ShiftDefinition> _shifts;

    /// <param name="zoneId">IANA zone id, e.g. <c>Europe/Budapest</c>.</param>
    /// <param name="shifts">Recurring shift definitions; at most one per weekday.</param>
    /// <exception cref="ArgumentException">The zone is unknown or a shift is malformed.</exception>
    public WorkingCalendar(string zoneId, IReadOnlyList<ShiftDefinition> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);

        _zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId)
            ?? throw new ArgumentException($"Unknown IANA time zone: '{zoneId}'.", nameof(zoneId));

        var duplicate = shifts.GroupBy(shift => shift.Weekday).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"More than one shift defined for {duplicate.Key}.", nameof(shifts));
        }

        foreach (var shift in shifts)
        {
            shift.Validate();
        }

        _shifts = shifts;
    }

    /// <summary>The calendar's IANA zone id, as it travels on the wire.</summary>
    public string ZoneId => _zone.Id;

    /// <summary>
    /// The schedulable intervals of one local date, breaks removed, on the absolute timeline.
    /// </summary>
    /// <returns>Empty when the date has no shift (weekend, or a weekday without a definition).</returns>
    public IReadOnlyList<WorkingInterval> IntervalsOn(LocalDate date)
    {
        var shift = _shifts.FirstOrDefault(candidate => candidate.Weekday == date.DayOfWeek);
        if (shift is null) { return []; }

        var intervals = new List<WorkingInterval>();
        var cursor = shift.Span.Start;

        foreach (var pause in shift.Breaks.OrderBy(pause => pause.Start))
        {
            if (pause.Start > cursor)
            {
                intervals.Add(Materialise(date, cursor, pause.Start));
            }
            cursor = pause.End;
        }

        if (cursor < shift.Span.End)
        {
            intervals.Add(Materialise(date, cursor, shift.Span.End));
        }

        return intervals;
    }

    /// <summary>
    /// Net schedulable minutes of one local date — real elapsed minutes, not clock arithmetic.
    /// </summary>
    /// <remarks>
    /// On a DST day this deliberately differs from the nominal shift length: an hour that
    /// does not exist cannot be worked, and an hour lived twice is worked twice.
    /// </remarks>
    public decimal NetMinutesOn(LocalDate date) =>
        IntervalsOn(date).Aggregate(0m, (total, interval) => total + (decimal)interval.Length.TotalMinutes);

    private WorkingInterval Materialise(LocalDate date, LocalTime start, LocalTime end)
    {
        // InZoneLeniently: a local time inside a spring gap is pushed forward by the gap
        // length, and an ambiguous autumn time resolves to the FIRST (earlier) occurrence.
        // Both are defined, documented behaviours -- the alternative (InZoneStrictly) would
        // throw and take a whole day's schedule down over a once-a-year clock change.
        var startInstant = (date + start).InZoneLeniently(_zone).ToInstant();
        var endInstant = (date + end).InZoneLeniently(_zone).ToInstant();
        return new WorkingInterval(startInstant, endInstant);
    }
}
