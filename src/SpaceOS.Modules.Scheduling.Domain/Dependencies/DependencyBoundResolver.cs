using System;
using System.Collections.Generic;

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
/// <para>
/// The partial-release rule is settled (business owner decision, 2026-07-28, ADR-069 §4):
/// the release wins <b>unconditionally</b>, even when it is later than the precedence bound.
/// Because that can delay work the dependency would have allowed earlier, the resolver
/// attaches <see cref="DependencyWarning.PartialReleaseDelaysStart"/> instead of leaving a
/// planner to wonder why an operation sits idle.
/// </para>
/// </remarks>
public static class DependencyBoundResolver
{
    /// <summary>Resolves one edge.</summary>
    /// <exception cref="ArgumentException">The predecessor finishes before it starts.</exception>
    public static ResolvedDependencyBounds Resolve(DependencyBoundInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.PredecessorFinishMinute < input.PredecessorStartMinute)
        {
            throw new ArgumentException("Predecessor finish precedes its start.", nameof(input));
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

        var warnings = new List<DependencyWarning>();

        decimal? earliestStart;
        BoundSource? startSource;

        if (input.FixedStartMinute.HasValue)
        {
            earliestStart = input.FixedStartMinute;
            startSource = BoundSource.FixedOverride;
        }
        else if (input.PartialReleaseMinute.HasValue)
        {
            earliestStart = input.PartialReleaseMinute;
            startSource = BoundSource.PartialRelease;

            if (dependencyStart.HasValue && input.PartialReleaseMinute.Value > dependencyStart.Value)
            {
                warnings.Add(DependencyWarning.PartialReleaseDelaysStart);
            }
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
            Warnings = warnings,
        };
    }
}
