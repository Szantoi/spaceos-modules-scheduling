using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Computes the content hash that identifies a schedule revision (ADR-069 §4, the
/// ADR-068 §8 terms-revision pattern).
/// </summary>
/// <remarks>
/// <para>
/// The hash is the revision's identity on the wire: Doorstar receives it with every
/// proposal and quotes it back when acting on one. Two properties therefore matter more
/// than speed:
/// </para>
/// <list type="bullet">
///   <item><b>Deterministic:</b> the same content must hash identically on any machine,
///   in any culture, in any enumeration order — hence ordinal sorting and invariant
///   number formatting.</item>
///   <item><b>Injective enough:</b> field values are length-prefixed, so an id ending in
///   the separator cannot impersonate a different field layout.</item>
/// </list>
/// </remarks>
public static class RevisionHasher
{
    /// <summary>Hashes the full content of a revision: operations, edges and calendar pins.</summary>
    /// <remarks>
    /// All three are hashed because all three are the plan. Two proposals with identical
    /// times but different precedence, different norm revisions or a different calendar
    /// revision are different answers, and a consumer comparing hashes must be told so.
    /// </remarks>
    /// <returns>Lowercase hex SHA-256 of the canonical representation.</returns>
    public static string ComputeHash(
        IEnumerable<OperationPlan> operations,
        IEnumerable<PlannedDependency> dependencies,
        IReadOnlyDictionary<string, int> calendarRevisions)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(calendarRevisions);

        var canonical = new StringBuilder();
        var ordered = operations
            .OrderBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ThenBy(operation => operation.ResourceKey, StringComparer.Ordinal);

        foreach (var operation in ordered)
        {
            Append(canonical, operation.OperationId);
            // The Kernel scope is part of the plan's identity: the same times under a
            // different epic or task say something different about the work, so the consumer
            // must see a different hash.
            Append(canonical, operation.Scope.ToString());
            Append(canonical, operation.ResourceKey);
            Append(canonical, Format(operation.StartMinute));
            Append(canonical, Format(operation.FinishMinute));
            Append(canonical, operation.AutomaticallyPlanned ? "1" : "0");
            Append(canonical, operation.StandardRevision?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

            // Provenance is hashed in key order, not insertion order: the same lineage
            // deserialised from JSON in another order is the same lineage.
            foreach (var (key, value) in operation.SourceRevisions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                Append(canonical, key);
                Append(canonical, value);
            }

            canonical.Append('\n');
        }

        canonical.Append("--edges\n");
        var orderedEdges = dependencies
            .OrderBy(edge => edge.PredecessorOperationId, StringComparer.Ordinal)
            .ThenBy(edge => edge.SuccessorOperationId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Relation);

        foreach (var edge in orderedEdges)
        {
            Append(canonical, edge.PredecessorOperationId);
            Append(canonical, edge.SuccessorOperationId);
            Append(canonical, edge.Relation.ToString());
            Append(canonical, Format(edge.LagMinutes));
            Append(canonical, edge.EarliestStartMinute is { } bound ? Format(bound) : string.Empty);
            Append(canonical, edge.StartSource?.ToString() ?? string.Empty);

            // ADDITIVE FIELDS, hashed only when they differ from the default.
            //
            // The hash has to cover everything that travels on the wire — otherwise two
            // different contents can share one hash and it stops being an identity. But
            // hashing a default unconditionally would move the hash of every plan that never
            // used these fields, and Doorstar re-quotes those hashes. So the rule is: a
            // default-valued field contributes NOTHING, byte for byte (pinned by
            // RevisionHashCompatibilityTests).
            //
            // Each value is preceded by a LABEL: without it, a plan carrying only a lag kind
            // and one carrying only a release fraction could produce the same bytes.
            if (edge.ReleaseThresholdFraction is { } fraction)
            {
                Append(canonical, "release");
                Append(canonical, Format(fraction));
            }

            if (edge.LagKind != LagKind.WorkingTime)
            {
                Append(canonical, "lagkind");
                Append(canonical, edge.LagKind.ToString());
            }

            // Warnings are sorted, not taken as produced: they are a set of conditions, and
            // the order a resolver happened to append them in carries no meaning.
            foreach (var warning in edge.Warnings.Select(warning => warning.ToString()).OrderBy(name => name, StringComparer.Ordinal))
            {
                Append(canonical, warning);
            }

            canonical.Append('\n');
        }

        canonical.Append("--calendars\n");
        foreach (var (resourceKey, revision) in calendarRevisions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Append(canonical, resourceKey);
            Append(canonical, revision.ToString(CultureInfo.InvariantCulture));
            canonical.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    // Length prefix instead of a plain separator: "a|b" and "a" + "|b" must not collide.
    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');

    // Normalising the scale keeps 10, 10.0 and 10.00 from hashing differently: they are
    // the same instant, and a spurious hash change would look like a plan change.
    private static string Format(decimal value) =>
        Normalise(value).ToString(CultureInfo.InvariantCulture);

    private static decimal Normalise(decimal value)
    {
        var normalised = value / 1.000000000000000000000000000000000m;
        return normalised == 0m ? 0m : normalised; // collapses -0 to 0
    }
}
