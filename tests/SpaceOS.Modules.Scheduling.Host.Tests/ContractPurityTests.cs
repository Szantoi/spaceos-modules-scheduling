using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SpaceOS.Modules.Scheduling.Host.Contracts;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Host.Tests;

/// <summary>
/// The ADR-070 D2 guard in executable form: the published contract must not leak the
/// platform's internal library choices or its domain types.
/// </summary>
/// <remarks>
/// <para>
/// Doorstar generates a TypeScript client from this shape. A NodaTime type — or a domain
/// aggregate — appearing here would make an internal decision part of the contract, and
/// changing that decision later would break a consumer that never asked for it.
/// </para>
/// <para>
/// Asserted by reflection over the contract assembly rather than by reading the generated
/// OpenAPI document, so the check fails at unit-test speed, before any spec is produced.
/// </para>
/// </remarks>
public sealed class ContractPurityTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "NodaTime",
        "SpaceOS.Modules.Scheduling.Domain",
        "SpaceOS.Modules.Scheduling.Infrastructure",
    ];

    private static IEnumerable<Type> ContractTypes =>
        typeof(SchedulingContractInfo).Assembly
            .GetTypes()
            .Where(type => type.IsPublic && type.Namespace == typeof(SchedulingContractInfo).Namespace);

    public static TheoryData<string> ContractTypeNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var type in ContractTypes)
            {
                data.Add(type.FullName!);
            }
            return data;
        }
    }

    [Fact]
    public void The_contract_namespace_is_not_empty()
    {
        // A vacuous purity check would pass forever; make sure there is something to check.
        Assert.NotEmpty(ContractTypes);
    }

    [Theory]
    [MemberData(nameof(ContractTypeNames))]
    public void No_contract_type_exposes_an_internal_type(string typeName)
    {
        var type = typeof(SchedulingContractInfo).Assembly.GetType(typeName)!;

        var leaked = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(property => Flatten(property.PropertyType).Select(used => (property.Name, Type: used)))
            .Where(usage => ForbiddenNamespacePrefixes.Any(prefix =>
                usage.Type.FullName?.StartsWith(prefix, StringComparison.Ordinal) == true))
            .Select(usage => $"{usage.Name}: {usage.Type.FullName}")
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            $"{typeName} exposes internal types on the wire: {string.Join(", ", leaked)}");
    }

    [Fact]
    public void Timestamps_travel_as_strings_not_as_clr_date_types()
    {
        // ISO-8601 UTC strings (ADR-070 D2). A DateTime/DateTimeOffset would serialise with
        // whatever offset semantics the server happened to have, and a generated client would
        // inherit that ambiguity.
        var dateProperties = ContractTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => (Type: type.Name, property.Name, property.PropertyType)))
            .Where(property =>
                property.PropertyType == typeof(DateTime) ||
                property.PropertyType == typeof(DateTimeOffset) ||
                property.PropertyType == typeof(DateTime?) ||
                property.PropertyType == typeof(DateTimeOffset?))
            .Select(property => $"{property.Type}.{property.Name}")
            .ToArray();

        Assert.True(
            dateProperties.Length == 0,
            $"These contract properties use a CLR date type instead of an ISO-8601 string: " +
            string.Join(", ", dateProperties));
    }

    [Fact]
    public void The_contract_advertises_its_base_path_and_version()
    {
        Assert.Equal("/api/scheduling/v1", SchedulingContractInfo.BasePath);
        Assert.False(string.IsNullOrWhiteSpace(SchedulingContractInfo.Version));
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        // Collections and dictionaries hide their element types one level down; a leak inside
        // IReadOnlyList<SomeDomainType> is just as visible on the wire as a direct one.
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }
}
