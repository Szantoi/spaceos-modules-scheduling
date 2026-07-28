using System;
using SpaceOS.Modules.Scheduling.Domain.Calculation;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// Behaviour the pinned vectors do not reach: fractional standards, boundary rounding
/// and the flag-versus-throw split.
/// </summary>
public sealed class EffortCalculatorTests
{
    [Fact]
    public void Workforce_multiplies_labour_but_never_shortens_elapsed_time()
    {
        var single = EffortCalculator.Calculate(new EffortInput { Volume = 10m, UnitMinutes = 6m, Workforce = 1m });
        var crew = EffortCalculator.Calculate(new EffortInput { Volume = 10m, UnitMinutes = 6m, Workforce = 4m });

        Assert.Equal(single.EstimatedDurationMinutes, crew.EstimatedDurationMinutes);
        Assert.Equal(60m, crew.EstimatedDurationMinutes);
        Assert.Equal(240m, crew.EstimatedLabourMinutes);
    }

    [Fact]
    public void Handles_a_fractional_unit_time_without_rounding_the_duration()
    {
        // Catalogue standards arrive in seconds (e.g. 90s = 1.5 min); a premature integer
        // conversion here would drift by hours across a large order.
        var estimate = EffortCalculator.Calculate(new EffortInput { Volume = 7m, UnitMinutes = 1.5m, Workforce = 2m });

        Assert.Equal(10.5m, estimate.EstimatedDurationMinutes);
        Assert.Equal(21m, estimate.EstimatedLabourMinutes);
        Assert.Equal(1, estimate.PlannedWorkingDays);
    }

    [Theory]
    [InlineData(480, 1)]   // exactly one full day -- must not round up to two
    [InlineData(481, 2)]
    [InlineData(960, 2)]
    public void Rounds_days_up_only_when_the_day_is_actually_exceeded(int durationMinutes, int expectedDays)
    {
        var estimate = EffortCalculator.Calculate(
            new EffortInput { Volume = durationMinutes, UnitMinutes = 1m, Workforce = 1m });

        Assert.Equal(expectedDays, estimate.PlannedWorkingDays);
    }

    [Fact]
    public void Extra_days_are_added_after_the_rounding()
    {
        var estimate = EffortCalculator.Calculate(
            new EffortInput { Volume = 481m, UnitMinutes = 1m, Workforce = 1m, ExtraDays = 2 });

        Assert.Equal(4, estimate.PlannedWorkingDays); // ceil(481/480) = 2, + 2
    }

    [Fact]
    public void A_custom_working_day_changes_the_day_count_but_not_the_effort()
    {
        var estimate = EffortCalculator.Calculate(
            new EffortInput { Volume = 60m, UnitMinutes = 10m, Workforce = 1m, WorkingMinutesPerDay = 300m });

        Assert.Equal(600m, estimate.EstimatedDurationMinutes);
        Assert.Equal(2, estimate.PlannedWorkingDays);
    }

    [Theory]
    // Double literals (5d, not 5): xUnit binds InlineData values by their literal CLR
    // type, and an Int32 will not convert to a double? parameter at invocation time.
    [InlineData(null, 5d, 2d, new[] { EffortInputField.Volume })]
    [InlineData(5d, null, 2d, new[] { EffortInputField.UnitMinutes })]
    [InlineData(5d, 5d, null, new[] { EffortInputField.Workforce })]
    public void An_incomplete_standard_is_flagged_not_thrown(
        double? volume, double? unitMinutes, double? workforce, EffortInputField[] expectedMissing)
    {
        var estimate = EffortCalculator.Calculate(new EffortInput
        {
            Volume = (decimal?)volume,
            UnitMinutes = (decimal?)unitMinutes,
            Workforce = (decimal?)workforce,
        });

        Assert.False(estimate.EligibleForAutomaticPlanning);
        Assert.Equal(0m, estimate.EstimatedDurationMinutes);
        Assert.Equal(0m, estimate.EstimatedLabourMinutes);
        Assert.Equal(expectedMissing, estimate.MissingFields);
    }

    [Fact]
    public void A_zero_or_negative_input_counts_as_missing_not_as_a_valid_zero()
    {
        var estimate = EffortCalculator.Calculate(
            new EffortInput { Volume = 0m, UnitMinutes = -3m, Workforce = 1m });

        Assert.Equal([EffortInputField.Volume, EffortInputField.UnitMinutes], estimate.MissingFields);
    }

    [Fact]
    public void An_incomplete_standard_keeps_the_operator_declared_extra_days()
    {
        var estimate = EffortCalculator.Calculate(new EffortInput { Volume = 10m, ExtraDays = 2 });

        Assert.Equal(2, estimate.PlannedWorkingDays);
        Assert.False(estimate.EligibleForAutomaticPlanning);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_working_day_is_a_caller_error(int workingMinutesPerDay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EffortCalculator.Calculate(
            new EffortInput { Volume = 1m, UnitMinutes = 1m, Workforce = 1m, WorkingMinutesPerDay = workingMinutesPerDay }));
    }

    [Fact]
    public void A_negative_extra_day_count_is_a_caller_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EffortCalculator.Calculate(
            new EffortInput { Volume = 1m, UnitMinutes = 1m, Workforce = 1m, ExtraDays = -1 }));
    }
}
