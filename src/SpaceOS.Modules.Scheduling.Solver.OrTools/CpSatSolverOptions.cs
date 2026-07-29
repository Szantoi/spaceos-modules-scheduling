namespace SpaceOS.Modules.Scheduling.Solver.OrTools;

/// <summary>Everything about the CP-SAT search that must be configurable rather than hidden.</summary>
/// <remarks>
/// The defaults are the reproducible profile required by ADR-070 D3. Anything that changes
/// the answer for the same input is a decision an operator has to be able to see and set —
/// which is why the seed and the parallel switch live here and not in the adapter's code.
/// </remarks>
public sealed record CpSatSolverOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Scheduling:Solver:CpSat";

    /// <summary>
    /// Fixed random seed. Part of the plan's identity: change it and the same input may
    /// produce a different — equally good — schedule.
    /// </summary>
    public int RandomSeed { get; init; } = 1;

    /// <summary>
    /// Opt-in parallel search. A solution found this way is marked NOT reproducible.
    /// </summary>
    /// <remarks>
    /// CP-SAT with several workers is a race between threads: same input, possibly a
    /// different optimum-cost plan. That is acceptable when someone explicitly trades
    /// reproducibility for speed, and never acceptable silently — the revision hash is quoted
    /// back by Doorstar as a stable identity.
    /// </remarks>
    public bool AllowParallelSearch { get; init; }

    /// <summary>Workers to use when <see cref="AllowParallelSearch"/> is on.</summary>
    public int ParallelSearchWorkers { get; init; } = 8;

    /// <summary>Wall-clock ceiling for one search phase, in seconds.</summary>
    public double MaxSearchSeconds { get; init; } = 30d;

    /// <summary>
    /// Integer units per minute on the solver's timeline (CP-SAT is integral).
    /// </summary>
    /// <remarks>
    /// 100 means hundredths of a minute (0.6 s), which covers the durations an effort
    /// calculation produces without inflating the search space. Durations and bounds that do
    /// not land exactly on this grid are rounded UP, never down: a plan may reserve slightly
    /// more than the work needs, but must never promise a slot shorter than reality.
    /// </remarks>
    public int TimeScalePerMinute { get; init; } = 100;
}
