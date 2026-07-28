using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceOS.Modules.Scheduling.Domain.Resources;

/// <summary>A local time range inside a working day, stored as minutes since midnight.</summary>
/// <param name="StartMinuteOfDay">Inclusive start, 0..1439.</param>
/// <param name="EndMinuteOfDay">Exclusive end, 1..1440.</param>
/// <remarks>
/// Minutes-since-midnight rather than a time type: the domain stays free of NodaTime
/// (ADR-070 D2), and the calendar layer converts these to instants with the tenant's zone.
/// </remarks>
public readonly record struct DayRange(int StartMinuteOfDay, int EndMinuteOfDay)
{
    /// <summary>Length in minutes, ignoring calendar effects.</summary>
    public int NominalMinutes => EndMinuteOfDay - StartMinuteOfDay;

    /// <summary>True when this range and <paramref name="other"/> share any minute.</summary>
    public bool Overlaps(DayRange other) =>
        StartMinuteOfDay < other.EndMinuteOfDay && other.StartMinuteOfDay < EndMinuteOfDay;

    /// <summary>True when this range lies entirely inside <paramref name="outer"/>.</summary>
    public bool IsInside(DayRange outer) =>
        StartMinuteOfDay >= outer.StartMinuteOfDay && EndMinuteOfDay <= outer.EndMinuteOfDay;
}

/// <summary>One recurring shift of a resource calendar.</summary>
/// <param name="IsoWeekday">ISO weekday, 1 = Monday .. 7 = Sunday.</param>
/// <param name="Shift">The shift range in local time.</param>
/// <param name="Breaks">Non-schedulable interruptions inside the shift.</param>
public sealed record RecurringShift(int IsoWeekday, DayRange Shift, IReadOnlyList<DayRange> Breaks)
{
    /// <summary>Nominal net minutes: shift length minus breaks, before any DST effect.</summary>
    public int NominalNetMinutes => Shift.NominalMinutes - Breaks.Sum(pause => pause.NominalMinutes);
}

/// <summary>How a calendar treats fractional resource capacity.</summary>
public enum CapacityPolicy
{
    /// <summary>Whole units only; a fractional capacity is a data error.</summary>
    Integer,

    /// <summary>Fractional full-time-equivalent capacity is permitted.</summary>
    FractionalFte,
}

/// <summary>
/// An immutable, approved-or-draft revision of one resource's working calendar
/// (ADR-069 §4/§5: calendars are revisioned, and a change must not silently rewrite the
/// calendar an existing plan was computed against).
/// </summary>
public sealed class ResourceCalendarRevision
{
    private readonly List<RecurringShift> _shifts = [];

    private ResourceCalendarRevision(
        Guid id,
        Guid tenantId,
        string resourceKey,
        int revision,
        string timeZoneId,
        decimal capacity,
        CapacityPolicy capacityPolicy,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc)
    {
        Id = id;
        TenantId = tenantId;
        ResourceKey = resourceKey;
        Revision = revision;
        TimeZoneId = timeZoneId;
        Capacity = capacity;
        CapacityPolicy = capacityPolicy;
        EffectiveFromUtc = effectiveFromUtc;
        EffectiveToUtc = effectiveToUtc;
    }

    /// <summary>Revision identity.</summary>
    public Guid Id { get; }

    /// <summary>Owning tenant; mirrors the RLS predicate.</summary>
    public Guid TenantId { get; }

    /// <summary>Stable key of the resource this calendar belongs to.</summary>
    public string ResourceKey { get; } = string.Empty;

    /// <summary>Monotonic revision number, starting at 1.</summary>
    public int Revision { get; }

    /// <summary>IANA zone id (e.g. <c>Europe/Budapest</c>) — a plain string on the wire.</summary>
    public string TimeZoneId { get; } = string.Empty;

    /// <summary>How many units of work the resource can run in parallel.</summary>
    public decimal Capacity { get; }

    /// <summary>Whether fractional capacity is allowed.</summary>
    public CapacityPolicy CapacityPolicy { get; }

    /// <summary>Start of this revision's validity.</summary>
    public DateTimeOffset EffectiveFromUtc { get; }

    /// <summary>End of validity; null while this is the open-ended current revision.</summary>
    public DateTimeOffset? EffectiveToUtc { get; private set; }

    /// <summary>The recurring shifts, at most one per weekday.</summary>
    public IReadOnlyList<RecurringShift> Shifts => _shifts;

