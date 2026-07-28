using System;
using System.Threading.Tasks;
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

        await CreateSchemaAsync();
        await ApplyRlsAsync();

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

    /// <summary>Opens an application-role connection with the tenant GUC already set.</summary>
    public async Task<NpgsqlConnection> OpenAsTenantAsync(Guid? tenantId)
    {
        var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await NonSuperuserRlsFixture.SetTenantAsync(connection, tenantId);
        return connection;
    }

    private async Task CreateSchemaAsync()
    {
        // Hand-written DDL rather than EnsureCreated: the proof must assert against exactly
        // the table and column names the RLS policies reference, and a silent EF naming
        // change would otherwise make the policies apply to nothing.
        const string ddl = """
            CREATE SCHEMA IF NOT EXISTS scheduling;

            CREATE TABLE scheduling."schedule_runs" (
                "id" uuid PRIMARY KEY,
                "tenant_id" uuid NOT NULL,
                "project_ref" uuid NOT NULL,
                "created_at_utc" timestamptz NOT NULL
            );

            CREATE TABLE scheduling."schedule_revisions" (
                "id" uuid PRIMARY KEY,
                "run_id" uuid NOT NULL REFERENCES scheduling."schedule_runs"("id") ON DELETE CASCADE,
                "sequence" integer NOT NULL,
                "content_hash" varchar(64) NOT NULL,
                "state" varchar(32) NOT NULL,
                "created_at_utc" timestamptz NOT NULL
            );

            CREATE TABLE scheduling."plan_operations" (
                "revision_id" uuid NOT NULL REFERENCES scheduling."schedule_revisions"("id") ON DELETE CASCADE,
                "operation_id" varchar(128) NOT NULL,
                "resource_key" varchar(128) NOT NULL,
                "start_minute" numeric(18,4) NOT NULL,
                "finish_minute" numeric(18,4) NOT NULL,
                "automatically_planned" boolean NOT NULL,
                PRIMARY KEY ("revision_id", "operation_id")
            );
            """;

        await ExecuteAsAdminAsync(ddl);
    }

    private async Task ApplyRlsAsync()
    {
        foreach (var statement in SchedulingRlsSql.Enable())
        {
            await ExecuteAsAdminAsync(statement);
        }
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
