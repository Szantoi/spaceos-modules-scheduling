using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;

#nullable disable

namespace SpaceOS.Modules.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchedulingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "capacity_reservations",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    project_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    epic_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    task_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capacity_reservations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operation_standards",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_task_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    qualifiers = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_standards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resource_calendar_revisions",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    capacity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    capacity_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    effective_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_approved = table.Column<bool>(type: "boolean", nullable: false),
                    shifts = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_calendar_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schedule_runs",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "standard_revisions",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    unit_minutes = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    workforce = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    default_dependency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    release_threshold_fraction = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    imported_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quarantine_reasons = table.Column<int[]>(type: "integer[]", nullable: false),
                    standard_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_standard_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_standard_revisions_operation_standards_standard_id",
                        column: x => x.standard_id,
                        principalSchema: "scheduling",
                        principalTable: "operation_standards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_exceptions",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    start_minute_of_day = table.Column<int>(type: "integer", nullable: true),
                    end_minute_of_day = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    calendar_revision_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_exceptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_calendar_exceptions_resource_calendar_revisions_calendar_re~",
                        column: x => x.calendar_revision_id,
                        principalSchema: "scheduling",
                        principalTable: "resource_calendar_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedule_revisions",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedule_revisions_schedule_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "scheduling",
                        principalTable: "schedule_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "operation_plans",
                schema: "scheduling",
                columns: table => new
                {
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    epic_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    task_ref = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    start_minute = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    finish_minute = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    automatically_planned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_plans", x => new { x.revision_id, x.operation_id });
                    table.ForeignKey(
                        name: "FK_operation_plans_schedule_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "scheduling",
                        principalTable: "schedule_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant_time",
                schema: "scheduling",
                table: "audit_entries",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_exceptions_revision_date",
                schema: "scheduling",
                table: "calendar_exceptions",
                columns: new[] { "calendar_revision_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_resource_interval",
                schema: "scheduling",
                table: "capacity_reservations",
                columns: new[] { "resource_key", "start_utc", "end_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_state_expiry",
                schema: "scheduling",
                table: "capacity_reservations",
                columns: new[] { "state", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_operation_plans_epic",
                schema: "scheduling",
                table: "operation_plans",
                column: "epic_ref");

            migrationBuilder.CreateIndex(
                name: "ix_standards_tenant_source_key",
                schema: "scheduling",
                table: "operation_standards",
                columns: new[] { "tenant_id", "source_task_key" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "scheduling",
                table: "outbox_messages",
                column: "occurred_at_utc",
                filter: "dispatched_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_calendar_resource_revision",
                schema: "scheduling",
                table: "resource_calendar_revisions",
                columns: new[] { "tenant_id", "resource_key", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_schedule_revisions_run_sequence",
                schema: "scheduling",
                table: "schedule_revisions",
                columns: new[] { "run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_schedule_runs_tenant_id",
                schema: "scheduling",
                table: "schedule_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_standard_revisions_standard_id",
                schema: "scheduling",
                table: "standard_revisions",
                column: "standard_id");

            // RLS belongs to the SCHEMA, not to a deploy step. Enable() is CALLED, not pasted,
            // so this migration and the RLS proof read from one source.
            foreach (var statement in SchedulingRlsSql.Enable())
            {
                migrationBuilder.Sql(statement, suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in SchedulingRlsSql.Disable())
            {
                migrationBuilder.Sql(statement, suppressTransaction: true);
            }

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "calendar_exceptions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "capacity_reservations",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "operation_plans",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "standard_revisions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "resource_calendar_revisions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "schedule_revisions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "operation_standards",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "schedule_runs",
                schema: "scheduling");
        }
    }
}