    /// <summary>True once a reviewer approved it; only an approved revision may be scheduled against.</summary>
    public bool IsApproved { get; private set; }

    /// <summary>Creates a draft revision.</summary>
    /// <exception cref="ArgumentException">Any invariant of the calendar shape is violated.</exception>
    public static ResourceCalendarRevision CreateDraft(
        Guid id,
        Guid tenantId,
        string resourceKey,
        int revision,
        string timeZoneId,
        decimal capacity,
        CapacityPolicy capacityPolicy,
        DateTimeOffset effectiveFromUtc,
        IReadOnlyList<RecurringShift> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A calendar revision must belong to a tenant.", nameof(tenantId));
        }
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            throw new ArgumentException("A calendar revision must name its resource.", nameof(resourceKey));
        }
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException(
                "A calendar revision must carry an IANA time zone: a shift is local time, and " +
                "without the zone it cannot be placed on the absolute timeline.", nameof(timeZoneId));
        }
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revisions start at 1.");
        }
        if (capacity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }
        if (capacityPolicy == CapacityPolicy.Integer && decimal.Truncate(capacity) != capacity)
        {
            throw new ArgumentException(
                $"Capacity {capacity} is fractional but the policy is Integer. Approve fractional " +
                "capacity explicitly rather than rounding it away.", nameof(capacity));
        }

        ValidateShifts(shifts);

        var instance = new ResourceCalendarRevision(
            id, tenantId, resourceKey, revision, timeZoneId, capacity, capacityPolicy, effectiveFromUtc, null);
        instance._shifts.AddRange(shifts);
        return instance;
    }

    /// <summary>Marks the revision approved, making it schedulable.</summary>
    /// <exception cref="InvalidOperationException">Already approved.</exception>
    public void Approve()
    {
        if (IsApproved)
        {
            throw new InvalidOperationException($"Calendar revision {Revision} is already approved.");
        }

        IsApproved = true;
    }

    /// <summary>Closes this revision's validity when a newer one takes over.</summary>
    /// <exception cref="ArgumentException">The end precedes the start.</exception>
    public void CloseAt(DateTimeOffset effectiveToUtc)
    {
        if (effectiveToUtc < EffectiveFromUtc)
        {
            throw new ArgumentException(
                "A calendar revision cannot end before it starts.", nameof(effectiveToUtc));
        }

        EffectiveToUtc = effectiveToUtc;
    }

    /// <summary>Nominal net minutes for an ISO weekday; 0 when the day has no shift.</summary>
    public int NominalNetMinutesOn(int isoWeekday) =>
        _shifts.FirstOrDefault(shift => shift.IsoWeekday == isoWeekday)?.NominalNetMinutes ?? 0;

    private static void ValidateShifts(IReadOnlyList<RecurringShift> shifts)
    {
        var duplicate = shifts.GroupBy(shift => shift.IsoWeekday).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"More than one shift defined for ISO weekday {duplicate.Key}.", nameof(shifts));
        }

        foreach (var shift in shifts)
        {
            if (shift.IsoWeekday is < 1 or > 7)
            {
                throw new ArgumentException(
                    $"ISO weekday must be 1..7, got {shift.IsoWeekday}.", nameof(shifts));
            }
            if (shift.Shift.StartMinuteOfDay < 0 || shift.Shift.EndMinuteOfDay > 1440)
            {
                throw new ArgumentException(
                    "A shift must lie within a single day (0..1440 minutes).", nameof(shifts));
            }
            if (shift.Shift.NominalMinutes <= 0)
            {
                throw new ArgumentException("A shift must end after it starts.", nameof(shifts));
            }

            foreach (var pause in shift.Breaks)
            {
                if (pause.NominalMinutes <= 0)
                {
                    throw new ArgumentException("A break must end after it starts.", nameof(shifts));
                }
                if (!pause.IsInside(shift.Shift))
                {
                    throw new ArgumentException("A break must lie inside its shift.", nameof(shifts));
                }
            }

            for (var index = 0; index < shift.Breaks.Count; index++)
            {
                for (var other = index + 1; other < shift.Breaks.Count; other++)
                {
                    if (shift.Breaks[index].Overlaps(shift.Breaks[other]))
                    {
                        // Overlapping breaks would subtract the same minutes twice and make
                        // the day look shorter than it is.
                        throw new ArgumentException("Two breaks overlap.", nameof(shifts));
                    }
                }
            }
        }
    }
}
