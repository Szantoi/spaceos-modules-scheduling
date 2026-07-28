using System;

namespace SpaceOS.Modules.Scheduling.Domain.Resources;

/// <summary>How a calendar exception changes a day's working time.</summary>
public enum CalendarExceptionKind
{
    /// <summary>The resource does not work: a holiday, a plant shutdown.</summary>
    Closure,

    /// <summary>The resource is unavailable for planned maintenance.</summary>
    Maintenance,

    /// <summary>Extra working time beyond the recurring shift.</summary>
    Overtime,
}

/// <summary>
/// A dated deviation from a resource's recurring shift pattern (ADR-069 §4).
/// </summary>
/// <remarks>
/// <para>
/// Without exceptions a calendar is a lie the moment the plant closes: every calculation
/// that counts working time — the release threshold above all — would happily place work
/// inside a shutdown. That is the same failure class the calculator already refuses when a
/// day has no shift at all, so it must not sneak back in through an unmodelled holiday.
/// </para>
/// <para>
/// <see cref="Span"/> is optional for a removal (null means the whole day) and required for
/// overtime: extra time with no span would be impossible to place.
/// </para>
/// <para>
/// The wording here says "span" throughout. Its common synonym names a product in the
/// industry taxonomy, so the ADR-067 guard rejects it — here the neutral term is also the
/// only one that compiles.
/// </para>
/// </remarks>
public sealed class CalendarException
{
    private CalendarException(
        Guid id,
        DateOnly date,
        CalendarExceptionKind kind,
        DayRange? span,
        string? reason)
    {
        Id = id;
        Date = date;
        Kind = kind;
        Span = span;
        Reason = reason;
    }

    /// <summary>Materialisation constructor for the persistence layer only.</summary>
    private CalendarException()
    {
    }

    /// <summary>Exception identity.</summary>
    public Guid Id { get; }

    /// <summary>The local date the exception applies to.</summary>
    public DateOnly Date { get; }

    /// <summary>Whether the exception removes or adds working time.</summary>
    public CalendarExceptionKind Kind { get; }

    /// <summary>The affected local span; null means the whole day (removals only).</summary>
    public DayRange? Span { get; }

    /// <summary>Optional human-readable justification, shown to planners.</summary>
    public string? Reason { get; }

    /// <summary>True when the exception takes working time away.</summary>
    public bool RemovesTime => Kind is CalendarExceptionKind.Closure or CalendarExceptionKind.Maintenance;

    /// <summary>Creates an exception.</summary>
    /// <exception cref="ArgumentException">
    /// Overtime without a span, or a span that does not lie inside a single day.
    /// </exception>
    public static CalendarException Create(
        Guid id,
        DateOnly date,
        CalendarExceptionKind kind,
        DayRange? span = null,
        string? reason = null)
    {
        if (kind == CalendarExceptionKind.Overtime && span is null)
        {
            throw new ArgumentException(
                "Overtime needs an explicit span: extra time with no place to put it cannot " +
                "be scheduled.", nameof(span));
        }

        if (span is not null)
        {
            if (span.StartMinuteOfDay < 0 || span.EndMinuteOfDay > 1440)
            {
                throw new ArgumentException("An exception span must lie within a single day.", nameof(span));
            }
            if (span.NominalMinutes <= 0)
            {
                throw new ArgumentException("An exception span must end after it starts.", nameof(span));
            }
        }

        return new CalendarException(id, date, kind, span, reason);
    }
}
