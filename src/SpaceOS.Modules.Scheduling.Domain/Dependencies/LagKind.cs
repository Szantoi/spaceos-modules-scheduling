namespace SpaceOS.Modules.Scheduling.Domain.Dependencies;

/// <summary>What a dependency's lag is measured in.</summary>
/// <remarks>
/// <para>
/// Two genuinely different things wear the same name (business owner decision, 2026-07-29).
/// An organisational delay — "the next shift picks it up" — is measured in WORKING time and
/// pauses when the resource does. A physical process — curing, drying, cooling — runs on the
/// clock: 48 hours means 48 hours, and it does not care that a weekend went past.
/// </para>
/// <para>
/// Treating them alike breaks in opposite directions. A curing time counted as working time
/// would hold work for days it did not need to; an organisational lag counted as elapsed time
/// would release work while nobody is there to take it.
/// </para>
/// </remarks>
public enum LagKind
{
    /// <summary>
    /// Working time on the successor's calendar; the default, and the meaning every existing
    /// dependency carries.
    /// </summary>
    WorkingTime,

    /// <summary>Real elapsed time, independent of any calendar.</summary>
    ElapsedTime,
}
