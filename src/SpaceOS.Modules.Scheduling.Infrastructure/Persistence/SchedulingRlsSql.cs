using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Resource calendars: revisioned, tenant-scoped directly.</summary>
    public const string CalendarsTable = "resource_calendar_revisions";

    /// <summary>Capacity reservations: tenant-scoped directly.</summary>
    public const string ReservationsTable = "capacity_reservations";

    /// <summary>Operation standards: tenant-scoped directly.</summary>
    public const string StandardsTable = "operation_standards";

    /// <summary>Standard revisions: tenant follows the parent standard.</summary>
    public const string StandardRevisionsTable = "standard_revisions";

    /// <summary>Append-only audit trail: tenant-scoped directly.</summary>
    public const string AuditTable = "audit_entries";

    /// <summary>Transactional outbox: tenant-scoped directly.</summary>
    public const string OutboxTable = "outbox_messages";

    /// <summary>Every table the proof suite must find FORCE-protected.</summary>
    /// <remarks>
    /// The model/RLS sync guard asserts this list against the EF model in both directions, so
    /// a table added to the schema without a policy fails the build rather than shipping
    /// readable across tenants.
    /// </remarks>
    public static IReadOnlyList<string> AllTables =>
    [
        RunsTable, RevisionsTable, OperationsTable,
        CalendarsTable,
        ReservationsTable,
        StandardsTable, StandardRevisionsTable,
        AuditTable, OutboxTable,
    ];

    /// <summary>Statements that remove the policies again (migration Down counterpart).</summary>
    /// <remarks>
    /// Derived from <see cref="AllTables"/> rather than hand-listed, so a table added to the
    /// schema cannot be left with a policy nobody knows how to remove.
    /// </remarks>
    public static IReadOnlyList<string> Disable(string schema = SchedulingDbContext.SchemaName) =>
    [
        .. AllTables.Select(table => RlsMigrationSql.DisableTenantRls(schema, table)),
        RlsMigrationSql.DropSetTenantContextFunction(schema),
    ];

    /// <summary>Statements that enable fail-closed isolation across the schema.</summary>
    public static IReadOnlyList<string> Enable(string schema = SchedulingDbContext.SchemaName) =>
    [
        RlsMigrationSql.CreateSetTenantContextFunction(schema),

        // Roots carry the tenant column directly.
        RlsMigrationSql.EnableTenantRls(schema, RunsTable, "tenant_id"),
        RlsMigrationSql.EnableTenantRls(schema, CalendarsTable, "tenant_id"),
        RlsMigrationSql.EnableTenantRls(schema, ReservationsTable, "tenant_id"),
        RlsMigrationSql.EnableTenantRls(schema, StandardsTable, "tenant_id"),
        RlsMigrationSql.EnableTenantRls(schema, AuditTable, "tenant_id"),
        RlsMigrationSql.EnableTenantRls(schema, OutboxTable, "tenant_id"),

        // Children reach their tenant through the parent row.
        RlsMigrationSql.EnableChildTenantRls(schema, RevisionsTable, "run_id", RunsTable, "id", "tenant_id"),
        RlsMigrationSql.EnableChildTenantRls(schema, StandardRevisionsTable, "standard_id", StandardsTable, "id", "tenant_id"),

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
