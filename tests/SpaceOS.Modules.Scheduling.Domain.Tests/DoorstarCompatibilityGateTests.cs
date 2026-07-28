using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SpaceOS.Modules.Scheduling.Domain.Calculation;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Domain.Tests;

/// <summary>
/// The Doorstar compatibility gate (PLAN-03 M1, ADR-069 §4): every vector in the
/// hash-pinned input pack must reproduce bit-for-bit in the C# core.
/// </summary>
/// <remarks>
/// Vectors are read FROM the fixture rather than transcribed into C#. A transcription
/// would drift from the source silently; reading the pinned file means a changed pack
/// either fails the hash pin or fails a vector.
/// </remarks>
public sealed class DoorstarCompatibilityGateTests
{
    /// <summary>
    /// The gate covers all 13 pack entries: 3 effort vectors, 6 dependency vectors,
    /// 3 operation standard samples and 1 calendar draft.
    /// </summary>
    private const int ExpectedPackEntryCount = 13;

    public static TheoryData<string> EffortVectorIds => Ids("legacyCalculationVectors");

    public static TheoryData<string> DependencyVectorIds => Ids("dependencyCompatibilityVectors");

    [Fact]
    public void Pack_carries_the_agreed_thirteen_entries()
    {
        var effort = CompatibilityFixture.Root.GetProperty("legacyCalculationVectors").GetArrayLength();
        var dependency = CompatibilityFixture.Root.GetProperty("dependencyCompatibilityVectors").GetArrayLength();
        var samples = CompatibilityFixture.Root.GetProperty("operationStandardSamples").GetArrayLength();
        var calendarResources = CompatibilityFixture.Root
            .GetProperty("calendarDraft").GetProperty("resources").GetArrayLength();

        Assert.Equal(3, effort);
        Assert.Equal(6, dependency);
        Assert.Equal(3, samples);
        Assert.Equal(1, calendarResources);
        Assert.Equal(ExpectedPackEntryCount, effort + dependency + samples + calendarResources);
    }

    [Theory]
    [MemberData(nameof(EffortVectorIds))]
    public void Effort_vector_reproduces(string vectorId)
    {
        var vector = Vector("legacyCalculationVectors", vectorId);
        var input = vector.GetProperty("input");
        var expected = vector.GetProperty("expected");

        var estimate = EffortCalculator.Calculate(new EffortInput
        {
            Volume = input.OptionalDecimal("volume"),
            UnitMinutes = input.OptionalDecimal("unitMinutes"),
            Workforce = input.OptionalDecimal("workforce"),
            ExtraDays = input.OptionalInt("extraDays"),
            WorkingMinutesPerDay = input.OptionalDecimal("workingMinutesPerDay"),
        });

        Assert.Equal(expected.GetProperty("estimatedDurationMinutes").GetDecimal(), estimate.EstimatedDurationMinutes);
        Assert.Equal(expected.GetProperty("estimatedLabourMinutes").GetDecimal(), estimate.EstimatedLabourMinutes);
        Assert.Equal(expected.GetProperty("plannedWorkingDays").GetInt32(), estimate.PlannedWorkingDays);
        Assert.Equal(
            expected.GetProperty("eligibleForAutomaticPlanning").GetBoolean(),
            estimate.EligibleForAutomaticPlanning);

        var expectedMissing = expected.TryGetProperty("missingFields", out var missing)
            ? missing.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : [];
        Assert.Equal(expectedMissing, estimate.MissingFields.Select(ToWireName).ToArray());
    }

