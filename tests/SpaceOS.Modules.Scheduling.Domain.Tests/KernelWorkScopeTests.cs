using System;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The scheduling core keeps Kernel identity opaque, complete and distinct from authorisation.
/// </summary>
public sealed class KernelWorkScopeTests
{
    private static readonly ProjectRef Project = ProjectRef.From(Guid.Parse("11111111-2222-4333-8444-555555555555"));
    private static readonly EpicRef Epic = EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666"));
    private static readonly TaskRef Task = TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777"));

    [Fact]
    public void A_scope_contains_the_complete_project_epic_task_chain()
    {
        var scope = KernelWorkScope.Create(Project, Epic, Task);

        Assert.Equal(Project, scope.Project);
        Assert.Equal(Epic, scope.Epic);
        Assert.Equal(Task, scope.Task);
    }

    [Fact]
    public void An_empty_task_identifier_is_not_a_reference()
    {
        Assert.Throws<ArgumentException>(() => TaskRef.From(Guid.Empty));
    }

    [Fact]
    public void A_scope_rejects_a_default_reference_even_if_a_deserialiser_created_it()
    {
        Assert.Throws<ArgumentException>(() => KernelWorkScope.Create(default, Epic, Task));
        Assert.Throws<ArgumentException>(() => KernelWorkScope.Create(Project, default, Task));
        Assert.Throws<ArgumentException>(() => KernelWorkScope.Create(Project, Epic, default));
    }

    [Fact]
    public void Moving_to_a_different_kernel_task_changes_the_scope()
    {
        var original = KernelWorkScope.Create(Project, Epic, Task);
        var moved = KernelWorkScope.Create(
            Project,
            Epic,
            TaskRef.From(Guid.Parse("44444444-5555-4666-8777-888888888888")));

        Assert.NotEqual(original, moved);
    }
}
