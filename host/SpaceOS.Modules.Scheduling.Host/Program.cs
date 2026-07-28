using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpaceOS.Modules.Hosting.Auth;
using SpaceOS.Modules.Hosting.Modules;
using SpaceOS.Modules.Hosting.Tenancy;
using SpaceOS.Modules.Scheduling.Host;

// PLAN-03 M2 host skeleton. Read endpoints arrive in M3 (the Doorstar consumption gate);
// today this host exists to prove the module is runnable and correctly wired.
var builder = WebApplication.CreateBuilder(args);

// Shared module-host auth (ADR-061): Keycloak JWT bearer with fail-fast configuration.
// Nothing here trusts a request header for identity -- see the tenancy middleware below.
builder.Services.AddSpaceOsModuleAuth(builder.Configuration, builder.Environment);

// Tenant resolution from the authenticated token (tid claim), never from a client header:
// a forged header is answered with 403 by the shared middleware.
builder.Services.AddSpaceOsModuleTenancy();

builder.Services.AddSchedulingModule(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Order matters: tenancy runs AFTER authentication, because it derives the tenant from the
// authenticated principal. Placed earlier it would see no claims and fail closed on every
// request.
app.UseSpaceOsModuleTenancy();

// Anonymous by design: an orchestrator or a load balancer must be able to see liveness
// without a token. It exposes module identity and version only, never tenant data.
app.MapModuleHealth(SchedulingModule.Descriptor);

app.Run();

/// <summary>Entry point marker so integration tests can reference this host.</summary>
public partial class Program;
