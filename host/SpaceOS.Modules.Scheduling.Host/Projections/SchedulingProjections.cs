using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Standards;
using SpaceOS.Modules.Scheduling.Host.Contracts;

namespace SpaceOS.Modules.Scheduling.Host.Projections;

/// <summary>
/// Translates domain aggregates into the published wire shapes.
/// </summary>
/// <remarks>
/// <para>
/// This is the ONLY place a domain type meets the contract, which is what keeps the
/// ADR-070 D2 purity guard satisfiable: the DTOs stay free of NodaTime, CLR date types and
/// aggregates, and the conversion cost is paid once, here. It sits in its OWN namespace so
/// the contract namespace contains contract types and nothing else — the guard scans that
/// namespace, and a helper living there would be one more thing it has to tolerate.
/// </para>
/// <para>
/// Enum values cross the wire as snake_case CODES, not .NET names. A consumer generating a
/// client from the spec should not inherit this module's C# casing conventions, and renaming
/// a C# member must not silently become a breaking API change — the mapping is explicit for
/// exactly that reason.
/// </para>
/// </remarks>
public static class SchedulingProjections
{
    /// <summary>Formats an instant as ISO-8601 UTC with a trailing Z.</summary>
    public static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    /// <summary>Converts a .NET enum member name to its snake_case wire code.</summary>
    public static string Code<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString()!;
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsUpper(name[index]))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(name[index]));
                continue;
            }

            builder.Append(name[index]);
        }

        return builder.ToString();
    }

    /// <summary>Relation codes are the planning-standard abbreviations, not enum names.</summary>
    public static string RelationCode(DependencyType relation) => relation switch
    {
        DependencyType.FinishToStart => "FS",
        DependencyType.StartToStart => "SS",
        DependencyType.FinishToFinish => "FF",
        DependencyType.StartToFinish => "SF",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unmapped dependency relation."),
    };

    /// <summary>Projects a run and its revision chain.</summary>
    public static ScheduleRunSummaryDto ToSummary(this ScheduleRun run) => new(
        run.Id,
        run.Project.Value,
        Utc(run.CreatedAtUtc),
        run.PublishedRevision?.ContentHash,
        run.Revisions.Count);

    /// <summary>Projects the Kernel work reference.</summary>
    public static WorkScopeDto ToDto(this KernelWorkScope scope) =>
        new(scope.Project.Value, scope.Epic.Value, scope.Task.Value);

    /// <summary>Projects one scheduled operation, lineage included.</summary>
    public static OperationPlanDto ToDto(this OperationPlan operation) => new(
        operation.OperationId,
        operation.Scope.ToDto(),
        operation.ResourceKey,
        operation.StartMinute,
        operation.FinishMinute,
        operation.AutomaticallyPlanned,
        operation.StandardRevision,
        operation.SourceRevisions);

    /// <summary>Projects one resolved precedence edge.</summary>
    public static DependencyEdgeDto ToDto(this PlannedDependency edge) => new(
        edge.PredecessorOperationId,
        edge.SuccessorOperationId,
        RelationCode(edge.Relation),
        edge.LagMinutes,
        edge.EarliestStartMinute,
        edge.StartSource is { } source ? Code(source) : null,
        [.. edge.Warnings.Select(warning => Code(warning))]);

    /// <summary>Projects a revision as the Doorstar proposal payload.</summary>
    public static ProposalDto ToProposal(this ScheduleRevision revision, Guid runId) => new(
        runId,
        revision.ContentHash,
        Code(revision.State),
        Utc(revision.CreatedAtUtc),
        [.. revision.Operations.Select(operation => operation.ToDto())],
        [.. revision.Dependencies.Select(edge => edge.ToDto())],
        revision.CalendarRevisions,
        PartialReleaseContract.ContractLabel);

    /// <summary>Projects a calendar revision as a list entry.</summary>
    public static ResourceCalendarDto ToDto(this ResourceCalendarRevision calendar) => new(
        calendar.ResourceKey,
        calendar.TimeZoneId,
        calendar.Capacity,
        Code(calendar.CapacityPolicy),
        calendar.Revision,
        calendar.IsApproved);

    /// <summary>Projects a calendar revision in full, with pattern and exceptions.</summary>
    public static ResourceCalendarDetailDto ToDetail(this ResourceCalendarRevision calendar) => new(
        calendar.ResourceKey,
        calendar.Revision,
        calendar.TimeZoneId,
        calendar.Capacity,
        Code(calendar.CapacityPolicy),
        calendar.IsApproved,
        Utc(calendar.EffectiveFromUtc),
        calendar.EffectiveToUtc is { } closedAt ? Utc(closedAt) : null,
        [.. calendar.Shifts.Select(shift => new RecurringShiftDto(
            shift.IsoWeekday,
            shift.Shift.ToDto(),
            [.. shift.Breaks.Select(pause => pause.ToDto())]))],
        [.. calendar.Exceptions.Select(exception => new CalendarExceptionDto(
            exception.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Code(exception.Kind),
            exception.Span?.ToDto(),
            exception.Reason))]);

    /// <summary>Projects a daily range.</summary>
    public static DayRangeDto ToDto(this DayRange range) =>
        new(range.StartMinuteOfDay, range.EndMinuteOfDay);

    /// <summary>Projects an overload period.</summary>
    public static OverloadSpanDto ToDto(this OverloadSpan span) => new(
        Utc(span.StartUtc),
        Utc(span.EndUtc),
        span.PeakDemand,
        span.Capacity,
        span.PeakExcess);

    /// <summary>Projects a standard and the state of its revision chain.</summary>
    public static OperationStandardSummaryDto ToSummary(this OperationStandard standard) => new(
        standard.SourceTaskKey,
        standard.Qualifiers,
        standard.AcceptedRevision?.Revision,
        standard.Revisions.Count);

    /// <summary>Projects one norm revision.</summary>
    public static StandardRevisionDto ToDto(this StandardRevision revision, string sourceTaskKey) => new(
        sourceTaskKey,
        revision.Revision,
        Code(revision.State),
        revision.UnitMinutes,
        revision.Workforce,
        [.. revision.QuarantineReasons.Select(reason => Code(reason))]);
}
