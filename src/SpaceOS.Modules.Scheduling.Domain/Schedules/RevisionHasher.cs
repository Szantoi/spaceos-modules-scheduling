using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
    /// <summary>Hashes the operation set of a revision.</summary>
    /// <returns>Lowercase hex SHA-256 of the canonical representation.</returns>
    public static string ComputeHash(IEnumerable<OperationPlan> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var canonical = new StringBuilder();
        var ordered = operations
            .OrderBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ThenBy(operation => operation.ResourceKey, StringComparer.Ordinal);

        foreach (var operation in ordered)
        {
            Append(canonical, operation.OperationId);
            Append(canonical, operation.ResourceKey);
            Append(canonical, Format(operation.StartMinute));
            Append(canonical, Format(operation.FinishMinute));
            Append(canonical, operation.AutomaticallyPlanned ? "1" : "0");
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
