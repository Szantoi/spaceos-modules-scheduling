using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>One operation's plan resolved to real instants.</summary>
/// <param name="OperationId">The operation.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="FinishUtc">When its working time is complete.</param>
public sealed record DatedOperation(string OperationId, Instant StartUtc, Instant FinishUtc);

/// <summary>
/// Resolves a stored revision's working minutes into dates, using the calendars the revision
/// was PINNED to.
/// </summary>
/// <remarks>
/// <para>
/// The revision stores working minutes plus a pin per resource (<c>calendarRevisions</c>) and
/// the timeline origin. That is deliberately everything the resolution needs: dating a plan is
/// then a pure function of content that is already hashed, so the same revision resolves to the
/// same dates forever — including after somebody approves a newer calendar.
/// </para>
/// <para>
/// <b>The pinned revision is what makes this honest.</b> Reading today's calendar instead would
/// be quietly wrong in the one case that matters: the plan a shop floor is working from would
/// silently move because a calendar changed after it was published.
/// </para>
/// </remarks>
public static class PlanDating
{
    /// <summary>Dates every operation of the revision.</summary>
    /// <param name="revision">The revision to resolve.</param>
    /// <param name="pinnedCalendars">
    /// The calendar revisions the plan was pinned to, by resource key. Must cover every
    /// scheduled resource — the aggregate guarantees the pin exists, this asks for the row.
    /// </param>
    /// <param name="horizon">How far ahead to materialise working time.</param>
    /// <returns>Empty when the revision predates the timeline origin and cannot be dated.</returns>
    /// <exception cref="ArgumentException">A scheduled resource has no supplied calendar.</exception>
    public static IReadOnlyList<DatedOperation> Resolve(
        ScheduleRevision revision,
        IReadOnlyDictionary<string, ResourceCalendarRevision> pinnedCalendars,
        Duration horizon)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(pinnedCalendars);

        // No origin, no dates — and no guessing. A revision computed before the origin was
        // recorded cannot be dated, and inventing one (the calculation time, say) would put a
        // plausible but arbitrary date in front of a planner.
        if (revision.TimelineOriginUtc is not { } origin)
        {
            return [];
        }

        var originInstant = Instant.FromDateTimeOffset(origin);
        var timelines = new Dictionary<string, WorkingTimeline>(StringComparer.Ordinal);

        foreach (var resourceKey in revision.Operations
                     .Select(operation => operation.ResourceKey)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!pinnedCalendars.TryGetValue(resourceKey, out var calendar))
            {
                throw new ArgumentException(
                    $"Resource '{resourceKey}' is scheduled in revision {revision.Sequence} but its " +
                    "pinned calendar was not supplied. The plan cannot be dated against a calendar " +
                    "that was not loaded.",
                    nameof(pinnedCalendars));
            }

            timelines[resourceKey] = new WorkingTimeline(calendar.ToWorkingCalendar(), originInstant, horizon);
        }

        return
        [
            .. revision.Operations.Select(operation =>
            {
                var timeline = timelines[operation.ResourceKey];
                var start = timeline.AtWorkingMinute(operation.StartMinute);

                // Start and finish read the axis from opposite sides — see WorkingTimeline: a
                // milestone stays on its start rather than being pushed into the next interval.
                var finish = operation.FinishMinute > operation.StartMinute
                    ? timeline.EndAtWorkingMinute(operation.FinishMinute)
                    : start;

                return new DatedOperation(operation.OperationId, start, finish);
            }),
        ];
    }
}
