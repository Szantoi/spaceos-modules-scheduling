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
                    epic_ref = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "ix_operation_plans_revision_epic",
                schema: "scheduling",
                table: "operation_plans",
                columns: new[] { "revision_id", "epic_ref" });

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

            // Row-level security is part of the SCHEMA, not a deploy-time afterthought: a
            // table that exists without its policy is readable across tenants for as long as
            // nobody notices. Calling SchedulingRlsSql.Enable() rather than pasting the SQL
            // keeps this migration and the RLS proof reading from one source.
            foreach (var statement in SchedulingRlsSql.Enable())
            {
                migrationBuilder.Sql(statement, suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Policies first: an explicit disable keeps Down() readable and works even on a
            // partially applied schema.
            foreach (var statement in SchedulingRlsSql.Disable())
            {
                migrationBuilder.Sql(statement, suppressTransaction: true);
            }

            migrationBuilder.DropTable(
                name: "operation_plans",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "schedule_revisions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "schedule_runs",
                schema: "scheduling");
        }
    }
}
