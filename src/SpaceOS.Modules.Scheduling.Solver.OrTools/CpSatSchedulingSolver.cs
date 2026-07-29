using System.Globalization;
using Google.OrTools.Sat;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Solving;

namespace SpaceOS.Modules.Scheduling.Solver.OrTools;

/// <summary>
/// Places every operation by constraint optimisation (ADR-070 D1), on the same port as the
/// reference strategy.
/// </summary>
/// <remarks>
/// <para>
/// The reference list scheduler is greedy and never backtracks: it commits each operation to
/// the earliest slot it can see, which can strand later work behind a choice that looked free
/// at the time. CP-SAT searches instead of committing, so it can find the shorter schedule the
/// greedy pass structurally cannot.
/// </para>
/// <para>
/// <b>Two phases, on purpose.</b> Minimising the makespan alone leaves every operation that is
/// not on the critical path free to sit anywhere, and CP-SAT would return whichever placement
/// it happened to prove first. So a second search fixes the optimal makespan and then pulls
/// every start as early as possible. That yields the plan a planner expects — shortest overall,
/// nothing idling for no reason — and it makes the result comparable with the reference, which
/// also starts work as early as it can.
/// </para>
/// <para>
/// <b>Determinism</b> (ADR-070 D3) comes from three things together: a fixed seed, a single
/// search worker, and a canonically ordered model. The last one matters as much as the
/// parameters — CP-SAT explores in the order the model was built, so handing the same work
/// over in a different sequence would otherwise produce a different (equally good) plan and a
/// changed revision hash. Every loop below therefore iterates an ordinal ordering, never a
/// dictionary's enumeration order.
/// </para>
/// <para>
/// <b>What it does NOT decide:</b> validity (shared validator), what a dependency edge means
/// (<see cref="DependencyBoundResolver"/>), and how an edge is explained to the planner
/// (<see cref="DependencyProjection"/>). Those are the domain's, so that swapping the strategy
/// cannot quietly swap the semantics.
/// </para>
/// </remarks>
public sealed class CpSatSchedulingSolver : ISchedulingSolver
{
    private readonly CpSatSolverOptions options;

    /// <summary>Creates the adapter with the reproducible default profile.</summary>
    public CpSatSchedulingSolver()
        : this(new CpSatSolverOptions())
    {
    }

    /// <summary>Creates the adapter with an explicit configuration.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its usable range.</exception>
    public CpSatSchedulingSolver(CpSatSolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TimeScalePerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ParallelSearchWorkers, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxSearchSeconds);

        this.options = options;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The search proved the request unsatisfiable, or ran out of time before finding any
    /// plan. Both are reported rather than approximated: a plan that violates the constraints
    /// it was built from is worse than no plan.
    /// </exception>
    public SchedulingSolution Solve(SchedulingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validated = SchedulingRequestValidator.Validate(request);
        var order = validated.TopologicalOrder;

        var model = new CpModel();
        var horizon = Horizon(request, validated);

        var starts = new Dictionary<string, IntVar>(StringComparer.Ordinal);
        var intervalsByResource = request.Resources
            .OrderBy(resource => resource.ResourceKey, StringComparer.Ordinal)
            .ToDictionary(
                resource => resource.ResourceKey,
                _ => new List<IntervalVar>(),
                StringComparer.Ordinal);

        foreach (var operationId in order)
        {
            var operation = validated.Operations[operationId];
            var duration = Scale(operation.DurationMinutes);
            var start = model.NewIntVar(0, horizon, $"start:{operationId}");
            starts[operationId] = start;

            if (operation.FixedStartMinute is { } fixedStart)
            {
                model.Add(start == Scale(fixedStart));
            }

            // A zero-length milestone consumes no capacity, so it must not enter the resource
            // constraint — otherwise it would hold a slot for an instant and could push real
            // work later for nothing.
            if (duration > 0L)
            {
                intervalsByResource[operation.ResourceKey].Add(
                    model.NewFixedSizeIntervalVar(start, duration, $"interval:{operationId}"));
            }
        }

        AddPrecedence(model, request, validated, starts);
        AddCapacity(model, request, intervalsByResource);

        var makespan = model.NewIntVar(0, horizon, "makespan");
        foreach (var operationId in order)
        {
            model.Add(makespan >= starts[operationId] + Scale(validated.Operations[operationId].DurationMinutes));
        }

        var solver = CreateSolver();
        model.Minimize(makespan);
        Search(solver, model, "makespan minimisation");

        // Phase two: keep the proven makespan, then start everything as early as it allows.
        model.Add(makespan <= solver.Value(makespan));
        model.Minimize(LinearExpr.Sum([.. order.Select(operationId => starts[operationId])]));
        Search(solver, model, "earliest-start refinement");

        return BuildSolution(request, validated, order, id => Unscale(solver.Value(starts[id])));
    }

