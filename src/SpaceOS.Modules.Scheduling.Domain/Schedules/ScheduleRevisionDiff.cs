using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>One operation that exists in both revisions but did not stay where it was.</summary>
/// <param name="OperationId">The operation.</param>
/// <param name="StartMinuteDelta">How much later it starts; negative means earlier.</param>
/// <param name="FinishMinuteDelta">How much later it finishes; negative means earlier.</param>
/// <param name="FromResourceKey">Resource it ran on before, when it moved; otherwise null.</param>
/// <param name="ToResourceKey">Resource it runs on now, when it moved; otherwise null.</param>
public sealed record OperationShift(
    string OperationId,
    decimal StartMinuteDelta,
    decimal FinishMinuteDelta,
    string? FromResourceKey,
    string? ToResourceKey)
{
    /// <summary>True when the operation changed resource, not just timing.</summary>
    public bool MovedResource => FromResourceKey is not null;
}

/// <summary>
/// What would change if a shadow revision replaced the one in force.
/// </summary>
/// <param name="AddedOperationIds">Operations the new revision plans and the old one did not.</param>
/// <param name="RemovedOperationIds">Operations the old revision planned and the new one does not.</param>
/// <param name="ShiftedOperations">Operations present in both, but moved in time or to another resource.</param>
/// <param name="UnchangedOperationCount">How many operations stayed exactly where they were.</param>
/// <param name="MakespanMinuteDelta">Change in the plan's total length; negative means shorter.</param>
/// <param name="ResourcesWithChangedCalendar">
/// Resources whose pinned calendar revision differs between the two plans.
/// </param>
/// <param name="DependencyNetworkChanged">True when the precedence network is not identical.</param>
public sealed record ScheduleRevisionDiff(
    IReadOnlyList<string> AddedOperationIds,
    IReadOnlyList<string> RemovedOperationIds,
    IReadOnlyList<OperationShift> ShiftedOperations,
    int UnchangedOperationCount,
    decimal MakespanMinuteDelta,
    IReadOnlyList<string> ResourcesWithChangedCalendar,
    bool DependencyNetworkChanged)
{
    /// <summary>True when the two revisions would put every operation in the same place.</summary>
    /// <remarks>
    /// A calendar change alone makes this false even with identical minutes — see
    /// <see cref="ScheduleRevisionComparer"/> for why that is not pedantry.
    /// </remarks>
    public bool IsEmpty =>
        AddedOperationIds.Count == 0
        && RemovedOperationIds.Count == 0
        && ShiftedOperations.Count == 0
        && ResourcesWithChangedCalendar.Count == 0
        && !DependencyNetworkChanged;
}

/// <summary>
/// Compares two schedule revisions so a planner can see what publishing would actually do.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the shadow state worth having (ADR-069 §4): a revision can be computed
/// and evaluated NEXT TO the published one, and the question that decides whether to publish is
/// "what moves, and by how much".
/// </para>
/// <para>
/// <b>Unchanged operations are counted, not listed.</b> On a plan of a few thousand operations a
/// list of everything that stayed put is noise that hides the twenty that moved. The count keeps
/// the reader honest about scale without burying the answer.
/// </para>
/// <para>
/// <b>A changed calendar revision counts as a difference even when every minute is identical</b>,
/// and this is the subtle one: the plan is stored in working minutes, so the same minute lands
/// on a different date when the calendar changes underneath it. Reporting "nothing changed"
/// there would be the most misleading thing this type could say — every promised date would have
/// moved while the diff claimed a quiet week.
/// </para>
/// </remarks>
public static class ScheduleRevisionComparer
{
    /// <summary>Compares the revision in force with a candidate.</summary>
    /// <param name="current">The revision currently in force.</param>
    /// <param name="candidate">The revision being evaluated.</param>
    public static ScheduleRevisionDiff Compare(ScheduleRevision current, ScheduleRevision candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);

        var before = current.Operations.ToDictionary(
            operation => operation.OperationId, StringComparer.Ordinal);
        var after = candidate.Operations.ToDictionary(
            operation => operation.OperationId, StringComparer.Ordinal);

        var added = after.Keys.Where(id => !before.ContainsKey(id));
        var removed = before.Keys.Where(id => !after.ContainsKey(id));

        var shifts = new List<OperationShift>();
        var unchanged = 0;

        // Ordinal ordering everywhere: this output is read by a human and compared between
        // runs, so it must not depend on dictionary enumeration order.
        foreach (var id in after.Keys.Where(before.ContainsKey).OrderBy(id => id, StringComparer.Ordinal))
        {
            var was = before[id];
            var now = after[id];

            var startDelta = now.StartMinute - was.StartMinute;
            var finishDelta = now.FinishMinute - was.FinishMinute;
            var movedResource = !string.Equals(was.ResourceKey, now.ResourceKey, StringComparison.Ordinal);

            if (startDelta == 0m && finishDelta == 0m && !movedResource)
            {
                unchanged++;
                continue;
            }

            shifts.Add(new OperationShift(
                id,
                startDelta,
                finishDelta,
                movedResource ? was.ResourceKey : null,
                movedResource ? now.ResourceKey : null));
        }

        return new ScheduleRevisionDiff(
            [.. added.OrderBy(id => id, StringComparer.Ordinal)],
            [.. removed.OrderBy(id => id, StringComparer.Ordinal)],
            shifts,
            unchanged,
            Makespan(candidate) - Makespan(current),
            [.. ChangedCalendars(current, candidate).OrderBy(key => key, StringComparer.Ordinal)],
            !SameNetwork(current, candidate));
    }

    /// <summary>Total length of a plan: zero when it holds no operations.</summary>
    private static decimal Makespan(ScheduleRevision revision) =>
        revision.Operations.Count == 0 ? 0m : revision.Operations.Max(operation => operation.FinishMinute);

    /// <summary>
    /// Resources whose calendar pin differs — including one that appears or disappears.
    /// </summary>
    private static IEnumerable<string> ChangedCalendars(ScheduleRevision current, ScheduleRevision candidate)
    {
        foreach (var (resourceKey, revision) in candidate.CalendarRevisions)
        {
            if (!current.CalendarRevisions.TryGetValue(resourceKey, out var previous) || previous != revision)
            {
                yield return resourceKey;
            }
        }

        // A resource that dropped out of the plan also changed what the plan rests on.
        foreach (var resourceKey in current.CalendarRevisions.Keys)
        {
            if (!candidate.CalendarRevisions.ContainsKey(resourceKey))
            {
                yield return resourceKey;
            }
        }
    }

    /// <summary>
    /// Whether the precedence networks are identical, attribution included.
    /// </summary>
    /// <remarks>
    /// The resolved bound and its source are compared too, not just the edge list: an edge that
    /// now binds through a partial release instead of the relation is a different promise, even
    /// when the dates happen to coincide today.
    /// </remarks>
    private static bool SameNetwork(ScheduleRevision current, ScheduleRevision candidate)
    {
        static IEnumerable<string> Canonical(ScheduleRevision revision) =>
            revision.Dependencies
                .Select(edge =>
                    $"{edge.PredecessorOperationId}|{edge.SuccessorOperationId}|{edge.Relation}|" +
                    $"{edge.LagMinutes}|{edge.EarliestStartMinute}|{edge.StartSource}")
                .OrderBy(key => key, StringComparer.Ordinal);

        return Canonical(current).SequenceEqual(Canonical(candidate), StringComparer.Ordinal);
    }
}
