using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> construct the context without a running application.
/// </summary>
/// <remarks>
/// <para>
/// The real context takes a tenant resolver, which the tooling cannot supply. Here it
/// always returns null: design-time work (scaffolding, migration diffing) only reads the
/// MODEL, never data, and a null ambient tenant is the fail-closed value anyway.
/// </para>
/// <para>
/// The connection string is a placeholder — <c>dotnet ef migrations</c> does not connect.
/// A real deployment supplies its own via configuration; override with
/// <c>SCHEDULING_DESIGN_TIME_CONNECTION</c> when a command genuinely needs a database
/// (for example <c>dotnet ef database update</c> against a local instance).
/// </para>
/// </remarks>
public sealed class SchedulingDesignTimeFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    private const string PlaceholderConnection =
        "Host=localhost;Port=5432;Database=scheduling_design_time;Username=postgres;Password=postgres";

    /// <inheritdoc />
    public SchedulingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SCHEDULING_DESIGN_TIME_CONNECTION")
            ?? PlaceholderConnection;

        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SchedulingDbContext.SchemaName))
            .Options;

        return new SchedulingDbContext(options, () => null);
    }
}
