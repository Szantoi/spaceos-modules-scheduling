using System;

namespace SpaceOS.Modules.Scheduling.Domain.Schedules;

/// <summary>
/// Immutable, opaque link from scheduling work to its Kernel Project, FlowEpic and Task scope.
/// </summary>
/// <remarks>
/// <para>
/// This value object intentionally holds identifiers only. It is not a local copy of a Kernel
/// aggregate and it is not evidence that a caller is authorised. A host must validate the
/// published, versioned Kernel handshake before accepting this scope at an API boundary.
/// </para>
/// <para>
/// A record CLASS rather than a record struct: the persistence layer maps this as an owned
/// type across three columns, which Entity Framework supports for reference types only.
/// Structural equality — the property the domain actually relies on — is identical either way.
/// </para>
/// </remarks>
public sealed record KernelWorkScope
{
    private KernelWorkScope(ProjectRef project, EpicRef epic, TaskRef task)
    {
        Project = project;
        Epic = epic;
        Task = task;
    }

    /// <summary>Referenced Kernel project.</summary>
    public ProjectRef Project { get; }

    /// <summary>Referenced Kernel FlowEpic.</summary>
    public EpicRef Epic { get; }

    /// <summary>Referenced Kernel task.</summary>
    public TaskRef Task { get; }

    /// <summary>
    /// Creates a complete work scope. A default value from deserialisation is never a valid
    /// scheduling scope and must be rejected at this boundary.
    /// </summary>
    /// <exception cref="ArgumentException">Any referenced Kernel identifier is empty.</exception>
    public static KernelWorkScope Create(ProjectRef project, EpicRef epic, TaskRef task)
    {
        if (project.Value == Guid.Empty)
        {
            throw new ArgumentException("A Kernel work scope needs a project reference.", nameof(project));
        }

        if (epic.Value == Guid.Empty)
        {
            throw new ArgumentException("A Kernel work scope needs an epic reference.", nameof(epic));
        }

        if (task.Value == Guid.Empty)
        {
            throw new ArgumentException("A Kernel work scope needs a task reference.", nameof(task));
        }

        return new KernelWorkScope(project, epic, task);
    }

    /// <summary>
    /// Returns a copy that belongs to exactly one row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A work scope is a VALUE — two operations on the same Kernel task hold equal scopes,
    /// and callers naturally pass the same instance to both. The persistence layer maps it as
    /// an OWNED ENTITY though (EF 8 complex types are unavailable on an owned type, which an
    /// operation is), and an owned entity has identity: a shared instance is attached to one
    /// owner and the other row is written with NULL references. Silently, at insert time.
    /// </para>
    /// <para>
    /// So every aggregate isolates the scope on the way in. Equality is unaffected — this is
    /// a record — which is exactly why the fix is invisible to the domain and to the hash.
    /// </para>
    /// </remarks>
    internal KernelWorkScope Isolated() => this with { };

    /// <inheritdoc />
    public override string ToString() => $"{Project}/{Epic}/{Task}";
}
