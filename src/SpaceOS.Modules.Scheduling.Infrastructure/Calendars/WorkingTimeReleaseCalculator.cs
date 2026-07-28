using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>
/// Converts a partial-release threshold into the instant at which the predecessor has
/// produced that fraction of its output.
/// </summary>
/// <remarks>
/// <para>
/// The rule (business owner decision, 2026-07-28, ADR-069 §4): the threshold is
/// <b>proportional to WORKING time</b> on the predecessor's resource calendar — breaks do
/// not count — and the result is rounded <b>up</b> to the next working minute.
/// </para>
/// <para>
/// Working time rather than wall-clock is the whole point: a 50% threshold on a job that
/// spans a lunch break or an overnight gap would otherwise land in the middle of a period
/// when nothing is being produced, and the successor would be released against output that
/// does not exist yet.
/// </para>
/// <para>
/// Rounding up rather than to nearest: releasing early means releasing against unfinished
/// output. A minute late costs a minute; a minute early can cost a scrapped batch.
/// </para>
/// </remarks>
public sealed class WorkingTimeReleaseCalculator
{
    private readonly WorkingCalendar _calendar;

    /// <param name="calendar">The predecessor resource's working calendar.</param>
    public WorkingTimeReleaseCalculator(WorkingCalendar calendar) =>
        _calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));

    /// <summary>
    /// The instant at which <paramref name="thresholdFraction"/> of the predecessor's working
    /// time has elapsed.
    /// </summary>
    /// <param name="startUtc">Predecessor start.</param>
    /// <param name="finishUtc">Predecessor finish, exclusive.</param>
    /// <param name="thresholdFraction">Release threshold in (0, 1].</param>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is outside (0, 1].</exception>
    /// <exception cref="ArgumentException">The interval is inverted.</exception>
    /// <exception cref="InvalidOperationException">
    /// The interval contains no working time at all, so no fraction of it can be identified.
    /// </exception>
    public Instant CalculateReleaseInstant(Instant startUtc, Instant finishUtc, decimal thresholdFraction)
    {
        if (thresholdFraction <= 0m || thresholdFraction > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdFraction), thresholdFraction, "The threshold must be within (0, 1].");
        }
        if (finishUtc < startUtc)
        {
            throw new ArgumentException("The predecessor interval is inverted.", nameof(finishUtc));
        }

        var segments = WorkingSegments(startUtc, finishUtc).ToArray();
        var totalMinutes = segments.Sum(segment => (decimal)(segment.End - segment.Start).TotalMinutes);

        if (totalMinutes <= 0m)
        {
            // Refusing beats guessing: a release point inside a shutdown would be a fiction,
            // and silently returning the finish would hide a broken calendar.
            throw new InvalidOperationException(
                "The predecessor interval contains no working time on its calendar, so a " +
                "partial-release point cannot be derived. Check the resource calendar revision.");
        }

        // Ceiling to a whole working minute: never release against output that is not finished.
        var requiredMinutes = Math.Ceiling(totalMinutes * thresholdFraction);
        var remaining = requiredMinutes;

        foreach (var segment in segments)
        {
            var segmentMinutes = (decimal)(segment.End - segment.Start).TotalMinutes;
            if (remaining > segmentMinutes)
            {
                remaining -= segmentMinutes;
                continue;
            }

            return segment.Start + Duration.FromMinutes((double)remaining);
        }

        // Rounding can push the requirement onto the very last minute; the finish is then the
        // honest answer.
        return segments[^1].End;
    }

    private IEnumerable<WorkingInterval> WorkingSegments(Instant startUtc, Instant finishUtc)
    {
        // Walk local dates, because shifts are defined in local time. One day of slack on each
        // side covers a shift that starts before or ends after the requested interval.
        var zone = DateTimeZoneProviders.Tzdb[_calendar.ZoneId];
        var firstDate = startUtc.InZone(zone).Date.PlusDays(-1);
        var lastDate = finishUtc.InZone(zone).Date.PlusDays(1);

        for (var date = firstDate; date <= lastDate; date = date.PlusDays(1))
        {
            foreach (var interval in _calendar.IntervalsOn(date))
            {
                var clippedStart = Instant.Max(interval.Start, startUtc);
                var clippedEnd = Instant.Min(interval.End, finishUtc);
                if (clippedEnd > clippedStart)
                {
                    yield return new WorkingInterval(clippedStart, clippedEnd);
                }
            }
        }
    }
}
