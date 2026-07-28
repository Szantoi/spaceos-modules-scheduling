using System;
using Microsoft.EntityFrameworkCore;
using SpaceOS.Modules.Scheduling.Domain.Schedules;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Persistence;

/// <summary>
/// Persistence for the scheduling module (ADR-069 §4, hosting pattern per ADR-061/062).
/// </summary>
/// <remarks>
/// <para>
/// Tenant isolation is enforced in TWO independent layers, on purpose:
/// PostgreSQL RLS (the authority — see the migration) and the EF query filter below
/// (defence in depth). Neither trusts the other: a forgotten filter still cannot read
/// another tenant's rows, and a missing policy is still caught by the RLS proof suite.
/// </para>
/// <para>
/// The tenant value comes from the session GUC <c>app.current_tenant_id</c>, set by the
/// shared <c>SpaceOsTenantSessionInterceptor</c> from the authenticated token — never from
/// a request header or a client-supplied field.
/// </para>
/// </remarks>
public sealed class SchedulingDbContext : DbContext
{
    /// <summary>The module's PostgreSQL schema.</summary>
    public const string SchemaName = "scheduling";

    private readonly Func<Guid?> _currentTenantId;

    /// <param name="options">EF options, including the tenant session interceptor.</param>
    /// <param name="currentTenantId">
    /// Resolves the ambient tenant for the query filter. A delegate rather than a captured
    /// value because a pooled context outlives a single request.
    /// </param>
    public SchedulingDbContext(DbContextOptions<SchedulingDbContext> options, Func<Guid?> currentTenantId)
        : base(options)
    {
        _currentTenantId = currentTenantId ?? throw new ArgumentNullException(nameof(currentTenantId));
    }

    /// <summary>Scheduling runs, the aggregate roots.</summary>
    public DbSet<ScheduleRun> ScheduleRuns => Set<ScheduleRun>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<ScheduleRun>(run =>
        {
            run.ToTable("schedule_runs");
            run.HasKey(entity => entity.Id);

            run.Property(entity => entity.Id).HasColumnName("id");
            run.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
            run.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            // ProjectRef stays opaque in the domain; on the wire it is just a uuid column.
            run.Property(entity => entity.Project)
                .HasColumnName("project_ref")
                .HasConversion(reference => reference.Value, value => ProjectRef.From(value))
                .IsRequired();

            run.HasIndex(entity => entity.TenantId).HasDatabaseName("ix_schedule_runs_tenant_id");

            // Second defence layer. A null ambient tenant matches nothing: fail-closed, the
            // same stance as the RLS predicate.
            run.HasQueryFilter(entity => entity.TenantId == _currentTenantId());

            run.Navigation(entity => entity.Revisions).UsePropertyAccessMode(PropertyAccessMode.Field);
            run.OwnsMany<ScheduleRevision>("_revisions", revision =>
            {
                revision.ToTable("schedule_revisions");
                revision.WithOwner().HasForeignKey("run_id");
                revision.HasKey(entity => entity.Id);

                revision.Property(entity => entity.Id).HasColumnName("id");
                revision.Property(entity => entity.Sequence).HasColumnName("sequence").IsRequired();
                revision.Property(entity => entity.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsRequired();
                revision.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

                // Enum as text: a schedule revision's state is read by humans in psql during
                // incidents, and an integer would silently shift meaning if the enum is reordered.
                revision.Property(entity => entity.State)
                    .HasColumnName("state")
                    .HasConversion<string>()
                    .HasMaxLength(32)
                    .IsRequired();

                revision.HasIndex("run_id", "sequence").IsUnique().HasDatabaseName("ux_schedule_revisions_run_sequence");

                revision.OwnsMany(entity => entity.Operations, operation =>
                {
                    operation.ToTable("plan_operations");
                    operation.WithOwner().HasForeignKey("revision_id");

                    operation.Property(entity => entity.OperationId).HasColumnName("operation_id").HasMaxLength(128).IsRequired();
                    operation.Property(entity => entity.ResourceKey).HasColumnName("resource_key").HasMaxLength(128).IsRequired();
                    operation.Property(entity => entity.StartMinute).HasColumnName("start_minute").HasPrecision(18, 4).IsRequired();
                    operation.Property(entity => entity.FinishMinute).HasColumnName("finish_minute").HasPrecision(18, 4).IsRequired();
                    operation.Property(entity => entity.AutomaticallyPlanned).HasColumnName("automatically_planned").IsRequired();

                    operation.HasKey("revision_id", "operation_id");
                });
            });
        });
    }
}
