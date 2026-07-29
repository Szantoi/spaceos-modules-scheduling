using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Host.Projections;
using SpaceOS.Modules.Scheduling.Host.Contracts;
using Xunit;
using YamlDotNet.Serialization;

namespace SpaceOS.Modules.Scheduling.Host.Tests;

/// <summary>
/// Keeps <c>docs/openapi.yaml</c> and the running host from drifting apart (ADR-069 §6).
/// </summary>
/// <remarks>
/// <para>
/// The spec is the SOURCE OF TRUTH here — Doorstar generates its client from it — so the
/// interesting failure is not "the spec is out of date" but "the spec and the code disagree",
/// and either direction is a defect. An endpoint added without a spec entry ships an
/// undocumented surface; a spec entry with no endpoint promises a route that answers 404.
/// </para>
/// <para>
/// Shapes are compared by PROPERTY NAME, not by hand-written expectation: a new DTO field
/// that nobody documented would otherwise reach a generated client as a missing type.
/// </para>
/// </remarks>
public sealed class OpenApiContractTests : IClassFixture<SchedulingHostFactory>
{
    private readonly SchedulingHostFactory _factory;

    public OpenApiContractTests(SchedulingHostFactory factory) => _factory = factory;

    private static readonly Lazy<JsonElement> Document = new(() =>
    {
        var path = SpecPath();
        // YAML in, JSON out: the assertions below only need a tree, and System.Text.Json is
        // already here. The YAML parser is what proves the file is well-formed at all.
        var yaml = new DeserializerBuilder().Build().Deserialize<object>(File.ReadAllText(path));
        var json = JsonSerializer.Serialize(yaml);
        return JsonDocument.Parse(json).RootElement.Clone();
    });

    private static string SpecPath()
    {
        // Walk up from the test binary to the repository root: the spec is a repo artifact,
        // not a copied test resource, so it must be read where it actually lives.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "openapi.yaml")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "docs/openapi.yaml was not found above the test binary.");
        return Path.Combine(directory!.FullName, "docs", "openapi.yaml");
    }

