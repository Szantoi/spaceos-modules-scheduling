using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Solving;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>One operation of a plan, on the absolute timeline.</summary>
/// <param name="OperationId">Operation the dates belong to.</param>
/// <param name="ResourceKey">Resource whose calendar produced them.</param>
/// <param name="StartUtc">First working instant of the operation.</param>
/// <param name="FinishUtc">Instant at which its working time is complete.</param>
public sealed record MaterialisedOperation(
    string OperationId,
    string ResourceKey,
    Instant StartUtc,
    Instant FinishUtc);

/// <summary>Why a materialised plan needs a remark.</summary>
public enum MaterialisationCode
{
    /// <summary>
    /// A dependency that holds on the working-minute axis does NOT hold in real time.
    /// </summary>
    /// <remarks>
    /// Only possible between resources whose calendars differ: the solver counts working
    /// minutes, and two calendars turn the same count into different instants.
    /// </remarks>
    PrecedenceBrokenAcrossCalendars,

    /// <summary>
    /// An elapsed-time lag did not settle within the allowed number of solver passes.
    /// </summary>
    /// <remarks>
    /// The plan is the last one computed, and it may release the successor before the physical
    /// process is finished. Reported rather than swallowed: a curing time that silently did not
    /// apply is exactly the kind of defect nobody finds until the material is ruined.
    /// </remarks>
    ElapsedLagNotSettled,
}

/// <summary>One remark about a materialised plan.</summary>
/// <param name="Code">What happened.</param>
/// <param name="PredecessorOperationId">Predecessor of the affected edge.</param>
/// <param name="SuccessorOperationId">Successor of the affected edge.</param>
public sealed record MaterialisationDiagnostic(
    MaterialisationCode Code,
    string PredecessorOperationId,
    string SuccessorOperationId);

/// <summary>A plan with real dates, and what has to be said about it.</summary>
/// <param name="Operations">The dated operations, in the plan's order.</param>
/// <param name="Diagnostics">Remarks the planner must see.</param>
public sealed record MaterialisedSchedule(
    IReadOnlyList<MaterialisedOperation> Operations,
    IReadOnlyList<MaterialisationDiagnostic> Diagnostics);

/// <summary>
/// Turns a solver plan measured in working minutes into one measured in dates.
/// </summary>
/// <remarks>
/// <para>
/// The solver works in WORKING minutes because that is what an effort calculation produces
/// and what a planner reasons in. A calendar is what turns that into "Tuesday morning": work
/// stops at the end of a shift and resumes at the start of the next one, so twelve working
/// hours can span three calendar days (business owner decision, 2026-07-29 — every operation
/// may span non-working time).
/// </para>
/// <para>
/// <b>Why capacity survives this and precedence may not.</b> Each operation is placed on ITS
/// OWN resource's axis, and that mapping is monotonic — order and overlap on one resource are
/// preserved exactly, so a capacity-correct plan stays capacity-correct. A dependency BETWEEN
/// resources is different: if two resources keep different calendars, the same working-minute
/// bound lands on different instants, and an edge the solver satisfied can be violated in real
/// time. That is not silently corrected here — the plan says so
/// (<see cref="MaterialisationCode.PrecedenceBrokenAcrossCalendars"/>), because quietly moving
/// a date would hide the fact that the schedule no longer satisfies the network it came from.
/// </para>
/// <para>
/// The diagnostics live in THIS layer rather than in the domain's
/// <see cref="SchedulingDiagnosticCode"/>: those codes travel on the published contract, and
/// adding one is a contract change with a Doorstar client behind it. When the read model
/// starts serving materialised plans, promoting this code is a deliberate, additive step.
/// </para>
/// </remarks>
public static class ScheduleMaterialiser
{
    /// <summary>Dates a solved plan.</summary>
    /// <param name="request">The request the plan answers; its edges are re-checked in real time.</param>
    /// <param name="solution">The solved plan, in working minutes.</param>
    /// <param name="timelines">One working-minute axis per resource key.</param>
    /// <exception cref="ArgumentException">A resource in the plan has no timeline.</exception>
    /// <exception cref="InvalidOperationException">The plan runs past a timeline's horizon.</exception>
    public static MaterialisedSchedule Materialise(
        SchedulingRequest request,
        SchedulingSolution solution,
        IReadOnlyDictionary<string, WorkingTimeline> timelines)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(timelines);

        var dated = new Dictionary<string, MaterialisedOperation>(StringComparer.Ordinal);

