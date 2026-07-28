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
    /// Both pinned packs run the same gate. v1 is the original 13-entry pack a consumer may
    /// still hold; v2 supersedes it with the settled partial-release vector (14 entries).
    /// </summary>
    public static TheoryData<string> Packs => new() { CompatibilityFixture.V1, CompatibilityFixture.V2 };

    public static TheoryData<string, string> EffortVectorIds => Ids("legacyCalculationVectors");

    public static TheoryData<string, string> DependencyVectorIds => Ids("dependencyCompatibilityVectors");

    [Theory]
    [MemberData(nameof(Packs))]
    public void Pack_carries_the_agreed_entry_count(string pack)
    {
        var root = CompatibilityFixture.Root(pack);
        var effort = root.GetProperty("legacyCalculationVectors").GetArrayLength();
        var dependency = root.GetProperty("dependencyCompatibilityVectors").GetArrayLength();
        var samples = root.GetProperty("operationStandardSamples").GetArrayLength();
        var calendarResources = root.GetProperty("calendarDraft").GetProperty("resources").GetArrayLength();

        var expectedDependency = pack == CompatibilityFixture.V1 ? 6 : 7;

        Assert.Equal(3, effort);
        Assert.Equal(expectedDependency, dependency);
        Assert.Equal(3, samples);
        Assert.Equal(1, calendarResources);
        Assert.Equal(
            pack == CompatibilityFixture.V1 ? 13 : 14,
            effort + dependency + samples + calendarResources);
    }

    [Theory]
    [MemberData(nameof(EffortVectorIds))]
    public void Effort_vector_reproduces(string pack, string vectorId)
    {
        var vector = Vector(pack, "legacyCalculationVectors", vectorId);
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
    public void Dependency_vector_reproduces(string pack, string vectorId)
    {
        var vector = Vector(pack, "dependencyCompatibilityVectors", vectorId);
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
            });

        AssertBound(expected, "earliestStartMinute", bounds.EarliestStartMinute);
        AssertBound(expected, "earliestFinishMinute", bounds.EarliestFinishMinute);
        AssertSource(expected, "startSource", bounds.StartSource);
        AssertSource(expected, "finishSource", bounds.FinishSource);
        AssertWarnings(expected, bounds.Warnings);
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void Calendar_draft_yields_the_documented_four_hundred_and_eighty_net_minutes(string pack)
    {
        var shifts = CompatibilityFixture.Root(pack)
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

    [Theory]
    [MemberData(nameof(Packs))]
    public void Operation_standard_samples_keep_their_source_provenance(string pack)
    {
        foreach (var sample in CompatibilityFixture.Root(pack).GetProperty("operationStandardSamples").EnumerateArray())
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

    private static void AssertWarnings(JsonElement expected, IReadOnlyList<DependencyWarning> actual)
    {
        var expectedWarnings = expected.TryGetProperty("warnings", out var warnings)
            ? warnings.EnumerateArray().Select(item => item.GetString()!).OrderBy(name => name, StringComparer.Ordinal).ToArray()
            : [];

        var actualWarnings = actual.Select(ToWireName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedWarnings, actualWarnings);
    }

    private static string ToWireName(DependencyWarning warning) => warning switch
    {
        DependencyWarning.PartialReleaseDelaysStart => "partial_release_delays_fs_start",
        _ => throw new ArgumentOutOfRangeException(nameof(warning), warning, null),
    };

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

    private static JsonElement Vector(string pack, string arrayName, string vectorId) =>
        CompatibilityFixture.Root(pack).GetProperty(arrayName).EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == vectorId);

    private static TheoryData<string, string> Ids(string arrayName)
    {
        var data = new TheoryData<string, string>();
        foreach (var pack in new[] { CompatibilityFixture.V1, CompatibilityFixture.V2 })
        {
            foreach (var item in CompatibilityFixture.Root(pack).GetProperty(arrayName).EnumerateArray())
            {
                data.Add(pack, item.GetProperty("id").GetString()!);
            }
        }
        return data;
    }
}
