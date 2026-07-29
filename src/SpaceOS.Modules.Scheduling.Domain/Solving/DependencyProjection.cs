using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;

namespace SpaceOS.Modules.Scheduling.Domain.Solving;

/// <summary>
/// Explains a finished plan: what each dependency edge resolved to, and what the planner has
/// to be told about it.
/// </summary>
/// <remarks>
/// Runs AFTER placement and is shared by every strategy. A greedy list scheduler and a CP-SAT
/// search decide start minutes very differently, but "what does this edge mean, and was it
/// overridden" must not depend on which one ran — otherwise the same plan would carry
/// different warnings depending on configuration, and the shadow diff would show solver noise
/// instead of change.
/// </remarks>
public static class DependencyProjection
{
    /// <summary>Projects every edge of the request against the placed operations.</summary>
    public static (IReadOnlyList<PlannedDependency> Dependencies, IReadOnlyList<SchedulingDiagnostic> Diagnostics)
        Project(
            SchedulingRequest request,
            IReadOnlyDictionary<string, SolverOperation> operations,
            IReadOnlyDictionary<string, OperationPlan> placed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(placed);

        var edges = new List<PlannedDependency>();
        var diagnostics = new List<SchedulingDiagnostic>();

        // Ordinal ordering, not enumeration order: the projection feeds the revision hash, so
        // the order the caller happened to hand the edges over in must not reach it.
        var ordered = request.Dependencies
            .OrderBy(dependency => dependency.SuccessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.PredecessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Relation);

        foreach (var dependency in ordered)
        {
            var predecessor = placed[dependency.PredecessorOperationId];
            var successor = operations[dependency.SuccessorOperationId];

            var resolved = DependencyBoundResolver.Resolve(new DependencyBoundInput
            {
                Type = dependency.Relation,
                PredecessorStartMinute = predecessor.StartMinute,
                PredecessorFinishMinute = predecessor.FinishMinute,
                LagMinutes = dependency.LagMinutes,
                PartialReleaseMinute = PartialReleaseMinute(dependency, predecessor),
                FixedStartMinute = successor.FixedStartMinute,
            });

            edges.Add(new PlannedDependency
            {
                PredecessorOperationId = dependency.PredecessorOperationId,
                SuccessorOperationId = dependency.SuccessorOperationId,
                Relation = dependency.Relation,
                LagMinutes = dependency.LagMinutes,
                EarliestStartMinute = resolved.EarliestStartMinute,
                StartSource = resolved.StartSource,

                // Carried through from the request, not re-derived: the revision must record
                // the agreement it was computed under, not what today's inputs would say.
                ReleaseThresholdFraction = dependency.ReleaseThresholdFraction,
                LagKind = dependency.LagKind,
                Warnings = resolved.Warnings,
            });

            if (resolved.StartSource == BoundSource.FixedOverride)
            {
                diagnostics.Add(new SchedulingDiagnostic(
                    SchedulingDiagnosticCode.FixedStartOverridesPrecedence, successor.OperationId));
            }

            if (resolved.Warnings.Contains(DependencyWarning.PartialReleaseDelaysStart))
            {
                diagnostics.Add(new SchedulingDiagnostic(
                    SchedulingDiagnosticCode.PartialReleaseDelaysStart, successor.OperationId));
            }
        }

        return (
            [.. edges
                .OrderBy(edge => edge.PredecessorOperationId, StringComparer.Ordinal)
                .ThenBy(edge => edge.SuccessorOperationId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Relation)],
            diagnostics);
    }

    /// <summary>The minute a partial release frees the successor, if the edge carries one.</summary>
    public static decimal? PartialReleaseMinute(SolverDependency dependency, OperationPlan predecessor)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        ArgumentNullException.ThrowIfNull(predecessor);

        return dependency.ReleaseThresholdFraction is { } fraction
            ? predecessor.StartMinute + ((predecessor.FinishMinute - predecessor.StartMinute) * fraction)
            : null;
    }
}
