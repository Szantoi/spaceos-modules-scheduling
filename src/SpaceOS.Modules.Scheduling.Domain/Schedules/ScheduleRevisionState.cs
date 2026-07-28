namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Lifecycle of a schedule revision (ADR-069 §4).
/// </summary>
/// <remarks>
/// <para>
/// <c>Proposal → Shadow → Published → Superseded</c>, with <c>Discarded</c> reachable
/// from any pre-published state. The point of the split is that a shadow calculation
/// must never disturb the plan the shop floor is currently working to.
/// </para>
/// </remarks>
public enum ScheduleRevisionState
{
    /// <summary>Freshly calculated, not yet compared against the published plan.</summary>
    Proposal,

    /// <summary>Under evaluation next to the published plan; produces a diff, changes nothing.</summary>
    Shadow,

    /// <summary>The active plan. At most one per run.</summary>
    Published,

    /// <summary>Was published, then replaced by a newer revision. Kept for audit.</summary>
    Superseded,

    /// <summary>Abandoned before publication. Terminal.</summary>
    Discarded,
}
