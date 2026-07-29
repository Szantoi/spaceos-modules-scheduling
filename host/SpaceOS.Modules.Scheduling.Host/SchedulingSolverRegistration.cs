using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using SpaceOS.Modules.Scheduling.Solver.OrTools;

namespace SpaceOS.Modules.Scheduling.Host;

/// <summary>Which strategy the host runs behind <see cref="ISchedulingSolver"/>.</summary>
public enum SchedulingStrategy
{
    /// <summary>The deterministic list scheduler; no native binaries, always available.</summary>
    Reference,

    /// <summary>The CP-SAT optimiser; finds shorter plans, ships native binaries.</summary>
    CpSat,
}

/// <summary>
/// Chooses and registers the scheduling strategy (ADR-070 D1: the port exists so this is a
/// configuration decision, not a code change).
/// </summary>
/// <remarks>
/// <para>
/// <b>The default is the reference strategy, deliberately.</b> CP-SAT produces better plans —
/// measured, 160 minutes against 110 on the case a greedy pass handles worst — but it loads
/// native binaries, and the ADR-070 Alpine/musl question is still open. A host that starts and
/// plans slightly worse beats a host that will not start at all on an image nobody measured;
/// switching is one configuration key, and the operator makes that call knowing their image.
/// </para>
/// <para>
/// An unrecognised strategy name is refused at startup rather than silently falling back. A
/// typo in "cpsat" that quietly leaves the optimiser off would be discovered as "the plans got
/// worse and nobody knows when" — the worst way to find out.
/// </para>
/// </remarks>
public static class SchedulingSolverRegistration
{
    /// <summary>Configuration key selecting the strategy.</summary>
    public const string StrategyKey = "Scheduling:Solver:Strategy";

    /// <summary>Registers the configured strategy and the calendar-aware runner over it.</summary>
    /// <exception cref="InvalidOperationException">The configured strategy name is unknown.</exception>
    public static IServiceCollection AddSchedulingSolver(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[StrategyKey];
        var strategy = Parse(configured);

        if (strategy == SchedulingStrategy.CpSat)
        {
            var options = new CpSatSolverOptions();
            configuration.GetSection(CpSatSolverOptions.SectionName).Bind(options);

            // Singleton: the adapter holds only its options, and the native solver is created
            // per Solve call. A scoped registration would suggest per-request state that does
            // not exist.
            services.AddSingleton<ISchedulingSolver>(_ => new CpSatSchedulingSolver(options));
        }
        else
        {
            services.AddSingleton<ISchedulingSolver, DeterministicListSolver>();
        }

        // The calendar-aware runner is what a caller actually wants: it reconciles elapsed-time
        // lags against the resource calendars, whichever strategy sits underneath.
        services.AddSingleton(provider =>
            new CalendarAwareScheduler(provider.GetRequiredService<ISchedulingSolver>()));

        return services;
    }

    private static SchedulingStrategy Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return SchedulingStrategy.Reference;
        }

        return configured.Trim().ToLowerInvariant() switch
        {
            "reference" => SchedulingStrategy.Reference,
            "cpsat" => SchedulingStrategy.CpSat,
            _ => throw new InvalidOperationException(
                $"Unknown scheduling strategy '{configured}' in {StrategyKey}. Valid values are " +
                "'reference' and 'cpsat'. Refusing to start rather than guessing which plan " +
                "quality you meant to run with."),
        };
    }
}
