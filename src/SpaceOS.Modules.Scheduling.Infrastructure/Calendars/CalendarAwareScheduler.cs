using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Solving;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Calendars;

/// <summary>A plan solved and dated together, with what the reconciliation had to say.</summary>
/// <param name="Solution">The solved plan, in working minutes.</param>
/// <param name="Dates">The same plan on the absolute timeline.</param>
/// <param name="Iterations">How many solver passes it took to settle the elapsed-time lags.</param>
/// <param name="Diagnostics">Remarks from the projection and from the reconciliation.</param>
public sealed record CalendarAwareSchedule(
    SchedulingSolution Solution,
    MaterialisedSchedule Dates,
    int Iterations,
    IReadOnlyList<MaterialisationDiagnostic> Diagnostics);

/// <summary>
/// Runs a solver against real calendars, reconciling the lags that are measured in elapsed
/// time rather than working time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs more than one pass.</b> The solver works on a calendar-free axis of
/// working minutes, so it cannot express "48 hours, weekend included": how many working
/// minutes that is depends on WHERE the predecessor lands, which is what the solver is
/// deciding. The two possible single-pass answers are both wrong in practice — counting the
/// lag as working time holds a Friday-afternoon curing job until the middle of the next week,
/// and counting it as zero releases the successor before the material is ready.
/// </para>
/// <para>
/// So the lag is reconciled instead: solve, date the plan, and for every elapsed-time lag ask
/// the calendar what that delay actually costs in working minutes from where the predecessor
/// ended up. Feed that back and solve again. The requirement only ever moves later, so the
/// loop settles quickly — and when it does not, it says so rather than shipping the last
/// guess as if it were an answer.
/// </para>
/// </remarks>
public sealed class CalendarAwareScheduler
{
    private const int DefaultMaximumIterations = 5;

    private readonly ISchedulingSolver _solver;
    private readonly int _maximumIterations;

    /// <param name="solver">The strategy to run; either implementation of the port.</param>
    /// <param name="maximumIterations">Cap on solver passes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The cap is below one.</exception>
    public CalendarAwareScheduler(ISchedulingSolver solver, int maximumIterations = DefaultMaximumIterations)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumIterations, 1);

        _solver = solver;
        _maximumIterations = maximumIterations;
    }

    /// <summary>Solves and dates the request.</summary>
    /// <exception cref="ArgumentException">A resource in the plan has no timeline.</exception>
    public CalendarAwareSchedule Run(
        SchedulingRequest request,
        IReadOnlyDictionary<string, WorkingTimeline> timelines)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timelines);

        // Elapsed lags start at zero working minutes — the calendar has not been consulted yet,
        // and starting from the working-time reading would begin the search days too late.
        var equivalents = request.Dependencies
            .Where(dependency => dependency.LagKind == LagKind.ElapsedTime)
            .ToDictionary(KeyOf, _ => 0m, StringComparer.Ordinal);

        for (var iteration = 1; ; iteration++)
        {
            var effective = WithWorkingLags(request, equivalents);
            var solution = _solver.Solve(effective);
            var dates = ScheduleMaterialiser.Materialise(effective, solution, timelines);

            var moved = Reconcile(request, dates, timelines, equivalents);
            if (moved.Count == 0 || iteration >= _maximumIterations)
            {
                // Only the edges that were STILL moving on the last pass are reported: the
                // ones that settled earlier are correct, and flagging them would bury the
                // real problem in noise.
                IReadOnlyList<MaterialisationDiagnostic> diagnostics = moved.Count == 0
                    ? dates.Diagnostics
                    : [.. dates.Diagnostics, .. moved.Select(edge => new MaterialisationDiagnostic(
                        MaterialisationCode.ElapsedLagNotSettled, edge.Predecessor, edge.Successor))];

                return new CalendarAwareSchedule(solution, dates, iteration, diagnostics);
            }
        }
    }

    private static string KeyOf(SolverDependency dependency) =>
        $"{dependency.PredecessorOperationId}{dependency.SuccessorOperationId}{dependency.Relation}";

    /// <summary>The request the solver actually sees: elapsed lags replaced by their equivalents.</summary>
    private static SchedulingRequest WithWorkingLags(
        SchedulingRequest request,
        IReadOnlyDictionary<string, decimal> equivalents) =>
        request with
        {
            Dependencies = [.. request.Dependencies.Select(dependency =>
                dependency.LagKind == LagKind.ElapsedTime
                    ? dependency with
                    {
                        LagMinutes = equivalents[KeyOf(dependency)],
                        LagKind = LagKind.WorkingTime,
                    }
                    : dependency)],
        };

    /// <summary>
    /// Asks the calendar what each elapsed lag costs from where the predecessor actually landed.
    /// </summary>
    /// <returns>The edges whose requirement moved; empty means the plan agrees with the calendars.</returns>
    private static List<(string Predecessor, string Successor)> Reconcile(
        SchedulingRequest request,
        MaterialisedSchedule dates,
        IReadOnlyDictionary<string, WorkingTimeline> timelines,
        Dictionary<string, decimal> equivalents)
    {
        var dated = dates.Operations.ToDictionary(
            operation => operation.OperationId, StringComparer.Ordinal);

        var moved = new List<(string Predecessor, string Successor)>();

        foreach (var dependency in request.Dependencies.Where(edge => edge.LagKind == LagKind.ElapsedTime))
        {
            var successor = request.Operations.Single(operation => string.Equals(
                operation.OperationId, dependency.SuccessorOperationId, StringComparison.Ordinal));

            // A fixed start overrides the network, lag included — reconciling it would fight
            // the planner's own decision for as many passes as the cap allows.
            if (successor.FixedStartMinute.HasValue)
            {
                continue;
            }

            var predecessor = dated[dependency.PredecessorOperationId];
            var basis = dependency.Relation is DependencyType.StartToStart or DependencyType.StartToFinish
                ? predecessor.StartUtc
                : predecessor.FinishUtc;

            // The physical process runs on the clock, so the requirement is plain arithmetic
            // on instants — no calendar involved in WHEN it is ready.
            var readyAt = basis + Duration.FromMinutes((double)dependency.LagMinutes);

            // What the calendar does decide: how much WORKING time that delay consumes, which
            // is the only language the solver understands.
            var timeline = timelines[dated[dependency.SuccessorOperationId].ResourceKey];
            var needed = timeline.WorkingMinutesBetween(Instant.Min(basis, readyAt), Instant.Max(basis, readyAt));

            var key = KeyOf(dependency);
            if (needed > equivalents[key])
            {
                equivalents[key] = needed;
                moved.Add((dependency.PredecessorOperationId, dependency.SuccessorOperationId));
            }
        }

        return moved;
    }
}