    private SchedulingSolution BuildSolution(
        SchedulingRequest request,
        ValidatedSchedulingRequest validated,
        IReadOnlyList<string> order,
        Func<string, decimal> startOf)
    {
        var placed = new Dictionary<string, OperationPlan>(StringComparer.Ordinal);
        var diagnostics = new List<SchedulingDiagnostic>();

        foreach (var operationId in order)
        {
            var operation = validated.Operations[operationId];
            var start = startOf(operationId);

            if (!operation.EligibleForAutomaticPlanning)
            {
                diagnostics.Add(new SchedulingDiagnostic(
                    SchedulingDiagnosticCode.PlacedDespiteIncompleteStandard, operationId));
            }

            placed[operationId] = new OperationPlan
            {
                OperationId = operationId,
                Scope = operation.Scope,
                ResourceKey = operation.ResourceKey,
                StartMinute = start,

                // The plan carries the ORIGINAL duration, not the one rounded up onto the
                // solver's grid. The rounding exists to keep the reservation conservative;
                // letting it leak into the plan would restate the effort calculation's answer.
                FinishMinute = start + operation.DurationMinutes,
                AutomaticallyPlanned = operation.EligibleForAutomaticPlanning,
                StandardRevision = operation.StandardRevision,
                SourceRevisions = operation.SourceRevisions,
            };
        }

        var (edges, edgeDiagnostics) = DependencyProjection.Project(request, validated.Operations, placed);

        return new SchedulingSolution
        {
            Operations = [.. order.Select(id => placed[id])],
            Dependencies = edges,
            CalendarRevisions = request.Resources.ToDictionary(
                resource => resource.ResourceKey, resource => resource.CalendarRevision, StringComparer.Ordinal),
            Diagnostics = [.. diagnostics, .. edgeDiagnostics],
            IsReproducible = !options.AllowParallelSearch,
        };
    }

    /// <summary>Turns every dependency into a lower bound between two start variables.</summary>
    /// <remarks>
    /// Durations are fixed before the search, so a finish is just "start + duration" and every
    /// relation collapses into one start-to-start offset. The offsets themselves are read off
    /// <see cref="DependencyBoundResolver"/>'s rules rather than reinvented, and the whole
    /// offset is rounded up once so a rounding artefact can only ever delay work, never let it
    /// start before it is allowed to.
    /// </remarks>
    private void AddPrecedence(
        CpModel model,
        SchedulingRequest request,
        ValidatedSchedulingRequest validated,
        IReadOnlyDictionary<string, IntVar> starts)
    {
        var ordered = request.Dependencies
            .OrderBy(dependency => dependency.SuccessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.PredecessorOperationId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Relation);

        foreach (var dependency in ordered)
        {
            var predecessor = validated.Operations[dependency.PredecessorOperationId];
            var successor = validated.Operations[dependency.SuccessorOperationId];

            // A fixed start overrides the network entirely (resolver rule: fixed > release >
            // relation). Constraining it as well could make a request infeasible that the
            // planner deliberately overruled — and the override is reported, not swallowed.
            if (successor.FixedStartMinute.HasValue)
            {
                continue;
            }

            foreach (var offset in StartOffsets(dependency, predecessor, successor))
            {
                model.Add(starts[dependency.SuccessorOperationId]
                    >= starts[dependency.PredecessorOperationId] + Scale(offset));
            }
        }
    }

