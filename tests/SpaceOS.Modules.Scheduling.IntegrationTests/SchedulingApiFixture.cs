using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpaceOS.Modules.Hosting.Persistence;
using SpaceOS.Modules.Hosting.RlsFixtures;
using SpaceOS.Modules.Hosting.Tenancy;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;
using Xunit;

namespace SpaceOS.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Runs the real host against a real PostgreSQL with RLS in force (PLAN-03 M3).
/// </summary>
/// <remarks>
/// <para>
/// The API is exercised over HTTP through the ACTUAL <c>Program</c>, on the non-superuser
/// application role. Anything the endpoints return therefore had to survive the entitlement
/// gate, the tenancy middleware, the EF query filter and the database policies — none of
/// which are stubbed here.
/// </para>
/// <para>
/// Only one thing is substituted: the authentication scheme, which mints the claims Keycloak
/// would sign. The token issuer is not what these tests are about, and a real one would make
/// them depend on a running identity provider.
/// </para>
/// </remarks>
public sealed class SchedulingApiFixture : IAsyncLifetime
{
    private NonSuperuserRlsFixture _inner = null!;
    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>The tenant every seeded row belongs to.</summary>
    public Guid TenantId { get; } = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    /// <summary>A second tenant, used to prove cross-tenant reads return nothing.</summary>
    public Guid OtherTenantId { get; } = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>Connection string of the non-superuser application role.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _inner = new NonSuperuserRlsFixture("scheduling_api");
        await _inner.StartAsync();

        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(_inner.AdminConnectionString, npgsql => npgsql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SchedulingDbContext.SchemaName))
            .Options;
        await using (var migrator = new SchedulingDbContext(options, () => null))
        {
            await migrator.Database.MigrateAsync();
        }

        await _inner.CreateApplicationRoleAsync(SchedulingDbContext.SchemaName);
        AppConnectionString = _inner.AppConnectionString();

        _factory = new SchedulingApiFactory(AppConnectionString, TenantId);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_inner is not null)
        {
            await _inner.DisposeAsync();
        }
    }

    /// <summary>Creates a client whose identity is entitled to the scheduling module.</summary>
    public HttpClient CreateEntitledClient(Guid? tenantId = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, (tenantId ?? TenantId).ToString());
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.ModulesHeader, "spaceos.scheduling");
        return client;
    }

    /// <summary>Creates a client authenticated for a tenant that has NOT enabled the module.</summary>
    public HttpClient CreateUnentitledClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, TenantId.ToString());
        return client;
    }

    /// <summary>
    /// A DbContext on the application role, pinned to one tenant — used to seed the data the
    /// API then has to find on its own.
    /// </summary>
    public SchedulingDbContext NewDbContext(Guid? tenantId = null)
    {
        var tenant = new FixedTenantContext(tenantId ?? TenantId);
        var interceptor = new SpaceOsTenantSessionInterceptor(
            tenant, new HttpContextAccessor(), NullLogger<SpaceOsTenantSessionInterceptor>.Instance);

        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(AppConnectionString, npgsql => npgsql.MigrationsHistoryTable(
                "__EFMigrationsHistory", SchedulingDbContext.SchemaName))
            .AddInterceptors(interceptor)
            .Options;

        return new SchedulingDbContext(options, () => tenant.TenantId);
    }
}

/// <summary>Boots the real host against the container database.</summary>
internal sealed class SchedulingApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly Guid _tenantId;

    public SchedulingApiFactory(string connectionString, Guid tenantId)
    {
        _connectionString = connectionString;
        _tenantId = tenantId;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Scheduling", _connectionString);
        builder.UseSetting("Jwt:Mode", "Development");
        builder.UseSetting("Jwt:Development:TenantId", _tenantId.ToString());

        // Runs AFTER the application's own registrations, so the test scheme becomes the
        // default. The shipped Development scheme issues no module entitlement claim at all
        // (see the note in SchedulingApiTests), which would make every request a 403.
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
            });
        });
    }
}

/// <summary>
/// Mints the claims a Keycloak token would carry, driven by request headers so one host can
/// serve entitled and unentitled callers in the same test run.
/// </summary>
internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    internal const string SchemeName = "IntegrationTest";
    internal const string TenantHeader = "X-Test-Tenant";
    internal const string ModulesHeader = "X-Test-Modules";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TenantHeader, out var tenantValues)
            || !Guid.TryParse(tenantValues.ToString(), out var tenantId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var modules = Request.Headers.TryGetValue(ModulesHeader, out var moduleValues)
            ? moduleValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        // The signed shape the resolver reads: one entry per tenant, snake_case, exactly as
        // the production Keycloak mapper emits it.
        var entries = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tenant_id"] = tenantId.ToString(),
                ["enabled_modules"] = modules,
            },
        });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "integration-test"),
            new("sub", "integration-test"),
            new(TenancyDefaults.TenantIdClaim, tenantId.ToString()),
            new(TenancyDefaults.TenantListClaim, entries, "JSON"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Shares one container and one host across the API suite.</summary>
[CollectionDefinition(Name)]
public sealed class SchedulingApiCollection : ICollectionFixture<SchedulingApiFixture>
{
    /// <summary>The xunit collection name.</summary>
    public const string Name = "scheduling-api";
}
