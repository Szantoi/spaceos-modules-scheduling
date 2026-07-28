using System;
using SpaceOS.Modules.Scheduling.Domain.Schedules;

namespace SpaceOS.Modules.Scheduling.Domain.Resources;

/// <summary>Lifecycle of a resource-time reservation.</summary>
public enum CapacityReservationState
{
    /// <summary>Provisionally held; expires unless confirmed before its TTL runs out.</summary>
    Held,

    /// <summary>Confirmed; it no longer expires and only an explicit release frees it.</summary>
    Confirmed,

    /// <summary>Explicitly released. Terminal.</summary>
    Released,

    /// <summary>Expired without confirmation. Terminal.</summary>
    Expired,
}

/// <summary>
/// A hold on a resource's TIME (ADR-069 §4).
/// </summary>
/// <remarks>
/// <para>
/// The name is deliberately not "Reservation": the Inventory module already owns that word
/// for MATERIAL reservations, and the audit (§4.4) records the collision as a real hazard.
/// Two different things called the same name in one platform eventually get wired to each
/// other by mistake.
/// </para>
/// <para>
/// The state machine follows the Inventory precedent (held → confirmed/released/expired,
/// with a TTL on the held state) so operators meet one behaviour, not two. Expiry is a
/// separate act, never an implicit side effect of reading: a reservation does not silently
/// become free while someone is looking at it.
/// </para>
/// </remarks>
public sealed class CapacityReservation
{
    private CapacityReservation(
        Guid id,
        Guid tenantId,
        string resourceKey,
        KernelWorkScope scope,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        decimal quantity,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        ResourceKey = resourceKey;
        Scope = scope;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Quantity = quantity;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        State = CapacityReservationState.Held;
    }

    /// <summary>Reservation identity.</summary>
    public Guid Id { get; }

    /// <summary>Owning tenant; mirrors the RLS predicate.</summary>
    public Guid TenantId { get; }

    /// <summary>The resource whose time is held.</summary>
    public string ResourceKey { get; } = string.Empty;

    /// <summary>The Kernel work this reservation serves.</summary>
    public KernelWorkScope Scope { get; } = null!;

    /// <summary>Start of the held interval (UTC).</summary>
    public DateTimeOffset StartUtc { get; }

    /// <summary>End of the held interval (UTC), exclusive.</summary>
    public DateTimeOffset EndUtc { get; }

    /// <summary>How much of the resource's capacity is held.</summary>
    public decimal Quantity { get; }

    /// <summary>When the hold was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>When an unconfirmed hold stops being valid.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Current lifecycle state.</summary>
    public CapacityReservationState State { get; private set; }

    /// <summary>True once the reservation can no longer change.</summary>
    public bool IsTerminal =>
        State is CapacityReservationState.Released or CapacityReservationState.Expired;

    /// <summary>True while the reservation actually occupies capacity.</summary>
    public bool OccupiesCapacity =>
        State is CapacityReservationState.Held or CapacityReservationState.Confirmed;

    /// <summary>Places a hold.</summary>
    /// <exception cref="ArgumentException">Tenant, resource, interval or TTL is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quantity is not positive.</exception>
    public static CapacityReservation Hold(
        Guid id,
        Guid tenantId,
        string resourceKey,
        KernelWorkScope scope,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        decimal quantity,
        DateTimeOffset createdAtUtc,
        TimeSpan timeToLive)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A reservation must belong to a tenant.", nameof(tenantId));
        }
        if (string.IsNullOrWhiteSpace(resourceKey))
        {
            throw new ArgumentException("A reservation must name its resource.", nameof(resourceKey));
        }
        if (endUtc <= startUtc)
        {
            // A zero-length hold occupies nothing yet blocks bookkeeping; it is a caller bug.
            throw new ArgumentException("The held interval must end after it starts.", nameof(endUtc));
        }
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");
        }
        if (timeToLive <= TimeSpan.Zero)
        {
            // Without a positive TTL the hold would be born expired and silently free capacity
            // that the caller believes it holds.
            throw new ArgumentException("A hold needs a positive time to live.", nameof(timeToLive));
        }

        return new CapacityReservation(
            id, tenantId, resourceKey, scope, startUtc, endUtc, quantity,
            createdAtUtc, createdAtUtc + timeToLive);
    }

    /// <summary>Confirms the hold, removing its expiry.</summary>
    /// <exception cref="InvalidOperationException">Not in <see cref="CapacityReservationState.Held"/>.</exception>
    public void Confirm()
    {
        if (State != CapacityReservationState.Held)
        {
            throw new InvalidOperationException($"Only a held reservation can be confirmed; this one is {State}.");
        }

        State = CapacityReservationState.Confirmed;
    }

    /// <summary>Releases the reservation, freeing the capacity.</summary>
    /// <exception cref="InvalidOperationException">Already terminal.</exception>
    public void Release()
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException($"A {State} reservation cannot be released again.");
        }

        State = CapacityReservationState.Released;
    }

    /// <summary>
    /// Expires an unconfirmed hold whose TTL has passed. Intended for the expiry worker.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The reservation is confirmed, already terminal, or its TTL has not passed yet.
    /// </exception>
    public void Expire(DateTimeOffset nowUtc)
    {
        if (State != CapacityReservationState.Held)
        {
            // A confirmed reservation must never expire behind the operator's back.
            throw new InvalidOperationException($"Only a held reservation can expire; this one is {State}.");
        }
        if (nowUtc < ExpiresAtUtc)
        {
            throw new InvalidOperationException(
                $"The reservation is valid until {ExpiresAtUtc:O}; expiring it at {nowUtc:O} would " +
                "free capacity a caller still believes it holds.");
        }

        State = CapacityReservationState.Expired;
    }

    /// <summary>True when this reservation and <paramref name="other"/> share resource time.</summary>
    /// <remarks>
    /// Only reservations that still occupy capacity can conflict — a released or expired one
    /// is history, and treating it as a conflict would block the slot forever.
    /// </remarks>
    public bool ConflictsWith(CapacityReservation other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return OccupiesCapacity
            && other.OccupiesCapacity
            && string.Equals(ResourceKey, other.ResourceKey, StringComparison.Ordinal)
            && StartUtc < other.EndUtc
            && other.StartUtc < EndUtc;
    }
}
