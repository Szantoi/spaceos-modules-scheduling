using System;

namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>
/// How a partial release of the predecessor's output interacts with the precedence bound.
/// </summary>
/// <remarks>
/// <para>
/// This is an OPEN CONTRACT QUESTION (Doorstar root, 2026-07-28): the compatibility
/// fixture only covers a release that is EARLIER than the FS bound, so it cannot tell
/// the two readings apart. Until Doorstar answers, the policy must be chosen explicitly
/// by the caller — a silent default here would freeze an unverified scheduling rule into
/// the core.
/// </para>
/// <para>
/// There is deliberately no <c>Default</c> member and no default parameter value on
/// <see cref="DependencyBoundResolver.Resolve"/>.
/// </para>
/// </remarks>
public enum PartialReleasePolicy
{
    /// <summary>
    /// Not chosen. Passing this while a partial release is present throws, so an
    /// unconfigured caller fails loudly instead of silently picking a semantic.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The release minute replaces the precedence bound unconditionally, even when it
    /// is LATER. This reproduces today's Doorstar reference implementation and is the
    /// policy the compatibility vectors are pinned against.
    /// </summary>
    ReplacesDependencyBound = 1,

    /// <summary>
    /// The release may only pull the start earlier, never push it later:
    /// min(dependency bound, release). The alternative reading of the same fixture.
    /// </summary>
    MayOnlyAdvanceStart = 2,
}

/// <summary>Provenance labels for the partial-release semantics.</summary>
public static class PartialReleaseContract
{
    /// <summary>
    /// Label for <see cref="PartialReleasePolicy.ReplacesDependencyBound"/>: it reproduces
    /// the current Doorstar reference, which is explicitly NOT the agreed final contract.
    /// Report it in review and in any API response that depends on it.
    /// </summary>
    public const string BaselineLabel = "doorstar-baseline-v1 (not final)";
}

/// <summary>
/// Converts a legacy release threshold (a fraction of the predecessor's output) into a
/// release minute on the normalised timeline.
/// </summary>
/// <remarks>
/// The Doorstar reference explicitly leaves this calendar-aware conversion to the
/// platform, and the required rounding/net-time semantics are still an open question.
/// The core therefore declares the seam and ships NO implementation that could be
/// mistaken for the agreed contract — see <see cref="PendingContractReleaseCalculator"/>.
/// </remarks>
public interface IPartialReleaseCalculator
{
    /// <summary>
    /// Release minute for a predecessor occupying [startMinute, finishMinute] whose
    /// output is released at <paramref name="thresholdFraction"/> (0 &lt; f &lt;= 1).
    /// </summary>
    decimal CalculateReleaseMinute(decimal startMinute, decimal finishMinute, decimal thresholdFraction);
}

/// <summary>
/// Placeholder seam: throws until the Doorstar answer defines the conversion.
/// </summary>
/// <remarks>
/// A guessed formula (for example a linear split of elapsed time) would look plausible
/// and quietly produce wrong schedules, so refusing is the safer failure mode.
/// </remarks>
public sealed class PendingContractReleaseCalculator : IPartialReleaseCalculator
{
    /// <summary>Why the conversion refuses, and what unblocks it.</summary>
    public const string PendingReason =
        "The releaseThresholdPercent -> releaseMinute conversion is an open contract question " +
        "(JoineryTech backend -> Doorstar root, 2026-07-28, question 5). Supply an explicit " +
        "IPartialReleaseCalculator once the calendar-aware rule is agreed.";

    /// <inheritdoc />
    public decimal CalculateReleaseMinute(decimal startMinute, decimal finishMinute, decimal thresholdFraction) =>
        throw new NotSupportedException(PendingReason);
}
