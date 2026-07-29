using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using SpaceOS.Modules.Scheduling.Host;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using SpaceOS.Modules.Scheduling.Solver.OrTools;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Host.Tests;

/// <summary>
/// Which scheduling strategy a host actually runs — a configuration decision, which is the
/// whole point of the port (ADR-070 D1).
/// </summary>
public sealed class SchedulingSolverRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(entry =>
                new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();

        return new ServiceCollection()
            .AddSchedulingSolver(configuration)
            .BuildServiceProvider();
    }

    [Fact]
    public void Without_configuration_the_reference_strategy_runs()
    {
        // Deliberate default: the reference strategy needs no native binaries, so a host on an
        // unmeasured base image starts and plans slightly worse instead of failing to start.
        using var provider = Build();

        Assert.IsType<DeterministicListSolver>(provider.GetRequiredService<ISchedulingSolver>());
    }

    [Fact]
    public void The_optimiser_is_selected_by_configuration_alone()
    {
        using var provider = Build((SchedulingSolverRegistration.StrategyKey, "cpsat"));

        Assert.IsType<CpSatSchedulingSolver>(provider.GetRequiredService<ISchedulingSolver>());
    }

    [Theory]
    [InlineData("CpSat")]
    [InlineData("  cpsat  ")]
    public void The_strategy_name_tolerates_casing_and_padding(string configured)
    {
        // A configuration value typed by a human, not a machine.
        using var provider = Build((SchedulingSolverRegistration.StrategyKey, configured));

        Assert.IsType<CpSatSchedulingSolver>(provider.GetRequiredService<ISchedulingSolver>());
    }

    [Fact]
    public void An_unknown_strategy_stops_the_host_instead_of_falling_back()
    {
        // A typo in "cpsat" that silently left the optimiser off would be found as "the plans
        // got worse at some point and nobody knows when".
        var error = Assert.Throws<InvalidOperationException>(() =>
            Build((SchedulingSolverRegistration.StrategyKey, "cp-sat")));

        Assert.Contains("cp-sat", error.Message, StringComparison.Ordinal);
        Assert.Contains("reference", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_optimiser_reads_its_own_options_from_configuration()
    {
        // The seed is part of the plan's identity (ADR-070 D3), so it must be operator-visible
        // and operator-settable — not compiled in.
        using var provider = Build(
            (SchedulingSolverRegistration.StrategyKey, "cpsat"),
            ($"{CpSatSolverOptions.SectionName}:RandomSeed", "4242"),
            ($"{CpSatSolverOptions.SectionName}:AllowParallelSearch", "true"));

        var solver = (CpSatSchedulingSolver)provider.GetRequiredService<ISchedulingSolver>();

        // The opt-in parallel search must carry through — its whole point is that the result
        // is then marked NOT reproducible.
        var solution = solver.Solve(new SchedulingRequest
        {
            Operations =
            [
                new SolverOperation
                {
                    OperationId = "a",
                    Scope = KernelWorkScope.Create(
                        ProjectRef.From(Guid.NewGuid()),
                        EpicRef.From(Guid.NewGuid()),
                        TaskRef.From(Guid.NewGuid())),
                    ResourceKey = "r1",
                    DurationMinutes = 60m,
                },
            ],
            Resources = [new SolverResource("r1", 1m, 1)],
        });

        Assert.False(solution.IsReproducible, "a párhuzamos keresés nem állíthat stabil identitást");
    }

    [Fact]
    public void The_calendar_aware_runner_is_available_over_whichever_strategy_runs()
    {
        using var provider = Build((SchedulingSolverRegistration.StrategyKey, "cpsat"));

        Assert.NotNull(provider.GetRequiredService<CalendarAwareScheduler>());
    }
}
