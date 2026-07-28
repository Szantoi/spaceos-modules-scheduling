using System;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Opaque reference to the owning project in the SpaceOS Kernel.
/// </summary>
/// <remarks>
/// PLAN-03 hard rule: the Kernel link is an opaque reference and nothing else. This type
/// deliberately exposes no Kernel concept, carries no navigation and resolves nothing —
/// the scheduling module never reads Kernel state, it only records which project a run
/// belongs to. Keeping it opaque is what stops a Kernel dependency from creeping in.
/// </remarks>
public readonly record struct ProjectRef
{
    private ProjectRef(Guid value) => Value = value;

    /// <summary>The referenced project's identifier.</summary>
    public Guid Value { get; }

    /// <summary>Creates a reference; an empty GUID is not a reference.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public static ProjectRef From(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("A project reference cannot be empty.", nameof(value))
            : new ProjectRef(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
