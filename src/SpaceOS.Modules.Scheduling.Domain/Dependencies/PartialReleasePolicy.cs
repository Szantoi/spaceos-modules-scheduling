namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>Provenance of the partial-release rule.</summary>
public static class PartialReleaseContract
{
    /// <summary>
    /// The rule in force, decided in writing by the business owner on 2026-07-28 and carried
    /// into ADR-069 §4.
    /// </summary>
    /// <remarks>
    /// It replaces the earlier <c>doorstar-baseline-v1 (not final)</c> label, which existed
    /// only while the semantics was undecided. Report it wherever a proposal's provenance
    /// matters.
    /// </remarks>
    public const string ContractLabel = "doorstar-contract-v1 (final)";
}

/// <summary>Conditions a resolved bound wants the consumer to see.</summary>
public enum DependencyWarning
{
    /// <summary>
    /// A partial release pushed the successor's start LATER than the precedence relation
    /// alone would have.
    /// </summary>
    /// <remarks>
    /// The final rule says the release wins unconditionally, which means it can delay work
    /// that the dependency would have allowed to start earlier. That is legitimate but
    /// counter-intuitive, so the proposal must say so out loud rather than let a planner
    /// wonder why the operation sits idle.
    /// </remarks>
    PartialReleaseDelaysStart,
}
