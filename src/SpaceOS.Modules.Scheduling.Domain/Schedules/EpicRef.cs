using System;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Opaque reference to the Kernel epic an operation belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The customer hierarchy is <b>project → epics → operations</b>: a Doorstar project is
/// made up of epics, and every scheduled operation sits under one of them. A schedule run
/// therefore plans a project, while each <see cref="OperationPlan"/> records which epic its
/// work belongs to.
/// </para>
/// <para>
/// Opaque on purpose, exactly like <see cref="ProjectRef"/>: the scheduling module records
/// the identifier and nothing else. In particular it never reads the Kernel's epic scope,
/// which still carries industry-specific values (ADR-065) that must not leak into a
/// horizontal capability.
/// </para>
/// </remarks>
public readonly record struct EpicRef
{
    private EpicRef(Guid value) => Value = value;

    /// <summary>The referenced epic's identifier.</summary>
    public Guid Value { get; }

    /// <summary>Creates a reference; an empty GUID is not a reference.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public static EpicRef From(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("An epic reference cannot be empty.", nameof(value))
            : new EpicRef(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