    /// <summary>Every documented (method, path) pair, with the server prefix applied.</summary>
    private static IReadOnlyList<(string Method, string Path)> DocumentedRoutes()
    {
        var basePath = Document.Value.GetProperty("servers")[0].GetProperty("url").GetString()!;
        return
        [
            .. Document.Value.GetProperty("paths").EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject()
                    .Select(operation => (Method: operation.Name.ToUpperInvariant(), Path: basePath + path.Name)))
        ];
    }

    /// <summary>Every route the host actually serves under the contract base path.</summary>
    private IReadOnlyList<(string Method, string Path)> HostedRoutes()
    {
        return
        [
            .. AllEndpoints().OfType<RouteEndpoint>()
                .Where(endpoint => Normalise(endpoint.RoutePattern.RawText!)
                    .StartsWith(SchedulingContractInfo.BasePath, StringComparison.Ordinal))
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => (Method: method, Path: Normalise(endpoint.RoutePattern.RawText!))))
        ];
    }

    /// <summary>
    /// Every endpoint the host built. Resolved through ALL registered data sources, not the
    /// single service: minimal APIs register their own source, so asking for one hands back
    /// an empty table and every comparison below would pass vacuously.
    /// </summary>
    private IEnumerable<Endpoint> AllEndpoints() =>
        _factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints);

    // Route patterns carry constraints ("{runId:guid}"); the spec names the parameter only.
    private static string Normalise(string pattern)
    {
        var withoutConstraints = string.Join('/', pattern
            .Split('/')
            .Select(segment => segment.StartsWith('{') && segment.Contains(':', StringComparison.Ordinal)
                ? string.Concat(segment.AsSpan(0, segment.IndexOf(':', StringComparison.Ordinal)), "}")
                : segment));

        return withoutConstraints.StartsWith('/') ? withoutConstraints : "/" + withoutConstraints;
    }

    [Fact]
    public void The_specification_declares_openapi_3_1()
    {
        // 3.1 specifically: the nullable type unions used throughout are not expressible in
        // 3.0, and a downgraded spec would silently lose them.
        Assert.StartsWith("3.1", Document.Value.GetProperty("openapi").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_specification_version_matches_the_contract_constant()
    {
        Assert.Equal(
            SchedulingContractInfo.Version,
            Document.Value.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public void Every_hosted_route_is_documented()
    {
        var documented = DocumentedRoutes().ToHashSet();
        var undocumented = HostedRoutes().Where(route => !documented.Contains(route)).ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"Endpoints served but absent from docs/openapi.yaml: {Describe(undocumented)}");
    }

    [Fact]
    public void Every_documented_route_is_hosted()
    {
        var hosted = HostedRoutes().ToHashSet();
        var missing = DocumentedRoutes().Where(route => !hosted.Contains(route)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"Documented in docs/openapi.yaml but not served: {Describe(missing)}");
    }

    [Fact]
    public void The_host_serves_something_at_all()
    {
        // Guards the two comparisons above against passing vacuously: an empty route table
        // would satisfy both directions.
        Assert.NotEmpty(HostedRoutes());
    }

    [Fact]
    public void Every_operation_id_matches_the_endpoint_name()
    {
        // The generated client's method names come from these. A rename that only happens on
        // one side produces a client calling a method the server never named.
        var hostedNames = AllEndpoints()
            .Select(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.EndpointNameMetadata>()?.EndpointName)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        var documentedIds = Document.Value.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Select(operation => operation.Value.GetProperty("operationId").GetString()!)
            .ToArray();

        Assert.NotEmpty(documentedIds);
        foreach (var operationId in documentedIds)
        {
            Assert.Contains(operationId, hostedNames);
        }
    }

    public static TheoryData<string, string> SchemaBindings => new()
    {
        { "ScheduleRunSummary", nameof(ScheduleRunSummaryDto) },
        { "WorkScope", nameof(WorkScopeDto) },
        { "OperationPlan", nameof(OperationPlanDto) },
        { "DependencyEdge", nameof(DependencyEdgeDto) },
        { "Proposal", nameof(ProposalDto) },
        { "DayRange", nameof(DayRangeDto) },
        { "RecurringShift", nameof(RecurringShiftDto) },
        { "CalendarException", nameof(CalendarExceptionDto) },
        { "ResourceCalendar", nameof(ResourceCalendarDto) },
        { "ResourceCalendarDetail", nameof(ResourceCalendarDetailDto) },
        { "OverloadSpan", nameof(OverloadSpanDto) },
        { "CapacityConflict", nameof(CapacityConflictDto) },
        { "OperationStandardSummary", nameof(OperationStandardSummaryDto) },
        { "StandardRevision", nameof(StandardRevisionDto) },
    };

    [Theory]
    [MemberData(nameof(SchemaBindings))]
    public void Each_schema_matches_its_wire_type(string schemaName, string dtoTypeName)
    {
        var schema = Document.Value.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        var documented = schema.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var type = typeof(SchedulingContractInfo).Assembly.GetTypes()
            .Single(candidate => candidate.Name == dtoTypeName);
        var actual = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !property.IsSpecialName && property.Name != "EqualityContract")
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(actual, documented);
    }

    [Fact]
    public void Every_declared_schema_is_bound_to_a_wire_type()
    {
        // Otherwise a schema could be deleted from the bindings table above and stop being
        // checked, while still being published to consumers.
        var declared = Document.Value.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Select(schema => schema.Name)
            .Where(name => name != "ProblemDetails") // shaped by the framework, not by a DTO
            .ToHashSet(StringComparer.Ordinal);

        var bound = SchemaBindings.Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared.OrderBy(name => name, StringComparer.Ordinal),
            bound.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_warning_code_is_declared_in_the_specification()
    {
        // The wire code is agreed with Doorstar, not derived from the C# member name. This
        // pins the two sides of that agreement inside our own build.
        var declared = Document.Value
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("DependencyEdge").GetProperty("properties")
            .GetProperty("warnings").GetProperty("items").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);

        var produced = Enum.GetValues<DependencyWarning>()
            .Select(SchedulingProjections.WarningCode)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            produced.OrderBy(code => code, StringComparer.Ordinal),
            declared.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void The_wire_warning_codes_match_the_Doorstar_fixture()
    {
        // THE gap that let the drift happen: the compatibility gate carried its own copy of
        // the mapping, so the fixture and the published wire could disagree and both stay
        // green. Now the fixture — the federation-agreed artefact — is compared against what
        // the API actually emits.
        var fixturePath = RepoFile(Path.Combine(
            "tests", "SpaceOS.Modules.Scheduling.Domain.Tests", "Fixtures",
            "doorstar-planning-input-pack.v2.json"));
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));

        var expected = Warnings(fixture.RootElement).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(expected);

        var produced = Enum.GetValues<DependencyWarning>()
            .Select(SchedulingProjections.WarningCode)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var code in expected)
        {
            Assert.Contains(code, produced);
        }
    }

    /// <summary>Every string under any "warnings" array, at any depth of the pack.</summary>
    private static IEnumerable<string> Warnings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("warnings") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var warning in property.Value.EnumerateArray())
                        {
                            if (warning.ValueKind == JsonValueKind.String)
                            {
                                yield return warning.GetString()!;
                            }
                        }

                        continue;
                    }

                    foreach (var nested in Warnings(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Warnings(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"{relativePath} was not found above the test binary.");
        return Path.Combine(directory!.FullName, relativePath);
    }

    private static string Describe(IEnumerable<(string Method, string Path)> routes) =>
        string.Join(", ", routes.Select(route => $"{route.Method} {route.Path}"));
}
