using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SpaceOS.Modules.Scheduling.Domain.Audit;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Standards;

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

    /// <summary>Revisioned resource calendars.</summary>
    public DbSet<ResourceCalendarRevision> CalendarRevisions => Set<ResourceCalendarRevision>();

    /// <summary>Resource-time reservations.</summary>
    public DbSet<CapacityReservation> CapacityReservations => Set<CapacityReservation>();

    /// <summary>Versioned operation norm times.</summary>
    public DbSet<OperationStandard> OperationStandards => Set<OperationStandard>();

    /// <summary>Append-only audit trail.</summary>
    public DbSet<SchedulingAuditEntry> AuditEntries => Set<SchedulingAuditEntry>();

    /// <summary>Transactional outbox.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

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

            // Mapped through the public navigation, not the "_revisions" field name: naming
            // the field left EF also discovering the read-only Revisions property, and it
            // could not tell the two apart. Field ACCESS is still what happens at runtime
            // (see the Navigation call below), so the aggregate keeps its private list.
            run.OwnsMany(entity => entity.Revisions, revision =>
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

                // Property names, not column names: "run_id" is the shadow FK property
                // declared above, but the sequence is the CLR property Sequence. Passing the
                // column name here throws at model build -- which nothing noticed until the
                // model/RLS sync guard started building the model.
                revision.HasIndex("run_id", nameof(ScheduleRevision.Sequence))
                    .IsUnique()
                    .HasDatabaseName("ux_schedule_revisions_run_sequence");

                revision.OwnsMany(entity => entity.Operations, operation =>
                {
                    operation.ToTable("operation_plans");
                    operation.WithOwner().HasForeignKey("revision_id");

                    operation.Property(entity => entity.OperationId).HasColumnName("operation_id").HasMaxLength(128).IsRequired();

                    // The Kernel scope is three opaque uuids. Stored as plain columns and
                    // never joined to: the module records identity, it does not resolve it.
                    // The project is denormalised onto the operation (the run carries it too)
                    // so a row is self-contained for reads and for materialisation.
                    operation.OwnsOne(entity => entity.Scope, scope =>
                    {
                        scope.Property(value => value.Project)
                            .HasColumnName("project_ref")
                            .HasConversion(reference => reference.Value, value => ProjectRef.From(value))
                            .IsRequired();
                        scope.Property(value => value.Epic)
                            .HasColumnName("epic_ref")
                            .HasConversion(reference => reference.Value, value => EpicRef.From(value))
                            .IsRequired();
                        scope.Property(value => value.Task)
                            .HasColumnName("task_ref")
                            .HasConversion(reference => reference.Value, value => TaskRef.From(value))
                            .IsRequired();

                        // Declared on the OWNED builder: an index over an owned property is not
                        // reachable from the parent, so a composite (revision_id, epic_ref)
                        // cannot be expressed here. Single-column is enough in practice — the
                        // primary key already leads with revision_id, so a revision-scoped read
                        // is covered, and this index serves the "everything for one epic" query.
                        scope.HasIndex(value => value.Epic).HasDatabaseName("ix_operation_plans_epic");
                    });

                    operation.Property(entity => entity.ResourceKey).HasColumnName("resource_key").HasMaxLength(128).IsRequired();
                    operation.Property(entity => entity.StartMinute).HasColumnName("start_minute").HasPrecision(18, 4).IsRequired();
                    operation.Property(entity => entity.FinishMinute).HasColumnName("finish_minute").HasPrecision(18, 4).IsRequired();
                    operation.Property(entity => entity.AutomaticallyPlanned).HasColumnName("automatically_planned").IsRequired();

                    // Same rule as the index above: the shadow FK is named "revision_id",
                    // but the operation id is the CLR property OperationId.
                    operation.HasKey("revision_id", nameof(OperationPlan.OperationId));
                });
            });

            run.Navigation(entity => entity.Revisions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ResourceCalendarRevision>(calendar =>
        {
            calendar.ToTable(SchedulingRlsSql.CalendarsTable);
            calendar.HasKey(entity => entity.Id);

            calendar.Property(entity => entity.Id).HasColumnName("id");
            calendar.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
            calendar.Property(entity => entity.ResourceKey).HasColumnName("resource_key").HasMaxLength(128).IsRequired();
            calendar.Property(entity => entity.Revision).HasColumnName("revision").IsRequired();
            calendar.Property(entity => entity.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64).IsRequired();
            calendar.Property(entity => entity.Capacity).HasColumnName("capacity").HasPrecision(18, 4).IsRequired();
            calendar.Property(entity => entity.CapacityPolicy).HasColumnName("capacity_policy").HasConversion<string>().HasMaxLength(32).IsRequired();
            calendar.Property(entity => entity.EffectiveFromUtc).HasColumnName("effective_from_utc").IsRequired();
            calendar.Property(entity => entity.EffectiveToUtc).HasColumnName("effective_to_utc");
            calendar.Property(entity => entity.IsApproved).HasColumnName("is_approved").IsRequired();

            calendar.HasIndex(entity => new { entity.TenantId, entity.ResourceKey, entity.Revision })
                .IsUnique()
                .HasDatabaseName("ux_calendar_resource_revision");

            calendar.HasQueryFilter(entity => entity.TenantId == _currentTenantId());

            // The whole shift pattern lives in ONE json column, not a side table: shifts are
            // always read with their calendar and never queried alone, so a table would add a
            // join for nothing. It also keeps the pattern inside the parent row, which means
            // the calendar's own RLS policy covers it with no second predicate to maintain.
            calendar.OwnsMany(entity => entity.Shifts, shift =>
            {
                shift.ToJson("shifts");
                shift.OwnsOne(entity => entity.Shift);
                shift.OwnsMany(entity => entity.Breaks);
            });
            calendar.Navigation(entity => entity.Shifts).UsePropertyAccessMode(PropertyAccessMode.Field);

            // Exceptions get their OWN TABLE, unlike the shift pattern that sits in json.
            // The difference is how they are used: the weekly pattern is fixed-size and always
            // read whole, while exceptions accumulate for years and are read by DATE RANGE
            // (the calculator wants the ones overlapping one job). In json every read would
            // load and scan every closure ever recorded for that resource; a table gives an
            // index on (calendar_revision_id, date) and a child RLS policy of its own.
            calendar.OwnsMany(entity => entity.Exceptions, exception =>
            {
                exception.ToTable(SchedulingRlsSql.CalendarExceptionsTable);
                exception.WithOwner().HasForeignKey("calendar_revision_id");
                exception.HasKey(entity => entity.Id);

                exception.Property(entity => entity.Id).HasColumnName("id");
                exception.Property(entity => entity.Date).HasColumnName("date").IsRequired();
                exception.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32).IsRequired();
                exception.Property(entity => entity.Reason).HasColumnName("reason").HasMaxLength(512);

                exception.OwnsOne(entity => entity.Span, span =>
                {
                    span.Property(value => value.StartMinuteOfDay).HasColumnName("start_minute_of_day");
                    span.Property(value => value.EndMinuteOfDay).HasColumnName("end_minute_of_day");
                });

                exception.HasIndex("calendar_revision_id", nameof(CalendarException.Date))
                    .HasDatabaseName("ix_calendar_exceptions_revision_date");
            });
            calendar.Navigation(entity => entity.Exceptions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CapacityReservation>(reservation =>
        {
            reservation.ToTable(SchedulingRlsSql.ReservationsTable);
            reservation.HasKey(entity => entity.Id);

            reservation.Property(entity => entity.Id).HasColumnName("id");
            reservation.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
            reservation.Property(entity => entity.ResourceKey).HasColumnName("resource_key").HasMaxLength(128).IsRequired();
            reservation.Property(entity => entity.StartUtc).HasColumnName("start_utc").IsRequired();
            reservation.Property(entity => entity.EndUtc).HasColumnName("end_utc").IsRequired();
            reservation.Property(entity => entity.Quantity).HasColumnName("quantity").HasPrecision(18, 4).IsRequired();
            reservation.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            reservation.Property(entity => entity.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
            reservation.Property(entity => entity.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32).IsRequired();

            reservation.OwnsOne(entity => entity.Scope, scope =>
            {
                scope.Property(value => value.Project).HasColumnName("project_ref")
                    .HasConversion(reference => reference.Value, value => ProjectRef.From(value)).IsRequired();
                scope.Property(value => value.Epic).HasColumnName("epic_ref")
                    .HasConversion(reference => reference.Value, value => EpicRef.From(value)).IsRequired();
                scope.Property(value => value.Task).HasColumnName("task_ref")
                    .HasConversion(reference => reference.Value, value => TaskRef.From(value)).IsRequired();
            });

            // The expiry worker scans exactly this: held reservations whose TTL has passed.
            reservation.HasIndex(entity => new { entity.State, entity.ExpiresAtUtc })
                .HasDatabaseName("ix_reservations_state_expiry");
            reservation.HasIndex(entity => new { entity.ResourceKey, entity.StartUtc, entity.EndUtc })
                .HasDatabaseName("ix_reservations_resource_interval");

            reservation.HasQueryFilter(entity => entity.TenantId == _currentTenantId());
        });

        modelBuilder.Entity<OperationStandard>(standard =>
        {
            standard.ToTable(SchedulingRlsSql.StandardsTable);
            standard.HasKey(entity => entity.Id);

            standard.Property(entity => entity.Id).HasColumnName("id");
            standard.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
            standard.Property(entity => entity.SourceTaskKey).HasColumnName("source_task_key").HasMaxLength(128).IsRequired();

            standard.HasIndex(entity => new { entity.TenantId, entity.SourceTaskKey })
                .HasDatabaseName("ix_standards_tenant_source_key");

            standard.HasQueryFilter(entity => entity.TenantId == _currentTenantId());

            // Through the public navigation, not the field name: naming the field leaves EF
            // discovering the read-only Revisions property too, and it cannot tell them apart
            // (the same trap as ScheduleRun.Revisions).
            standard.OwnsMany(entity => entity.Revisions, revision =>
            {
                revision.ToTable(SchedulingRlsSql.StandardRevisionsTable);
                revision.WithOwner().HasForeignKey("standard_id");
                revision.HasKey(entity => entity.Id);

                revision.Property(entity => entity.Id).HasColumnName("id");
                revision.Property(entity => entity.Revision).HasColumnName("revision").IsRequired();
                revision.Property(entity => entity.UnitMinutes).HasColumnName("unit_minutes").HasPrecision(18, 4);
                revision.Property(entity => entity.Workforce).HasColumnName("workforce").HasPrecision(18, 4);
                revision.Property(entity => entity.DefaultDependency).HasColumnName("default_dependency").HasConversion<string>().HasMaxLength(32);
                revision.Property(entity => entity.ReleaseThresholdFraction).HasColumnName("release_threshold_fraction").HasPrecision(9, 6);
                revision.Property(entity => entity.ImportedAtUtc).HasColumnName("imported_at_utc").IsRequired();
                revision.Property(entity => entity.State).HasColumnName("state").HasConversion<string>().HasMaxLength(32).IsRequired();

                // Quarantine reasons as JSON: they are the revision's explanation, always read
                // with it, and never filtered on individually.
                // EF 8 maps a primitive collection to a JSON array natively; the reasons are
                // the revision's explanation, always read with it and never filtered on alone.
                revision.PrimitiveCollection<List<StandardQuarantineReason>>("_quarantineReasons")
                    .HasColumnName("quarantine_reasons")
                    .IsRequired();
            });

            // Qualifiers as a JSON column, not a side table: the set identifies the standard
            // and is always read with it, so a join would buy nothing. The domain keeps them
            // sorted, so the serialised form is canonical.
            standard.Property<SortedDictionary<string, string>>("_qualifiers")
                .HasColumnName("qualifiers")
                .HasColumnType("jsonb")
                .HasConversion(
                    value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                    text => JsonSerializer.Deserialize<SortedDictionary<string, string>>(text, (JsonSerializerOptions?)null)!,
                    new ValueComparer<SortedDictionary<string, string>>(
                        (left, right) => left!.SequenceEqual(right!),
                        value => value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                        value => new SortedDictionary<string, string>(value, StringComparer.Ordinal)))
                .IsRequired();
        });

        modelBuilder.Entity<SchedulingAuditEntry>(audit =>
        {
            audit.ToTable(SchedulingRlsSql.AuditTable);
            audit.HasKey(entity => entity.Id);

            audit.Property(entity => entity.Id).HasColumnName("id");
            audit.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
            audit.Property(entity => entity.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(64).IsRequired();
            audit.Property(entity => entity.SubjectId).HasColumnName("subject_id").HasMaxLength(256).IsRequired();
            audit.Property(entity => entity.Actor).HasColumnName("actor").HasMaxLength(128).IsRequired();
            audit.Property(entity => entity.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            audit.Property(entity => entity.Note).HasColumnName("note").HasMaxLength(1024);
            audit.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

            audit.HasIndex(entity => new { entity.TenantId, entity.OccurredAtUtc })
                .HasDatabaseName("ix_audit_tenant_time");

            audit.HasQueryFilter(entity => entity.TenantId == _currentTenantId());
        });

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable(SchedulingRlsSql.OutboxTable);
            outbox.HasKey(entity => entity.Id);

            outbox.Property(entity => entity.Id).HasColumnName("id");
            outbox.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
            outbox.Property(entity => entity.MessageType).HasColumnName("message_type").HasMaxLength(256).IsRequired();
            outbox.Property(entity => entity.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            outbox.Property(entity => entity.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
            outbox.Property(entity => entity.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            outbox.Property(entity => entity.DispatchedAtUtc).HasColumnName("dispatched_at_utc");
            outbox.Property(entity => entity.FailedAttempts).HasColumnName("failed_attempts").IsRequired();
            outbox.Property(entity => entity.LastError).HasColumnName("last_error").HasMaxLength(2048);

            // The dispatcher polls pending messages oldest-first; a filtered index keeps that
            // scan proportional to the backlog rather than to the whole history.
            outbox.HasIndex(entity => entity.OccurredAtUtc)
                .HasFilter("dispatched_at_utc IS NULL")
                .HasDatabaseName("ix_outbox_pending");

            outbox.HasQueryFilter(entity => entity.TenantId == _currentTenantId());
        });
    }
}
