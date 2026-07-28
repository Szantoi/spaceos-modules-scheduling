using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpaceOS.Modules.Hosting.Modules;
using SpaceOS.Modules.Hosting.Persistence;
using SpaceOS.Modules.Hosting.Tenancy;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;

namespace SpaceOS.Modules.Scheduling.Host;

/// <summary>
/// Registers the scheduling module's persistence with the shared hosting contract
/// (ADR-061/062, PLAN-03 M2).
/// </summary>
public static class SchedulingModule
{
    /// <summary>Catalogue identity of this module (ADR-067 canonical ModuleId).</summary>
    public static ModuleDescriptor Descriptor { get; } = new(
        moduleId: "spaceos.scheduling",
        version: "0.1.0-preview.1",
        migrationsAssembly: typeof(SchedulingDbContext).Assembly.GetName().Name!);

    /// <summary>Adds the module DbContext with the shared tenant session interceptor.</summary>
    /// <exception cref="InvalidOperationException">The connection string is missing.</exception>
    public static IServiceCollection AddSchedulingModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Scheduling")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Scheduling is not configured. The module refuses to start " +
                "without it rather than silently falling back to a local default.");

        services.AddSingleton<SpaceOsTenantSessionInterceptor>();

        services.AddDbContext<SchedulingDbContext>((provider, options) =>
        {
            // The interceptor sets app.current_tenant_id from the authenticated principal on
            // every connection, and resets it when the connection returns to the pool. It is
            // what makes the RLS policies see a tenant at all.
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                    "__EFMigrationsHistory", SchedulingDbContext.SchemaName))
                .AddInterceptors(provider.GetRequiredService<SpaceOsTenantSessionInterceptor>());
        });

        // The EF query filter reads the ambient tenant through this delegate: the second
        // defence layer behind RLS, resolved per request rather than captured once.
        services.AddScoped(provider =>
        {
            var tenantContext = provider.GetRequiredService<ITenantContext>();
            return new Func<Guid?>(() => tenantContext.TenantId);
        });

        return services;
    }
}
