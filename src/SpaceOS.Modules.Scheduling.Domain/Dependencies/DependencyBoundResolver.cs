using System;

namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>
/// Resolves the lower bounds a single dependency edge imposes on its successor
/// (ADR-069 §4, "Függőség-precedencia").
/// </summary>
/// <remarks>
/// <para>
/// Start branch: fixed override &gt; partial release &gt; FS/SS bound.<br/>
/// Finish branch: fixed override &gt; FF/SF bound.
/// </para>
/// <para>
/// The two branches are independent: an FS edge constrains only the start, an FF edge
/// only the finish. The caller combines bounds from all incoming edges.
/// </para>
/// </remarks>
public static class DependencyBoundResolver
{
    /// <summary>Resolves one edge.</summary>
    /// <param name="input">The edge and its already-scheduled predecessor.</param>
    /// <param name="partialReleasePolicy">
    /// Required, with no default: the partial-release semantics is an open contract
    /// question, so the caller must state which reading it wants (see
    /// <see cref="PartialReleasePolicy"/>).
    /// </param>
    /// <exception cref="ArgumentException">The predecessor finishes before it starts.</exception>
    /// <exception cref="InvalidOperationException">
    /// A partial release is present but the policy is <see cref="PartialReleasePolicy.Unspecified"/>.
    /// </exception>
    public static ResolvedDependencyBounds Resolve(
        DependencyBoundInput input,
        PartialReleasePolicy partialReleasePolicy)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.PredecessorFinishMinute < input.PredecessorStartMinute)
        {
            throw new ArgumentException(
                "Predecessor finish precedes its start.", nameof(input));
        }

        if (input.PartialReleaseMinute.HasValue && partialReleasePolicy == PartialReleasePolicy.Unspecified)
        {
            throw new InvalidOperationException(
                "A partial release is present but no PartialReleasePolicy was chosen. " +
                "The semantics is not settled with Doorstar, so the core refuses to assume one.");
        }

        var dependencyStart = input.Type switch
        {
            DependencyType.FinishToStart => input.PredecessorFinishMinute + input.LagMinutes,
            DependencyType.StartToStart => input.PredecessorStartMinute + input.LagMinutes,
            _ => (decimal?)null,
        };

        var dependencyFinish = input.Type switch
        {
            DependencyType.FinishToFinish => input.PredecessorFinishMinute + input.LagMinutes,
            DependencyType.StartToFinish => input.PredecessorStartMinute + input.LagMinutes,
            _ => (decimal?)null,
        };

        decimal? earliestStart;
        BoundSource? startSource;

        if (input.FixedStartMinute.HasValue)
        {
            earliestStart = input.FixedStartMinute;
            startSource = BoundSource.FixedOverride;
        }
        else if (input.PartialReleaseMinute.HasValue)
        {
            (earliestStart, startSource) = ApplyPartialRelease(
                input.PartialReleaseMinute.Value, dependencyStart, partialReleasePolicy);
        }
        else
        {
            earliestStart = dependencyStart;
            startSource = dependencyStart.HasValue ? BoundSource.Dependency : null;
        }

        return new ResolvedDependencyBounds
        {
            EarliestStartMinute = earliestStart,
            EarliestFinishMinute = input.FixedFinishMinute ?? dependencyFinish,
            StartSource = startSource,
            FinishSource = input.FixedFinishMinute.HasValue
                ? BoundSource.FixedOverride
                : dependencyFinish.HasValue ? BoundSource.Dependency : null,
        };
    }

    private static (decimal? Bound, BoundSource? Source) ApplyPartialRelease(
        decimal releaseMinute,
        decimal? dependencyStart,
        PartialReleasePolicy policy)
    {
        switch (policy)
        {
            case PartialReleasePolicy.ReplacesDependencyBound:
                return (releaseMinute, BoundSource.PartialRelease);

            case PartialReleasePolicy.MayOnlyAdvanceStart:
                // A release later than the precedence bound is ignored: it may pull the
                // start earlier, never push it out.
                if (dependencyStart.HasValue && releaseMinute >= dependencyStart.Value)
                {
                    return (dependencyStart, BoundSource.Dependency);
                }
                return (releaseMinute, BoundSource.PartialRelease);

            default:
                throw new InvalidOperationException($"Unhandled PartialReleasePolicy: {policy}.");
        }
    }
}