        foreach (var plan in solution.Operations)
        {
            if (!timelines.TryGetValue(plan.ResourceKey, out var timeline))
            {
                throw new ArgumentException(
                    $"Operation '{plan.OperationId}' runs on resource '{plan.ResourceKey}', which has " +
                    "no working timeline. A plan cannot be dated against a calendar that was not supplied.",
                    nameof(timelines));
            }

            var start = timeline.AtWorkingMinute(plan.StartMinute);

            // Start and finish read the axis from opposite sides: work BEGINS at the next
            // interval when a position lands on a boundary, and ENDS at the previous interval's
            // close. A milestone consumes no time, so it stays exactly on its start rather
            // than being pushed into the next working interval.
            var finish = plan.FinishMinute > plan.StartMinute
                ? timeline.EndAtWorkingMinute(plan.FinishMinute)
                : start;

            dated[plan.OperationId] = new MaterialisedOperation(
                plan.OperationId, plan.ResourceKey, start, finish);
        }

        return new MaterialisedSchedule(
            [.. solution.Operations.Select(plan => dated[plan.OperationId])],
            CheckPrecedence(request, dated, timelines));
    }

    /// <summary>Re-checks every edge against the DATES, not the working minutes.</summary>
    private static List<MaterialisationDiagnostic> CheckPrecedence(
        SchedulingRequest request,
        IReadOnlyDictionary<string, MaterialisedOperation> dated,
        IReadOnlyDictionary<string, WorkingTimeline> timelines)
    {
        var diagnostics = new List<MaterialisationDiagnostic>();

        var ordered = request.Dependencies
            .OrderBy(dependency => dependency.PredecessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.SuccessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Relation);

        foreach (var dependency in ordered)
        {
            var successorOperation = request.Operations.Single(operation => string.Equals(
                operation.OperationId, dependency.SuccessorOperationId, StringComparison.Ordinal));

            // A fixed start overrides the network by design; reporting it again here would
            // repeat what the solver already said.
            if (successorOperation.FixedStartMinute.HasValue)
            {
                continue;
            }

            var predecessor = dated[dependency.PredecessorOperationId];
            var successor = dated[dependency.SuccessorOperationId];
            var timeline = timelines[successor.ResourceKey];

            var (bound, constrained) = RealTimeBound(dependency, predecessor, timelines);

            // The lag is measured in WORKING minutes of the successor's calendar, the same
            // unit the solver used. (A lag that means real elapsed time — a curing period, say
            // — is a distinct concept; raised with root rather than assumed here.)
            var required = dependency.LagMinutes == 0m
                ? bound
                : timeline.AddWorkingMinutes(bound, dependency.LagMinutes);

            var actual = constrained == ConstrainedEnd.Start ? successor.StartUtc : successor.FinishUtc;

            if (actual < required)
            {
                diagnostics.Add(new MaterialisationDiagnostic(
                    MaterialisationCode.PrecedenceBrokenAcrossCalendars,
                    dependency.PredecessorOperationId,
                    dependency.SuccessorOperationId));
            }
        }

        return diagnostics;
    }

    private enum ConstrainedEnd
    {
        Start,
        Finish,
    }

    /// <summary>What the edge requires, as an instant, and which end of the successor it binds.</summary>
    private static (Instant Bound, ConstrainedEnd End) RealTimeBound(
        SolverDependency dependency,
        MaterialisedOperation predecessor,
        IReadOnlyDictionary<string, WorkingTimeline> timelines)
    {
        // A partial release replaces the relation's own start bound — including when it lands
        // later. The threshold is proportional to the PREDECESSOR's working time, so it is
        // derived by the one authority for that rule rather than restated here.
        if (dependency.ReleaseThresholdFraction is { } fraction)
        {
            var predecessorTimeline = timelines[predecessor.ResourceKey];
            var release = new WorkingTimeReleaseCalculator(predecessorTimeline.Calendar)
                .CalculateReleaseInstant(predecessor.StartUtc, predecessor.FinishUtc, fraction);

            return (release, ConstrainedEnd.Start);
        }

        return dependency.Relation switch
        {
            DependencyType.FinishToStart => (predecessor.FinishUtc, ConstrainedEnd.Start),
            DependencyType.StartToStart => (predecessor.StartUtc, ConstrainedEnd.Start),
            DependencyType.FinishToFinish => (predecessor.FinishUtc, ConstrainedEnd.Finish),
            DependencyType.StartToFinish => (predecessor.StartUtc, ConstrainedEnd.Finish),
            _ => throw new ArgumentOutOfRangeException(
                nameof(dependency), dependency.Relation, "Unmapped relation."),
        };
    }
}
