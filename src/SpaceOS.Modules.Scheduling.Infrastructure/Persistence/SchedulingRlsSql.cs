using System.Collections.Generic;
using SpaceOS.Modules.Hosting.Persistence;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Persistence;

/// <summary>
/// The scheduling schema's row-level-security statements, built from the shared
/// <see cref="RlsMigrationSql"/> template (ADR-062, audit §5.2).
/// </summary>
/// <remarks>
/// Every tenant-scoped table gets ENABLE + <b>FORCE</b> RLS. FORCE is the part that
/// actually matters: plain ENABLE does not apply to the table owner, and the deploy role
/// usually owns what it migrates — without it the policies are silently inert.
/// </remarks>
public static class SchedulingRlsSql
{
    /// <summary>Tables that carry the tenant column directly.</summary>
    public const string RunsTable = "schedule_runs";

    /// <summary>Revisions: tenant follows the parent run.</summary>
    public const string RevisionsTable = "schedule_revisions";

    /// <summary>Operations: tenant follows the revision's run — two hops.</summary>
    public const string OperationsTable = "operation_plans";

    /// <summary>Every table the proof suite must find FORCE-protected.</summary>
    public static IReadOnlyList<string> AllTables => [RunsTable, RevisionsTable, OperationsTable];

    /// <summary>Statements that enable fail-closed isolation across the schema.</summary>
    public static IReadOnlyList<string> Enable(string schema = SchedulingDbContext.SchemaName) =>
    [
        RlsMigrationSql.CreateSetTenantContextFunction(schema),
        RlsMigrationSql.EnableTenantRls(schema, RunsTable, "tenant_id"),
        RlsMigrationSql.EnableChildTenantRls(schema, RevisionsTable, "run_id", RunsTable, "id", "tenant_id"),
        // Two levels deep, so the shared single-hop helper does not fit: operations reach
        // their tenant through the revision. Written in the same shape as the helper output
        // (ENABLE + FORCE + one combined USING/WITH CHECK policy) so the proof suite and a
        // human reading psql see a consistent pattern.
        $"""
        ALTER TABLE {schema}."{OperationsTable}" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {schema}."{OperationsTable}" FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS "{OperationsTable}_tenant_isolation" ON {schema}."{OperationsTable}";
        CREATE POLICY "{OperationsTable}_tenant_isolation" ON {schema}."{OperationsTable}"
            USING (EXISTS (
                SELECT 1
                FROM {schema}."{RevisionsTable}" revision
                JOIN {schema}."{RunsTable}" run ON run."id" = revision."run_id"
                WHERE revision."id" = {schema}."{OperationsTable}"."revision_id"
                  AND run."tenant_id" = {RlsMigrationSql.CurrentTenantExpression}))
            WITH CHECK (EXISTS (
                SELECT 1
                FROM {schema}."{RevisionsTable}" revision
                JOIN {schema}."{RunsTable}" run ON run."id" = revision."run_id"
                WHERE revision."id" = {schema}."{OperationsTable}"."revision_id"
                  AND run."tenant_id" = {RlsMigrationSql.CurrentTenantExpression}));
        """,
    ];
}
