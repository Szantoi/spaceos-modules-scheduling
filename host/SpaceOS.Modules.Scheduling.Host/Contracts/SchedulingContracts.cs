using System;
using System.Collections.Generic;

namespace SpaceOS.Modules.Scheduling.Host.Contracts;

/// <summary>
/// The published read contract (ADR-069 §6, base <c>/api/scheduling/v1</c>).
/// </summary>
/// <remarks>
/// <para>
/// These types are the WIRE, not the domain. Timestamps travel as ISO-8601 UTC strings and
/// time zones as IANA identifiers (ADR-070 D2): no NodaTime type, and no domain aggregate,
/// may appear here. The Doorstar client is generated from this shape, so a leak would make
/// an internal library choice part of the contract.
/// </para>
/// <para>
/// Every response carries the provenance a consumer needs to act: the revision hash it can
/// quote back, the calendar revision the plan was computed against, and the label of the
/// rule set in force.
/// </para>
/// </remarks>
public static class SchedulingContractInfo
{
    /// <summary>Base path of the published contract.</summary>
    public const string BasePath = "/api/scheduling/v1";

    /// <summary>Contract version exposed in the OpenAPI document.</summary>
    public const string Version = "1.0.0-preview.1";
}

/// <summary>Opaque Kernel work reference: project → epic → task.</summary>
/// <param name="ProjectId">Kernel project identifier.</param>
/// <param name="EpicId">Kernel epic identifier.</param>
/// <param name="TaskId">Kernel task identifier.</param>
public sealed record WorkScopeDto(Guid ProjectId, Guid EpicId, Guid TaskId);

/// <summary>A scheduling run and the state of its revision chain.</summary>
/// <param name="RunId">Run identifier.</param>
/// <param name="ProjectId">The project this run plans.</param>
/// <param name="CreatedAtUtc">ISO-8601 UTC instant the run was opened.</param>
/// <param name="PublishedRevisionHash">Content hash of the active plan, or null when none is published.</param>
/// <param name="RevisionCount">How many revisions the chain holds.</param>
public sealed record ScheduleRunSummaryDto(
    Guid RunId,
    Guid ProjectId,
    string CreatedAtUtc,
    string? PublishedRevisionHash,
    int RevisionCount);

/// <summary>One scheduled operation as the consumer sees it.</summary>
/// <param name="OperationId">Stable operation identifier inside the revision.</param>
/// <param name="Scope">The Kernel work this operation serves.</param>
/// <param name="ResourceKey">Resource the operation runs on.</param>
/// <param name="StartMinute">Start on the normalised minute timeline.</param>
/// <param name="FinishMinute">Finish on the normalised minute timeline.</param>
/// <param name="AutomaticallyPlanned">False when the operation was not placed automatically.</param>
/// <param name="StandardRevision">Revision of the norm the times came from; null when none was used.</param>
/// <param name="SourceRevisions">
/// The consumer's own provenance, reflected back byte-for-byte. The platform stores these
/// values and never resolves them (PLAN-03 M3 contract input).
/// </param>
public sealed record OperationPlanDto(
    string OperationId,
    WorkScopeDto Scope,
    string ResourceKey,
    decimal StartMinute,
    decimal FinishMinute,
    bool AutomaticallyPlanned,
    int? StandardRevision,
    IReadOnlyDictionary<string, string> SourceRevisions);

