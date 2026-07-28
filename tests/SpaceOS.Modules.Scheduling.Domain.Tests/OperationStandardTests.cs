using System;
using System.Collections.Generic;
using System.Linq;
using SpaceOS.Modules.Scheduling.Domain.Calculation;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Standards;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Versioned norm times with import quarantine (ADR-069 §4).
/// </summary>
public sealed class OperationStandardTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    private static OperationStandard Register(IReadOnlyDictionary<string, string>? qualifiers = null) =>
        OperationStandard.Register(Guid.NewGuid(), TenantId, "GyV-0042", qualifiers);

    private static StandardRevision ImportValid(OperationStandard standard, decimal unitMinutes = 1.5m) =>
        standard.Import(Guid.NewGuid(), unitMinutes, 2m, DependencyType.FinishToStart, null, Now);

    [Fact]
    public void A_valid_import_is_accepted_and_usable_for_planning()
    {
        var standard = Register();

        var revision = ImportValid(standard);

        Assert.Equal(StandardRevisionState.Accepted, revision.State);
        Assert.True(revision.IsUsableForPlanning);
        Assert.Same(revision, standard.AcceptedRevision);
        Assert.Empty(revision.QuarantineReasons);
    }

    [Fact]
    public void A_newer_accepted_import_supersedes_the_previous_one()
    {
        var standard = Register();
        var first = ImportValid(standard, 1.5m);
        var second = ImportValid(standard, 2m);

        Assert.Equal(StandardRevisionState.Superseded, first.State);
        Assert.Same(second, standard.AcceptedRevision);
        Assert.Single(standard.Revisions.Where(revision => revision.IsUsableForPlanning));
    }

    [Theory]
    // Double literals (2d, not 2): xUnit binds InlineData by the literal's CLR type, and an
    // Int32 will not convert to a double? parameter at invocation time.
    [InlineData(null, 2d, StandardQuarantineReason.MissingUnitTime)]
    [InlineData(0d, 2d, StandardQuarantineReason.NonPositiveUnitTime)]
    [InlineData(1.5d, null, StandardQuarantineReason.MissingWorkforce)]
    [InlineData(1.5d, 0d, StandardQuarantineReason.NonPositiveWorkforce)]
    public void A_defective_import_is_quarantined_with_the_reason_named(
        double? unitMinutes, double? workforce, StandardQuarantineReason expected)
    {
        var standard = Register();

        var revision = standard.Import(
            Guid.NewGuid(), (decimal?)unitMinutes, (decimal?)workforce, null, null, Now);

        Assert.Equal(StandardRevisionState.Quarantined, revision.State);
        Assert.False(revision.IsUsableForPlanning);
        Assert.Contains(expected, revision.QuarantineReasons);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5d)]
    [InlineData(1.5d)]
    public void A_release_threshold_outside_the_zero_to_one_range_is_quarantined(double threshold)
    {
        var standard = Register();

        var revision = standard.Import(
            Guid.NewGuid(), 1.5m, 2m, DependencyType.FinishToStart, (decimal)threshold, Now);

        Assert.Contains(StandardQuarantineReason.InvalidReleaseThreshold, revision.QuarantineReasons);
    }

    [Fact]
    public void A_full_release_threshold_of_one_is_accepted()
    {
        var revision = Register().Import(Guid.NewGuid(), 1.5m, 2m, DependencyType.FinishToStart, 1m, Now);

        Assert.Equal(StandardRevisionState.Accepted, revision.State);
    }

    [Fact]
    public void A_quarantined_import_does_not_displace_the_accepted_standard()
    {
        // This is the load-bearing rule: a bad row arriving from the source must never take
        // the shop floor's current norm time away.
        var standard = Register();
        var accepted = ImportValid(standard);

        standard.Import(Guid.NewGuid(), null, null, null, null, Now.AddHours(1));

        Assert.Same(accepted, standard.AcceptedRevision);
        Assert.Equal(StandardRevisionState.Accepted, accepted.State);
    }

    [Fact]
    public void Quarantine_reasons_are_deduplicated_and_ordered()
    {
        var standard = Register();

        var revision = standard.Import(
            Guid.NewGuid(), null, null, null, null, Now,
            [StandardQuarantineReason.MissingUnitTime, StandardQuarantineReason.DuplicateSourceTaskKey]);

        Assert.Equal(revision.QuarantineReasons.Distinct().Count(), revision.QuarantineReasons.Count);
        Assert.Equal(revision.QuarantineReasons.OrderBy(reason => reason), revision.QuarantineReasons);
    }

    [Fact]
    public void A_quarantined_revision_still_reports_its_gaps_to_the_effort_calculator()
    {
        // Refusing to answer would hide the row from the operator; answering with the gaps
        // intact lets the calculator flag exactly what is missing.
        var standard = Register();
        var revision = standard.Import(Guid.NewGuid(), null, 2m, null, null, Now);

        var estimate = EffortCalculator.Calculate(revision.ToEffortInput(volume: 10m));

        Assert.False(estimate.EligibleForAutomaticPlanning);
        Assert.Contains(EffortInputField.UnitMinutes, estimate.MissingFields);
    }

    [Fact]
    public void An_accepted_revision_produces_a_complete_effort_estimate()
    {
        var revision = ImportValid(Register(), unitMinutes: 1.5m);

        var estimate = EffortCalculator.Calculate(revision.ToEffortInput(volume: 10m));

        Assert.True(estimate.EligibleForAutomaticPlanning);
        Assert.Equal(15m, estimate.EstimatedDurationMinutes);
        Assert.Equal(30m, estimate.EstimatedLabourMinutes);
    }

    [Fact]
    public void Qualifiers_have_one_canonical_order()
    {
        // The qualifier set identifies the standard; two orderings of the same set must not
        // look like two different standards.
        var forward = Register(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var reversed = Register(new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });

        Assert.Equal(forward.Qualifiers.Keys, reversed.Qualifiers.Keys);
    }

    [Fact]
    public void A_standard_must_belong_to_a_tenant_and_carry_its_source_key()
    {
        Assert.Throws<ArgumentException>(
            () => OperationStandard.Register(Guid.NewGuid(), Guid.Empty, "GyV-1"));

        var exception = Assert.Throws<ArgumentException>(
            () => OperationStandard.Register(Guid.NewGuid(), TenantId, "   "));
        Assert.Contains("source key", exception.Message, StringComparison.Ordinal);
    }
}