    /// <summary>The minimum distance between the two starts, in minutes.</summary>
    private static IEnumerable<decimal> StartOffsets(
        SolverDependency dependency,
        SolverOperation predecessor,
        SolverOperation successor)
    {
        // The start branch: a partial release replaces the relation's own bound — including
        // when it lands later, which is the settled business rule and is reported separately.
        if (dependency.ReleaseThresholdFraction is { } fraction)
        {
            yield return predecessor.DurationMinutes * fraction;
        }
        else if (dependency.Relation == DependencyType.FinishToStart)
        {
            yield return predecessor.DurationMinutes + dependency.LagMinutes;
        }
        else if (dependency.Relation == DependencyType.StartToStart)
        {
            yield return dependency.LagMinutes;
        }

        // The finish branch is independent of the start branch and is NOT overridden by a
        // release: an FF/SF edge constrains when the successor may FINISH.
        if (dependency.Relation == DependencyType.FinishToFinish)
        {
            yield return predecessor.DurationMinutes + dependency.LagMinutes - successor.DurationMinutes;
        }
        else if (dependency.Relation == DependencyType.StartToFinish)
        {
            yield return dependency.LagMinutes - successor.DurationMinutes;
        }
    }

    private static void AddCapacity(
        CpModel model,
        SchedulingRequest request,
        IReadOnlyDictionary<string, List<IntervalVar>> intervalsByResource)
    {
        foreach (var resource in request.Resources.OrderBy(resource => resource.ResourceKey, StringComparer.Ordinal))
        {
            var intervals = intervalsByResource[resource.ResourceKey];
            if (intervals.Count == 0)
            {
                continue;
            }

            // Capacity counts SIMULTANEOUS operations, so a fractional capacity means the
            // whole units of it: 2.5 admits two. Below one it still admits one, because the
            // reference strategy serialises such a resource rather than declaring it unusable,
            // and the two strategies must not disagree about what a request even means.
            var concurrent = Math.Max(1L, (long)Math.Floor(resource.Capacity));

            model.AddCumulative(concurrent).AddDemands(intervals, [.. intervals.Select(_ => 1L)]);
        }
    }

    private CpSolver CreateSolver()
    {
        var workers = options.AllowParallelSearch ? options.ParallelSearchWorkers : 1;

        return new CpSolver
        {
            StringParameters = string.Create(
                CultureInfo.InvariantCulture,
                $"random_seed:{options.RandomSeed},num_search_workers:{workers}," +
                $"max_time_in_seconds:{options.MaxSearchSeconds}"),
        };
    }

    private static void Search(CpSolver solver, CpModel model, string phase)
    {
        var status = solver.Solve(model);

        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
        {
            throw new InvalidOperationException(
                $"The CP-SAT search returned '{status}' during {phase}. The most common cause is " +
                "fixed starts that cannot all fit their resource's capacity; a plan violating " +
                "its own constraints would be worse than none.");
        }
    }

    /// <summary>An upper bound no feasible plan can exceed.</summary>
    /// <remarks>
    /// Deliberately loose — running every operation back to back, after the latest fixed
    /// start, plus every positive lag. A tight horizon would risk declaring a solvable request
    /// infeasible, which is a far worse failure than a slightly larger search space.
    /// </remarks>
    private long Horizon(SchedulingRequest request, ValidatedSchedulingRequest validated)
    {
        var work = validated.Operations.Values.Sum(operation => Scale(operation.DurationMinutes));
        var lags = request.Dependencies.Sum(dependency => Math.Max(0L, Scale(dependency.LagMinutes)));
        var latestFixedStart = validated.Operations.Values
            .Where(operation => operation.FixedStartMinute.HasValue)
            .Select(operation => Scale(operation.FixedStartMinute!.Value))
            .DefaultIfEmpty(0L)
            .Max();

        return work + lags + latestFixedStart + options.TimeScalePerMinute;
    }

    private long Scale(decimal minutes) => (long)Math.Ceiling(minutes * options.TimeScalePerMinute);

    private decimal Unscale(long units) => units / (decimal)options.TimeScalePerMinute;
}
