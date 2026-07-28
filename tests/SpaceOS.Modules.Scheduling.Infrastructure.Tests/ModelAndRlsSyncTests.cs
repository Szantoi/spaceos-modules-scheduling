using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Keeps the EF model and the RLS statements from drifting apart.
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is silent and severe: the policies name tables and columns as
/// strings, so an EF rename leaves the policy attached to a table that no longer exists —
/// or worse, leaves a renamed table with NO policy at all. Nothing errors; the isolation
/// simply stops applying.
/// </para>
/// <para>
/// The root review already found one such drift (a unique index present in the model but
/// missing from the proof DDL), which is why this guard exists rather than a convention.
/// </para>
/// </remarks>
public sealed class ModelAndRlsSyncTests
{
    private static IModel BuildModel()
    {
        // Model building does not open a connection, so no database is needed here.
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;

        using var context = new SchedulingDbContext(options, () => null);
        return context.Model;
    }

    private static IReadOnlyList<string> TableNames(IModel model) =>
        [.. model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)];

    [Fact]
    public void Every_mapped_table_is_covered_by_an_rls_statement()
    {
        var mapped = TableNames(BuildModel());
        var protectedTables = SchedulingRlsSql.AllTables;

        var unprotected = mapped.Except(protectedTables, StringComparer.Ordinal).ToArray();

        Assert.True(
            unprotected.Length == 0,
            $"These mapped tables have no RLS statement: {string.Join(", ", unprotected)}. " +
            "A tenant-scoped table without a policy is readable across tenants.");
    }

    [Fact]
    public void Every_rls_statement_targets_a_table_that_actually_exists_in_the_model()
    {
        var mapped = TableNames(BuildModel());

        var orphans = SchedulingRlsSql.AllTables.Except(mapped, StringComparer.Ordinal).ToArray();

        Assert.True(
            orphans.Length == 0,
            $"These RLS statements name tables the model does not map: {string.Join(", ", orphans)}. " +
            "The policy would be attached to nothing, or the migration would fail.");
    }

    [Fact]
    public void The_schema_name_is_the_one_the_policies_are_written_against()
    {
        var model = BuildModel();

        Assert.Equal(SchedulingDbContext.SchemaName, model.GetDefaultSchema());
        Assert.All(
            model.GetEntityTypes().Where(entity => entity.GetTableName() is not null),
            entity => Assert.Equal(SchedulingDbContext.SchemaName, entity.GetSchema() ?? model.GetDefaultSchema()));
    }

    [Theory]
    [InlineData("schedule_runs", "tenant_id")]
    [InlineData("schedule_revisions", "run_id")]
    [InlineData("operation_plans", "revision_id")]
    public void The_columns_the_policies_reference_exist_in_the_model(string table, string column)
    {
        // The policy predicates are raw SQL: a renamed column turns them into a migration
        // error at best, and a policy over the wrong data at worst.
        var model = BuildModel();

        var entity = model.GetEntityTypes().FirstOrDefault(candidate => candidate.GetTableName() == table);
        Assert.NotNull(entity);

        var identifier = StoreObjectIdentifier.Table(table, SchedulingDbContext.SchemaName);
        var columns = entity!.GetProperties()
            .Select(property => property.GetColumnName(identifier))
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains(column, columns);
    }

    [Fact]
    public void The_revision_sequence_uniqueness_is_expressed_in_the_model()
    {
        // Two revisions numbered 1 in the same run would make the chain ambiguous and the
        // "sequence" in the API meaningless. The proof DDL must carry this too.
        var model = BuildModel();

        var revisions = model.GetEntityTypes().Single(entity => entity.GetTableName() == "schedule_revisions");
        var uniqueIndexes = revisions.GetIndexes().Where(index => index.IsUnique).ToArray();

        Assert.Contains(uniqueIndexes, index =>
            index.Properties.Select(property => property.GetColumnName(
                StoreObjectIdentifier.Table("schedule_revisions", SchedulingDbContext.SchemaName)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(["run_id", "sequence"], StringComparer.Ordinal));
    }
}
