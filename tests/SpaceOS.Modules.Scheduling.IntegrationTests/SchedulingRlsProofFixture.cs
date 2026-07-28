using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SpaceOS.Modules.Hosting.RlsFixtures;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;
using Xunit;

namespace SpaceOS.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Brings up a real PostgreSQL, creates the scheduling schema as the superuser, applies the
/// RLS statements, then hands the tests a NON-superuser application role.
/// </summary>
/// <remarks>
/// <para>
/// The superuser is used for DDL only. Every assertion runs as the application role, because
/// PostgreSQL superusers bypass RLS unconditionally — proving isolation with a superuser
/// connection would prove nothing at all.
/// </para>
/// <para>
/// Shared per test class (<see cref="ICollectionFixture{TFixture}"/>) so one container serves
/// the whole proof suite.
/// </para>
/// </remarks>
public sealed class SchedulingRlsProofFixture : IAsyncLifetime
{
    private NonSuperuserRlsFixture _inner = null!;

    /// <summary>Connection string for the non-superuser application role.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <summary>Connection string for the DDL/superuser role. Never used for assertions.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Tenant A, used as "our" tenant in isolation checks.</summary>
    public Guid TenantA { get; } = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    /// <summary>Tenant B, the one whose rows must never be visible to A.</summary>
    public Guid TenantB { get; } = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _inner = new NonSuperuserRlsFixture("scheduling_rls_proof");
        await _inner.StartAsync();

        AdminConnectionString = _inner.AdminConnectionString;

        await ApplyMigrationsAsync();

        // The role is created AFTER the DDL so it owns nothing: an owner would need FORCE RLS
        // to be constrained, and this way the test also proves the grant path works.
        await _inner.CreateApplicationRoleAsync(SchedulingDbContext.SchemaName);
        AppConnectionString = _inner.AppConnectionString();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _inner.DisposeAsync();

    /// <summary>Reads the catalogue state of the schema's tables (as the DDL role).</summary>
    public Task<System.Collections.Generic.IReadOnlyList<CatalogRlsState>> ReadForceRlsAsync() =>
        _inner.ReadForceRlsCatalogAsync(SchedulingDbContext.SchemaName, [.. SchedulingRlsSql.AllTables]);

    /// <summary>Reads whether the application role is superuser / bypasses RLS.</summary>
    public Task<(bool RolSuper, bool RolBypassRls)> ReadApplicationRoleAsync() =>
        _inner.ReadApplicationRolePropertiesAsync();

    /// <summary>
    /// A single-connection pool. Reuse is then guaranteed rather than incidental, so the
    /// leak test cannot pass merely because it happened to get a fresh connection.
    /// </summary>
    public string SingleConnectionPoolString => _inner.AppConnectionString(maxPoolSize: 1);

    /// <summary>Opens an application-role connection with the tenant GUC already set.</summary>
    public async Task<NpgsqlConnection> OpenAsTenantAsync(Guid? tenantId, string? connectionString = null)
    {
        var connection = new NpgsqlConnection(connectionString ?? AppConnectionString);
        await connection.OpenAsync();
        await NonSuperuserRlsFixture.SetTenantAsync(connection, tenantId);
        return connection;
    }

    private async Task ApplyMigrationsAsync()
    {
        // The real migration, not a hand-written copy of it. This is what makes the proof
        // meaningful: if the migration ever stops producing the tables or their policies,
        // every fact below fails here rather than passing against a schema only the test knows.
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(AdminConnectionString, npgsql => npgsql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SchedulingDbContext.SchemaName))
            .Options;

        await using var context = new SchedulingDbContext(options, () => null);
        await context.Database.MigrateAsync();
    }

    private async Task ExecuteAsAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Shares one container across the proof suite.</summary>
[CollectionDefinition(Name)]
public sealed class SchedulingRlsProofCollection : ICollectionFixture<SchedulingRlsProofFixture>
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "scheduling-rls-proof";
}
