using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>
/// The bridge between the solver's working-minute axis and the absolute timeline.
/// </summary>
/// <remarks>
/// <para>
/// The solver decides in WORKING minutes — that is the unit an effort calculation produces,
/// and the unit a planner reasons in ("this takes twelve hours of machine time"). A calendar
/// turns that into dates: work stops at the end of a shift and resumes at the start of the
/// next one, so twelve working hours can span three calendar days. Business decision
/// (Gábor, 2026-07-29): <b>every operation may span non-working time</b>; the duration is
/// working time, not wall clock.
/// </para>
/// <para>
/// The intervals are materialised ONCE for a horizon and indexed by a running total, so a
/// lookup is a binary search rather than a walk over the calendar. A plan with a few thousand
/// operations would otherwise re-derive the same shifts for every single one.
/// </para>
/// <para>
/// DST correctness is inherited from <see cref="WorkingCalendar"/>, not re-implemented: the
/// intervals already arrive as instants, so an hour that does not exist is simply absent from
/// the axis, and an hour lived twice appears twice. That is the behaviour a planner expects —
/// the shift is worked, whatever the clock did.
/// </para>
/// </remarks>
public sealed class WorkingTimeline
{
    private readonly IReadOnlyList<WorkingInterval> _intervals;

    // Working minutes elapsed BEFORE each interval; _cumulative[i] is the axis position of
    // _intervals[i].Start. One extra entry at the end holds the total.
    private readonly decimal[] _cumulative;