    [Theory]
    [MemberData(nameof(DependencyVectorIds))]
    public void Dependency_vector_reproduces(string vectorId)
    {
        var vector = Vector("dependencyCompatibilityVectors", vectorId);
        var input = vector.GetProperty("input");
        var expected = vector.GetProperty("expected");

        Assert.True(
            DependencyGraph.TryParseRelationCode(input.GetProperty("type").GetString()!, out var type),
            $"Unknown relation code in vector '{vectorId}'.");

        var bounds = DependencyBoundResolver.Resolve(
            new DependencyBoundInput
            {
                Type = type,
                PredecessorStartMinute = input.GetProperty("predecessorStartMinute").GetDecimal(),
                PredecessorFinishMinute = input.GetProperty("predecessorFinishMinute").GetDecimal(),
                LagMinutes = input.OptionalDecimal("lagMinutes") ?? 0m,
                PartialReleaseMinute = input.OptionalDecimal("partialReleaseMinute"),
                FixedStartMinute = input.OptionalDecimal("fixedStartMinute"),
                FixedFinishMinute = input.OptionalDecimal("fixedFinishMinute"),
            },
            // The pack was produced by the Doorstar reference, whose partial-release
            // reading is exactly ReplacesDependencyBound. Pinning the gate to that policy
            // keeps the OPEN question visible instead of hiding it behind a default.
            PartialReleasePolicy.ReplacesDependencyBound);

        AssertBound(expected, "earliestStartMinute", bounds.EarliestStartMinute);
        AssertBound(expected, "earliestFinishMinute", bounds.EarliestFinishMinute);
        AssertSource(expected, "startSource", bounds.StartSource);
        AssertSource(expected, "finishSource", bounds.FinishSource);
    }

    [Fact]
    public void Calendar_draft_yields_the_documented_four_hundred_and_eighty_net_minutes()
    {
        var shifts = CompatibilityFixture.Root
            .GetProperty("calendarDraft").GetProperty("resources")[0].GetProperty("shifts");

        foreach (var shift in shifts.EnumerateArray())
        {
            var breaks = shift.GetProperty("breaks").EnumerateArray()
                .Select(item => (Start: item.GetProperty("start").GetString()!, End: item.GetProperty("end").GetString()!))
                .ToArray();

            var netMinutes = ShiftMinutes(shift.GetProperty("start").GetString()!, shift.GetProperty("end").GetString()!)
                - breaks.Sum(item => ShiftMinutes(item.Start, item.End));

            Assert.Equal(EffortCalculator.DefaultWorkingMinutesPerDay, netMinutes);
        }
    }

    [Fact]
    public void Operation_standard_samples_keep_their_source_provenance()
    {
        foreach (var sample in CompatibilityFixture.Root.GetProperty("operationStandardSamples").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(sample.GetProperty("sourceTaskKey").GetString()));
            Assert.True(sample.GetProperty("sourceRow").GetInt32() > 0);
            Assert.True(sample.GetProperty("unitSeconds").GetDecimal() > 0m);

            // Every sample's relation code must be one the core understands, otherwise the
            // import would silently drop a precedence edge.
            var relation = sample.OptionalString("dependencyType");
            if (relation is not null)
            {
                Assert.True(DependencyGraph.TryParseRelationCode(relation, out _));
            }
        }
    }

    private static decimal ShiftMinutes(string start, string end)
    {
        var from = TimeOnly.Parse(start);
        var to = TimeOnly.Parse(end);
        return (decimal)(to - from).TotalMinutes;
    }

    private static void AssertBound(JsonElement expected, string propertyName, decimal? actual)
    {
        if (expected.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null)
        {
            Assert.Equal(value.GetDecimal(), actual);
        }
        else
        {
            Assert.Null(actual);
        }
    }

    private static void AssertSource(JsonElement expected, string propertyName, BoundSource? actual)
    {
        if (expected.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null)
        {
            Assert.Equal(value.GetString(), actual is null ? null : ToWireName(actual.Value));
        }
        else
        {
            Assert.Null(actual);
        }
    }

    private static string ToWireName(BoundSource source) => source switch
    {
        BoundSource.FixedOverride => "fixed_override",
        BoundSource.PartialRelease => "partial_release",
        BoundSource.Dependency => "dependency",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static string ToWireName(EffortInputField field) => field switch
    {
        EffortInputField.Volume => "volume",
        EffortInputField.UnitMinutes => "unitMinutes",
        EffortInputField.Workforce => "workforce",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static JsonElement Vector(string arrayName, string vectorId) =>
        CompatibilityFixture.Root.GetProperty(arrayName).EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == vectorId);

    private static TheoryData<string> Ids(string arrayName)
    {
        var data = new TheoryData<string>();
        foreach (var item in CompatibilityFixture.Root.GetProperty(arrayName).EnumerateArray())
        {
            data.Add(item.GetProperty("id").GetString()!);
        }
        return data;
    }
}
