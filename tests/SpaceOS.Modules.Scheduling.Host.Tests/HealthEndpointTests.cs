using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Host.Tests;

/// <summary>
/// Runtime behaviour of the module host (PLAN-03 M2).
/// </summary>
/// <remarks>
/// Deliberately Docker-free: the health endpoint must answer without touching the database,
/// so a liveness probe still reports during a database outage instead of timing out. The
/// connection string below is therefore syntactically valid but never connected to.
/// </remarks>
public sealed class HealthEndpointTests : IClassFixture<SchedulingHostFactory>
{
    private readonly SchedulingHostFactory _factory;

    public HealthEndpointTests(SchedulingHostFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_answers_without_a_token()
    {
        // Anonymous by design: an orchestrator or load balancer has no user token, and a
        // 401 here would look like an outage.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_the_canonical_module_identity()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await response.Content.ReadAsStringAsync());

        Assert.NotNull(payload);
        Assert.Equal("spaceos.scheduling", payload!["moduleId"].GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload["version"].GetString()));
        Assert.Equal("Healthy", payload["status"].GetString());
    }

    [Fact]
    public async Task Health_exposes_no_tenant_data()
    {
        // A health payload is read by anyone who can reach the port. It may carry module
        // identity, never anything tenant-scoped.
        using var client = _factory.CreateClient();

        var body = await client.GetStringAsync("/health");

        foreach (var forbidden in new[] { "tenant", "tid", "connection", "password" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_module_refuses_to_start_without_a_connection_string()
    {
        // Fail fast beats a silent fallback to a local default: the latter starts happily
        // and then serves the wrong database.
        using var factory = new SchedulingHostFactory { ConnectionString = string.Empty };

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Scheduling", exception.Message, StringComparison.Ordinal);
    }
}

/// <summary>Boots the real host with test configuration.</summary>
/// <remarks>
/// Exactly one public constructor: xUnit refuses a class fixture with more, so the
/// connection string is a settable property instead of a constructor parameter.
/// </remarks>
public sealed class SchedulingHostFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Injected connection string. The default looks valid but is never opened; set it to
    /// an empty value to exercise the fail-fast path.
    /// </summary>
    public string ConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=scheduling_host_tests;Username=none;Password=none";

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development auth mode: the shared package refuses it outside the Development
        // environment, which is exactly the guard we want to keep intact.
        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration: the factory's configuration sources are
        // added BEFORE the application's own appsettings.json, so an in-memory value would be
        // overwritten by the shipped file (which deliberately carries an empty Authority to
        // fail fast in production).
        builder.UseSetting("ConnectionStrings:Scheduling", ConnectionString);
        builder.UseSetting("Jwt:Mode", "Development");
        // The shared package insists on a real tenant even for the synthetic dev identity, so
        // the tenancy pipeline behaves exactly as in production. A fixed GUID keeps the test
        // deterministic.
        builder.UseSetting("Jwt:Development:TenantId", "aaaaaaaa-0000-4000-8000-000000000001");
    }
}
