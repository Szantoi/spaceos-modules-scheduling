using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Calculation;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;

namespace SpaceOS.Modules.Scheduling.Domain.Standards;

/// <summary>Lifecycle of one imported standard revision.</summary>
public enum StandardRevisionState
{
    /// <summary>Imported and usable for planning.</summary>
    Accepted,

    /// <summary>Imported but refused; visible to an operator, never used for planning.</summary>
    Quarantined,

    /// <summary>Replaced by a newer accepted revision. Kept for audit.</summary>
    Superseded,
}

/// <summary>
/// One immutable version of a norm time for an operation.
/// </summary>
public sealed class StandardRevision
{
    private readonly List<StandardQuarantineReason> _quarantineReasons = [];

    private StandardRevision(
        Guid id,
        int revision,
        decimal? unitMinutes,
        decimal? workforce,
        DependencyType? defaultDependency,
        decimal? releaseThresholdFraction,
        DateTimeOffset importedAtUtc,
        StandardRevisionState state)
    {
        Id = id;
        Revision = revision;
        UnitMinutes = unitMinutes;
        Workforce = workforce;
        DefaultDependency = defaultDependency;
        ReleaseThresholdFraction = releaseThresholdFraction;
        ImportedAtUtc = importedAtUtc;
        State = state;
    }

    /// <summary>Materialisation constructor for the persistence layer only.</summary>
    /// <remarks>
    /// EF cannot bind the real constructor, so it needs this one. Private, so application
    /// code still has to go through the factory method and its invariants.
    /// </remarks>
    private StandardRevision()
    {
    }

    /// <summary>Revision identity.</summary>
    public Guid Id { get; }

    /// <summary>Position in the chain, starting at 1.</summary>
    public int Revision { get; }

    /// <summary>Norm time for one unit, in minutes. Null when the import did not carry it.</summary>
    public decimal? UnitMinutes { get; }

    /// <summary>People required. Null when the import did not carry it.</summary>
    public decimal? Workforce { get; }

    /// <summary>Relation this operation defaults to when the network does not say otherwise.</summary>
    public DependencyType? DefaultDependency { get; }

    /// <summary>Partial-release threshold in (0, 1], when the source defines one.</summary>
    public decimal? ReleaseThresholdFraction { get; }

    /// <summary>When the revision was imported.</summary>
    public DateTimeOffset ImportedAtUtc { get; }

    /// <summary>Where the revision sits in its lifecycle.</summary>
    public StandardRevisionState State { get; private set; }

    /// <summary>Why it was quarantined; empty for an accepted revision.</summary>
    public IReadOnlyList<StandardQuarantineReason> QuarantineReasons => _quarantineReasons;

    /// <summary>True when this revision may be used to plan work.</summary>
    public bool IsUsableForPlanning => State == StandardRevisionState.Accepted;

    /// <summary>
    /// Builds the effort input this standard implies for a given volume.
    /// </summary>
    /// <remarks>
    /// A quarantined revision still answers, and it answers with the gaps intact — the effort
    /// calculator then reports <c>MissingFields</c> instead of a fabricated number. Refusing
    /// to answer would hide the row from the operator entirely.
    /// </remarks>
    public EffortInput ToEffortInput(decimal volume, int extraDays = 0) => new()
    {
        Volume = volume,
        UnitMinutes = UnitMinutes,
        Workforce = Workforce,
        ExtraDays = extraDays,
    };

    internal static StandardRevision Accepted(
        Guid id,
        int revision,
        decimal unitMinutes,
        decimal workforce,
        DependencyType? defaultDependency,
        decimal? releaseThresholdFraction,
        DateTimeOffset importedAtUtc) =>
        new(id, revision, unitMinutes, workforce, defaultDependency, releaseThresholdFraction,
            importedAtUtc, StandardRevisionState.Accepted);

    internal static StandardRevision Quarantined(
        Guid id,
        int revision,
        decimal? unitMinutes,
        decimal? workforce,
        DependencyType? defaultDependency,
        decimal? releaseThresholdFraction,
        DateTimeOffset importedAtUtc,
        IEnumerable<StandardQuarantineReason> reasons)
    {
        var instance = new StandardRevision(
            id, revision, unitMinutes, workforce, defaultDependency, releaseThresholdFraction,
            importedAtUtc, StandardRevisionState.Quarantined);
        instance._quarantineReasons.AddRange(reasons.Distinct().OrderBy(reason => reason));
        return instance;
    }

    internal void Supersede() => State = StandardRevisionState.Superseded;
}

