using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Resources;

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
    private readonly IReadOnlyList<CalendarException> _exceptions;

    /// <param name="zoneId">IANA zone id, e.g. <c>Europe/Budapest</c>.</param>
    /// <param name="shifts">Recurring shift definitions; at most one per weekday.</param>
    /// <param name="exceptions">
    /// Dated deviations: closures and maintenance remove working time, overtime adds it.
    /// Without them a calendar is a lie the moment the plant closes.
    /// </param>
    /// <exception cref="ArgumentException">The zone is unknown or a shift is malformed.</exception>
    public WorkingCalendar(
        string zoneId,
        IReadOnlyList<ShiftDefinition> shifts,
        IReadOnlyList<CalendarException>? exceptions = null)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        _exceptions = exceptions ?? [];

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
        var ranges = ShiftRangesOn(date);
        ranges = ApplyExceptions(date, ranges);

        return [.. ranges
            .OrderBy(range => range.Start)
            .Select(range => Materialise(date, range.Start, range.End))];
    }

    /// <summary>The shift's local ranges for a date, breaks already removed.</summary>
    private List<(LocalTime Start, LocalTime End)> ShiftRangesOn(LocalDate date)
    {
        var shift = _shifts.FirstOrDefault(candidate => candidate.Weekday == date.DayOfWeek);
        if (shift is null) { return []; }

        var ranges = new List<(LocalTime Start, LocalTime End)>();
        var cursor = shift.Span.Start;

        foreach (var pause in shift.Breaks.OrderBy(pause => pause.Start))
        {
            if (pause.Start > cursor) { ranges.Add((cursor, pause.Start)); }
            cursor = pause.End;
        }

        if (cursor < shift.Span.End) { ranges.Add((cursor, shift.Span.End)); }
        return ranges;
    }

    /// <summary>
    /// Applies the dated exceptions: removals cut time out, overtime adds it.
    /// </summary>
    /// <remarks>
    /// Removals are applied BEFORE overtime on purpose. A closure means the resource is not
    /// available at all that day, and approved overtime is a deliberate, explicit statement
    /// that someone WILL work — so it must survive the closure rather than be cancelled by it.
    /// </remarks>
    private List<(LocalTime Start, LocalTime End)> ApplyExceptions(
        LocalDate date,
        List<(LocalTime Start, LocalTime End)> ranges)
    {
        var forDate = _exceptions.Where(item => item.Date == new DateOnly(date.Year, date.Month, date.Day)).ToArray();
        if (forDate.Length == 0) { return ranges; }

        foreach (var removal in forDate.Where(item => item.RemovesTime))
        {
            var cut = removal.Span is null
                ? (Start: new LocalTime(0, 0), End: LocalTime.MaxValue)
                : (Start: FromMinutes(removal.Span.StartMinuteOfDay), End: FromMinutes(removal.Span.EndMinuteOfDay));

            ranges = ranges.SelectMany(range => Subtract(range, cut)).ToList();
        }

        foreach (var overtime in forDate.Where(item => item.Kind == CalendarExceptionKind.Overtime))
        {
            ranges.Add((FromMinutes(overtime.Span!.StartMinuteOfDay), FromMinutes(overtime.Span.EndMinuteOfDay)));
        }

        return Merge(ranges);
    }

    private static LocalTime FromMinutes(int minuteOfDay) =>
        minuteOfDay >= 1440 ? LocalTime.MaxValue : new LocalTime(minuteOfDay / 60, minuteOfDay % 60);

    private static IEnumerable<(LocalTime Start, LocalTime End)> Subtract(
        (LocalTime Start, LocalTime End) range,
        (LocalTime Start, LocalTime End) cut)
    {
        if (cut.End <= range.Start || cut.Start >= range.End)
        {
            yield return range;
            yield break;
        }

        if (cut.Start > range.Start) { yield return (range.Start, cut.Start); }
        if (cut.End < range.End) { yield return (cut.End, range.End); }
    }

    /// <summary>Merges overlapping or touching ranges so no minute is counted twice.</summary>
    private static List<(LocalTime Start, LocalTime End)> Merge(List<(LocalTime Start, LocalTime End)> ranges)
    {
        var merged = new List<(LocalTime Start, LocalTime End)>();
        foreach (var range in ranges.Where(item => item.End > item.Start).OrderBy(item => item.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Start, range.End > previous.End ? range.End : previous.End);
                continue;
            }
            merged.Add(range);
        }
        return merged;
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
