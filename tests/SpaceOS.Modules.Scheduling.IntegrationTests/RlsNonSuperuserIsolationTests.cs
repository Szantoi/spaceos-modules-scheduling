using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;
using Xunit;

namespace SpaceOS.Modules.Scheduling.IntegrationTests;

/// <summary>
/// The PLAN-03 M2 tenant/RLS proof (audit §5.2): four facts, measured against a real
/// PostgreSQL through a NON-superuser role.
/// </summary>
/// <remarks>
/// These assertions run raw SQL on purpose. Asserting through EF would mostly measure the
/// EF query filter — the point here is what PostgreSQL itself enforces when the application
/// layer is bypassed or buggy.
/// </remarks>
[Collection(SchedulingRlsProofCollection.Name)]
public sealed class RlsNonSuperuserIsolationTests
{
    private readonly SchedulingRlsProofFixture _fixture;

    public RlsNonSuperuserIsolationTests(SchedulingRlsProofFixture fixture) => _fixture = fixture;

    // (a)
    [Fact]
    public async Task The_application_role_is_neither_superuser_nor_rls_bypassing()
    {
        var (isSuperuser, bypassesRls) = await _fixture.ReadApplicationRoleAsync();

        Assert.False(isSuperuser, "The application role must not be a superuser: superusers ignore RLS entirely.");
        Assert.False(bypassesRls, "The application role must not have BYPASSRLS: policies would not apply to it.");
    }

    // (b)
    [Fact]
    public async Task Every_scheduling_table_has_row_security_enabled_and_forced()
    {
        var catalogue = await _fixture.ReadForceRlsAsync();

        Assert.Equal(SchedulingRlsSql.AllTables.Count, catalogue.Count);
        foreach (var table in catalogue)
        {
            Assert.True(table.RelRowSecurity, $"{table.Table}: row security is not enabled.");
            // FORCE is the load-bearing half: without it the table owner silently bypasses
            // every policy, and the deploy role usually owns what it migrated.
            Assert.True(table.RelForceRowSecurity, $"{table.Table}: row security is not FORCED.");
        }
    }

    // (c)
    [Fact]
    public async Task A_tenant_sees_only_its_own_runs_and_an_unset_context_sees_nothing()
    {
        var runA = await SeedRunAsync(_fixture.TenantA);
        var runB = await SeedRunAsync(_fixture.TenantB);

        await using (var asTenantA = await _fixture.OpenAsTenantAsync(_fixture.TenantA))
        {
            var visible = await ReadRunIdsAsync(asTenantA);
            Assert.Contains(runA, visible);
            Assert.DoesNotContain(runB, visible);
        }

        await using (var asTenantB = await _fixture.OpenAsTenantAsync(_fixture.TenantB))
        {
            var visible = await ReadRunIdsAsync(asTenantB);
            Assert.Contains(runB, visible);
            Assert.DoesNotContain(runA, visible);
        }

        // Fail-closed: no tenant context must mean no rows, never "all rows".
        await using (var withoutTenant = await _fixture.OpenAsTenantAsync(null))
        {
            Assert.Empty(await ReadRunIdsAsync(withoutTenant));
        }
    }

    [Fact]
    public async Task A_pooled_connection_does_not_leak_the_previous_tenant()
    {
        // MaxPoolSize=1 forces the SECOND open to reuse the very connection the first one
        // set a tenant on. Without that, this test could pass simply by getting a fresh
        // physical connection -- proving nothing.
        //
        // Note on scope: this raw-SQL path measures Npgsql's own pool reset (DISCARD ALL),
        // not the hosting interceptor. Both matter, and the weaker of the two decides:
        // if the pool did not reset the GUC, a later request would silently read the
        // previous tenant's rows -- the worst failure mode, because nothing errors.
        var runA = await SeedRunAsync(_fixture.TenantA);
        var pool = _fixture.SingleConnectionPoolString;

        await using (var first = await _fixture.OpenAsTenantAsync(_fixture.TenantA, pool))
        {
            Assert.Contains(runA, await ReadRunIdsAsync(first));
        }

        await using var reused = new NpgsqlConnection(pool);
        await reused.OpenAsync();
        Assert.Empty(await ReadRunIdsAsync(reused));
    }

    // (d)
    [Fact]
    public async Task Child_rows_follow_their_parent_run_tenant()
    {
        var (_, revisionA) = await SeedRunWithRevisionAsync(_fixture.TenantA);
        var (_, revisionB) = await SeedRunWithRevisionAsync(_fixture.TenantB);

        await using var asTenantA = await _fixture.OpenAsTenantAsync(_fixture.TenantA);

        var revisions = await ReadIdsAsync(asTenantA, "SELECT \"id\" FROM scheduling.\"schedule_revisions\";");
        Assert.Contains(revisionA, revisions);
        Assert.DoesNotContain(revisionB, revisions);

        // Two hops deep: operations reach their tenant through the revision's run.
        var operationRevisions = await ReadIdsAsync(
            asTenantA, "SELECT \"revision_id\" FROM scheduling.\"operation_plans\";");
        Assert.Contains(revisionA, operationRevisions);
        Assert.DoesNotContain(revisionB, operationRevisions);
    }

    [Fact]
    public async Task A_tenant_cannot_write_a_row_belonging_to_another_tenant()
    {
        // WITH CHECK, not just USING: without it a tenant could INSERT rows it could never
        // read back -- silent cross-tenant data injection.
        await using var asTenantA = await _fixture.OpenAsTenantAsync(_fixture.TenantA);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO scheduling."schedule_runs" ("id","tenant_id","project_ref","created_at_utc")
            VALUES (@id, @tenant, @project, now());
            """, asTenantA);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", _fixture.TenantB);
        command.Parameters.AddWithValue("project", Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState); // insufficient_privilege — the policy refused
    }

    private async Task<Guid> SeedRunAsync(Guid tenantId)
    {
        var runId = Guid.NewGuid();
        await ExecuteAsAdminAsync(
            """
            INSERT INTO scheduling."schedule_runs" ("id","tenant_id","project_ref","created_at_utc")
            VALUES (@id, @tenant, @project, now());
            """,
            ("id", runId), ("tenant", tenantId), ("project", Guid.NewGuid()));
        return runId;
    }

    private async Task<(Guid RunId, Guid RevisionId)> SeedRunWithRevisionAsync(Guid tenantId)
    {
        var runId = await SeedRunAsync(tenantId);
        var revisionId = Guid.NewGuid();

        await ExecuteAsAdminAsync(
            """
            INSERT INTO scheduling."schedule_revisions" ("id","run_id","sequence","content_hash","state","created_at_utc")
            VALUES (@id, @run, 1, 'deadbeef', 'Proposal', now());
            """,
            ("id", revisionId), ("run", runId));

        await ExecuteAsAdminAsync(
            """
            INSERT INTO scheduling."operation_plans"
                ("revision_id","operation_id","resource_key","start_minute","finish_minute","automatically_planned")
            VALUES (@revision, 'op-1', 'resource-1', 0, 60, true);
            """,
            ("revision", revisionId));

        return (runId, revisionId);
    }

    private async Task ExecuteAsAdminAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static Task<Guid[]> ReadRunIdsAsync(NpgsqlConnection connection) =>
        ReadIdsAsync(connection, "SELECT \"id\" FROM scheduling.\"schedule_runs\";");

    private static async Task<Guid[]> ReadIdsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var ids = new System.Collections.Generic.List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }
        return [.. ids];
    }
}