/// <summary>One dependency edge with the attribution the planner needs.</summary>
/// <param name="PredecessorOperationId">Predecessor operation.</param>
/// <param name="SuccessorOperationId">Successor operation.</param>
/// <param name="RelationCode">FS, SS, FF or SF.</param>
/// <param name="LagMinutes">Lag (positive) or lead (negative), in minutes.</param>
/// <param name="EarliestStartMinute">Resolved earliest start, when this edge binds it.</param>
/// <param name="StartSource">Which rule produced the start bound: fixed_override, partial_release or dependency.</param>
/// <param name="Warnings">Conditions the consumer must see, e.g. partial_release_delays_fs_start.</param>
public sealed record DependencyEdgeDto(
    string PredecessorOperationId,
    string SuccessorOperationId,
    string RelationCode,
    decimal LagMinutes,
    decimal? EarliestStartMinute,
    string? StartSource,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The Doorstar main endpoint's payload: a proposal with its provenance.
/// </summary>
/// <param name="RunId">Run the proposal belongs to.</param>
/// <param name="RevisionHash">Quote this back when acting on the proposal.</param>
/// <param name="State">Revision lifecycle state.</param>
/// <param name="CalculatedAtUtc">ISO-8601 UTC instant of the calculation.</param>
/// <param name="Operations">The scheduled operations.</param>
/// <param name="Dependencies">The precedence network with resolved bounds.</param>
/// <param name="CalendarRevisions">Calendar revision used per resource — the plan is only reproducible with it.</param>
/// <param name="RuleSetLabel">Provenance of the rule set in force, e.g. doorstar-contract-v1 (final).</param>
public sealed record ProposalDto(
    Guid RunId,
    string RevisionHash,
    string State,
    string CalculatedAtUtc,
    IReadOnlyList<OperationPlanDto> Operations,
    IReadOnlyList<DependencyEdgeDto> Dependencies,
    IReadOnlyDictionary<string, int> CalendarRevisions,
    string RuleSetLabel);

/// <summary>A resource and the calendar revision currently in force.</summary>
/// <param name="ResourceKey">Stable resource key.</param>
/// <param name="TimeZoneId">IANA time zone identifier, e.g. Europe/Budapest.</param>
/// <param name="Capacity">Parallel capacity.</param>
/// <param name="CapacityPolicy">integer or fractional_fte.</param>
/// <param name="Revision">Calendar revision number.</param>
/// <param name="IsApproved">Only an approved revision may be scheduled against.</param>
public sealed record ResourceCalendarDto(
    string ResourceKey,
    string TimeZoneId,
    decimal Capacity,
    string CapacityPolicy,
    int Revision,
    bool IsApproved);

/// <summary>A daily time range on the resource's own local clock, in minutes from midnight.</summary>
/// <param name="StartMinuteOfDay">Inclusive start, 0-1440.</param>
/// <param name="EndMinuteOfDay">Exclusive end, 0-1440.</param>
public sealed record DayRangeDto(int StartMinuteOfDay, int EndMinuteOfDay);

/// <summary>One weekday's recurring shift with its breaks.</summary>
/// <param name="IsoWeekday">ISO-8601 weekday, Monday = 1.</param>
/// <param name="Shift">The shift itself.</param>
/// <param name="Breaks">Non-working ranges inside the shift.</param>
public sealed record RecurringShiftDto(int IsoWeekday, DayRangeDto Shift, IReadOnlyList<DayRangeDto> Breaks);

/// <summary>A dated deviation from the recurring pattern.</summary>
/// <param name="Date">Local date, ISO-8601 (yyyy-MM-dd).</param>
/// <param name="Kind">closure, maintenance or overtime.</param>
/// <param name="Span">Affected range; null means the whole day.</param>
/// <param name="Reason">Optional justification shown to planners.</param>
public sealed record CalendarExceptionDto(string Date, string Kind, DayRangeDto? Span, string? Reason);

/// <summary>A calendar revision in full: the pattern a plan was computed against.</summary>
/// <param name="ResourceKey">Stable resource key.</param>
/// <param name="Revision">Calendar revision number.</param>
/// <param name="TimeZoneId">IANA time zone identifier.</param>
/// <param name="Capacity">Parallel capacity.</param>
/// <param name="CapacityPolicy">integer or fractional_fte.</param>
/// <param name="IsApproved">Only an approved revision may be scheduled against.</param>
/// <param name="EffectiveFromUtc">ISO-8601 UTC instant the revision takes effect.</param>
/// <param name="EffectiveToUtc">ISO-8601 UTC instant it was closed at, or null while open.</param>
/// <param name="Shifts">The recurring weekly pattern.</param>
/// <param name="Exceptions">Dated deviations.</param>
public sealed record ResourceCalendarDetailDto(
    string ResourceKey,
    int Revision,
    string TimeZoneId,
    decimal Capacity,
    string CapacityPolicy,
    bool IsApproved,
    string EffectiveFromUtc,
    string? EffectiveToUtc,
    IReadOnlyList<RecurringShiftDto> Shifts,
    IReadOnlyList<CalendarExceptionDto> Exceptions);

/// <summary>A period where committed demand exceeds the resource's capacity (R5).</summary>
/// <param name="StartUtc">ISO-8601 UTC start of the overload.</param>
/// <param name="EndUtc">ISO-8601 UTC exclusive end.</param>
/// <param name="PeakDemand">Highest simultaneous demand inside the period.</param>
/// <param name="Capacity">Capacity it was measured against.</param>
/// <param name="PeakExcess">PeakDemand minus Capacity — how bad it gets at worst.</param>
public sealed record OverloadSpanDto(
    string StartUtc,
    string EndUtc,
    decimal PeakDemand,
    decimal Capacity,
    decimal PeakExcess);

/// <summary>A registered operation standard and the state of its revision chain.</summary>
/// <param name="SourceTaskKey">Stable key from the source catalogue.</param>
/// <param name="Qualifiers">The qualifier set that identifies this standard.</param>
/// <param name="AcceptedRevision">Revision currently usable for planning, or null when none is.</param>
/// <param name="RevisionCount">How many revisions the chain holds.</param>
public sealed record OperationStandardSummaryDto(
    string SourceTaskKey,
    IReadOnlyDictionary<string, string> Qualifiers,
    int? AcceptedRevision,
    int RevisionCount);

/// <summary>A versioned norm time, including why a revision was quarantined.</summary>
/// <param name="SourceTaskKey">Stable key from the source catalogue.</param>
/// <param name="Revision">Revision number.</param>
/// <param name="State">accepted, quarantined or superseded.</param>
/// <param name="UnitMinutes">Norm time per unit; null when the import did not carry it.</param>
/// <param name="Workforce">People required; null when the import did not carry it.</param>
/// <param name="QuarantineReasons">Named defects; empty for an accepted revision.</param>
public sealed record StandardRevisionDto(
    string SourceTaskKey,
    int Revision,
    string State,
    decimal? UnitMinutes,
    decimal? Workforce,
    IReadOnlyList<string> QuarantineReasons);