    /// <param name="calendar">The resource's working calendar.</param>
    /// <param name="origin">Axis zero: working minute 0 is the first working instant at or after it.</param>
    /// <param name="horizon">How far ahead to materialise intervals.</param>
    /// <exception cref="ArgumentOutOfRangeException">The horizon is not positive.</exception>
    public WorkingTimeline(WorkingCalendar calendar, Instant origin, Duration horizon)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (horizon <= Duration.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(horizon), horizon, "The horizon must cover some time.");
        }

        Calendar = calendar;
        Origin = origin;
        End = origin + horizon;
        _intervals = Materialise(calendar, origin, End);

        _cumulative = new decimal[_intervals.Count + 1];
        for (var index = 0; index < _intervals.Count; index++)
        {
            _cumulative[index + 1] = _cumulative[index] + Minutes(_intervals[index]);
        }
    }

    /// <summary>The calendar this axis was built from.</summary>
    /// <remarks>
    /// Exposed so callers that need a calendar RULE — the partial-release threshold above all —
    /// can use the one authority for it (<see cref="WorkingTimeReleaseCalculator"/>) instead of
    /// re-deriving the rule from the axis.
    /// </remarks>
    public WorkingCalendar Calendar { get; }

    /// <summary>Axis zero on the absolute timeline.</summary>
    public Instant Origin { get; }

    /// <summary>End of the materialised horizon.</summary>
    public Instant End { get; }

    /// <summary>Total working minutes available in the horizon.</summary>
    public decimal TotalWorkingMinutes => _cumulative[^1];

    /// <summary>
    /// The instant at which work STARTS after <paramref name="workingMinutes"/> have elapsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Minute zero is the START of the first interval, not the origin: an operation planned for
    /// "minute 0" starts when the resource actually starts working, not in the middle of the
    /// night the plan happened to be computed in.
    /// </para>
    /// <para>
    /// <b>On an interval boundary this deliberately returns the NEXT interval's start</b>, and
    /// <see cref="EndAtWorkingMinute"/> returns the previous interval's end. The same position
    /// genuinely means two different instants depending on the question: after a full 8-hour
    /// shift, work FINISHED at 16:00 and the next work can only BEGIN at 08:00 tomorrow.
    /// Collapsing the two would either promise a start when nobody is there, or report a
    /// finish later than the work actually took.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A negative position was asked for.</exception>
    /// <exception cref="InvalidOperationException">
    /// The position lies beyond the materialised horizon. Reported rather than clamped: a
    /// silently truncated plan would show a date the resource never reaches.
    /// </exception>
    public Instant AtWorkingMinute(decimal workingMinutes) => Resolve(workingMinutes, startingWork: true);

    /// <summary>
    /// The instant at which the <paramref name="workingMinutes"/>-th working minute is COMPLETE.
    /// </summary>
    /// <remarks>See <see cref="AtWorkingMinute"/> for why the two differ on a boundary.</remarks>
    public Instant EndAtWorkingMinute(decimal workingMinutes) => Resolve(workingMinutes, startingWork: false);

    private Instant Resolve(decimal workingMinutes, bool startingWork)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workingMinutes);

        if (_intervals.Count == 0)
        {
            throw new InvalidOperationException(
                "The calendar has no working time in the requested horizon, so no plan can be " +
                "placed on it. Check the resource's calendar revision and the horizon.");
        }

        if (workingMinutes > TotalWorkingMinutes)
        {
            throw new InvalidOperationException(
                $"The plan needs working minute {workingMinutes}, but the horizon holds only " +
                $"{TotalWorkingMinutes}. Extend the horizon rather than trusting a truncated plan.");
        }

        var index = IndexOf(workingMinutes, startingWork);
        var into = workingMinutes - _cumulative[index];

        return _intervals[index].Start + Duration.FromMinutes((double)into);
    }

    /// <summary>Where an instant sits on the working-minute axis.</summary>
    /// <remarks>
    /// An instant outside working time reports the position of the last working minute before
    /// it — the resource has produced exactly that much by then, which is what every downstream
    /// question ("how far along is this?") actually asks.
    /// </remarks>
    public decimal PositionOf(Instant instant) => WorkingMinutesBetween(Origin, instant);

    /// <summary>The instant reached by working <paramref name="minutes"/> more from an instant.</summary>
    public Instant AddWorkingMinutes(Instant from, decimal minutes) =>
        AtWorkingMinute(PositionOf(from) + minutes);

    /// <summary>Working minutes between two instants, ignoring everything outside the intervals.</summary>
    /// <exception cref="ArgumentException">The interval is inverted.</exception>
    public decimal WorkingMinutesBetween(Instant from, Instant to)
    {
        if (to < from)
        {
            throw new ArgumentException("The interval is inverted.", nameof(to));
        }

        return _intervals.Aggregate(0m, (total, interval) =>
        {
            var start = Instant.Max(interval.Start, from);
            var end = Instant.Min(interval.End, to);
            return end > start ? total + (decimal)(end - start).TotalMinutes : total;
        });
    }

    /// <summary>The interval index the position falls into.</summary>
    /// <param name="workingMinutes">Position on the axis.</param>
    /// <param name="startingWork">
    /// True when the position is a START (a boundary belongs to the interval that BEGINS there),
    /// false when it is a FINISH (the boundary belongs to the interval that ENDS there).
    /// </param>
    private int IndexOf(decimal workingMinutes, bool startingWork)
    {
        // Binary search over the running totals: find the last interval whose start total is at
        // (or below) the position. The comparison differs by one strictness step, and that is
        // exactly the start/finish distinction.
        var low = 0;
        var high = _intervals.Count - 1;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var isCandidate = startingWork
                ? _cumulative[middle] <= workingMinutes
                : _cumulative[middle] < workingMinutes;

            if (isCandidate)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private static decimal Minutes(WorkingInterval interval) => (decimal)interval.Length.TotalMinutes;

    private static List<WorkingInterval> Materialise(WorkingCalendar calendar, Instant origin, Instant end)
    {
        var zone = DateTimeZoneProviders.Tzdb[calendar.ZoneId];

        // One day of slack on each side: a shift may start before the origin and still have
        // working time inside the horizon.
        var first = origin.InZone(zone).Date.PlusDays(-1);
        var last = end.InZone(zone).Date.PlusDays(1);

        var intervals = new List<WorkingInterval>();
        for (var date = first; date <= last; date = date.PlusDays(1))
        {
            foreach (var interval in calendar.IntervalsOn(date))
            {
                var start = Instant.Max(interval.Start, origin);
                var finish = Instant.Min(interval.End, end);
                if (finish > start)
                {
                    intervals.Add(new WorkingInterval(start, finish));
                }
            }
        }

        return [.. intervals.OrderBy(interval => interval.Start)];
    }
}
