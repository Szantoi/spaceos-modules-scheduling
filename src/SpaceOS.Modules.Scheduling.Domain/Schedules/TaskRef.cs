using System;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Opaque reference to the Kernel task that a scheduled operation fulfils.
/// </summary>
/// <remarks>
/// This capability does not read or mutate Kernel task lifecycle state. The reference keeps
/// scheduling output traceable to the Project → FlowEpic → Task hierarchy while the published
/// Kernel handshake contract remains the authority for scope, access and revision validation.
/// </remarks>
public readonly record struct TaskRef
{
    private TaskRef(Guid value) => Value = value;

    /// <summary>Referenced task identifier.</summary>
    public Guid Value { get; }

    /// <summary>Creates a task reference from a non-empty Kernel identifier.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public static TaskRef From(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("A task reference cannot be empty.", nameof(value))
            : new TaskRef(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