/// <summary>
/// Aggregate root: the versioned norm time of one operation (ADR-069 §4).
/// </summary>
/// <remarks>
/// The invariant: <b>at most one accepted revision</b>. Planning must never have to choose
/// between two valid norm times, and a quarantined import must not displace the standard the
/// shop floor is currently working to.
/// </remarks>
public sealed class OperationStandard
{
    private readonly List<StandardRevision> _revisions = [];
    private readonly SortedDictionary<string, string> _qualifiers;

    private OperationStandard(
        Guid id,
        Guid tenantId,
        string sourceTaskKey,
        SortedDictionary<string, string> qualifiers)
    {
        Id = id;
        TenantId = tenantId;
        SourceTaskKey = sourceTaskKey;
        _qualifiers = qualifiers;
    }

    /// <summary>Materialisation constructor for the persistence layer only.</summary>
    /// <remarks>
    /// EF cannot bind the real constructor, so it needs this one. Private, so application
    /// code still has to go through the factory method and its invariants.
    /// </remarks>
    private OperationStandard()
    {
        _qualifiers = new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Standard identity.</summary>
    public Guid Id { get; }

    /// <summary>Owning tenant; mirrors the RLS predicate.</summary>
    public Guid TenantId { get; }

    /// <summary>Stable key from the source catalogue, used to reconcile re-imports.</summary>
    public string SourceTaskKey { get; } = string.Empty;

    /// <summary>
    /// Open key-value qualifiers (material, thickness, machine class …).
    /// </summary>
    /// <remarks>
    /// The core provides the key-value MECHANISM only; what the keys mean belongs to the
    /// industry layer (ADR-069 §3). Sorted so the qualifier set has one canonical form.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Qualifiers => _qualifiers;

    /// <summary>The revision chain, oldest first.</summary>
    public IReadOnlyList<StandardRevision> Revisions => _revisions;

    /// <summary>The revision planning may use, or null when none was ever accepted.</summary>
    public StandardRevision? AcceptedRevision =>
        _revisions.SingleOrDefault(revision => revision.State == StandardRevisionState.Accepted);

    /// <summary>Registers a standard for one operation.</summary>
    /// <exception cref="ArgumentException">Tenant or source key missing.</exception>
    public static OperationStandard Register(
        Guid id,
        Guid tenantId,
        string sourceTaskKey,
        IReadOnlyDictionary<string, string>? qualifiers = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A standard must belong to a tenant.", nameof(tenantId));
        }
        if (string.IsNullOrWhiteSpace(sourceTaskKey))
        {
            throw new ArgumentException(
                "A standard needs its source key: without it a re-import cannot tell whether " +
                "a row is the same operation or a new one.", nameof(sourceTaskKey));
        }

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in qualifiers ?? new Dictionary<string, string>())
        {
            sorted[pair.Key] = pair.Value;
        }

        return new OperationStandard(id, tenantId, sourceTaskKey, sorted);
    }

    /// <summary>
    /// Imports a revision, accepting it or quarantining it according to the supplied values.
    /// </summary>
    /// <returns>The created revision, accepted or quarantined.</returns>
    public StandardRevision Import(
        Guid revisionId,
        decimal? unitMinutes,
        decimal? workforce,
        DependencyType? defaultDependency,
        decimal? releaseThresholdFraction,
        DateTimeOffset importedAtUtc,
        IEnumerable<StandardQuarantineReason>? additionalReasons = null)
    {
        var reasons = new List<StandardQuarantineReason>(additionalReasons ?? []);

        if (unitMinutes is null) { reasons.Add(StandardQuarantineReason.MissingUnitTime); }
        else if (unitMinutes <= 0m) { reasons.Add(StandardQuarantineReason.NonPositiveUnitTime); }

        if (workforce is null) { reasons.Add(StandardQuarantineReason.MissingWorkforce); }
        else if (workforce <= 0m) { reasons.Add(StandardQuarantineReason.NonPositiveWorkforce); }

        if (releaseThresholdFraction is not null &&
            (releaseThresholdFraction <= 0m || releaseThresholdFraction > 1m))
        {
            reasons.Add(StandardQuarantineReason.InvalidReleaseThreshold);
        }

        var sequence = _revisions.Count + 1;

        if (reasons.Count > 0)
        {
            // Quarantined, not rejected: the row stays visible with its defects named, and it
            // deliberately does NOT displace the currently accepted revision.
            var quarantined = StandardRevision.Quarantined(
                revisionId, sequence, unitMinutes, workforce, defaultDependency,
                releaseThresholdFraction, importedAtUtc, reasons);
            _revisions.Add(quarantined);
            return quarantined;
        }

        AcceptedRevision?.Supersede();

        var accepted = StandardRevision.Accepted(
            revisionId, sequence, unitMinutes!.Value, workforce!.Value, defaultDependency,
            releaseThresholdFraction, importedAtUtc);
        _revisions.Add(accepted);
        return accepted;
    }
}
