using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using SpaceOS.Modules.Scheduling.Domain.Dependencies;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Domain.Standards;
using Xunit;

namespace SpaceOS.Modules.Scheduling.IntegrationTests;

/// <summary>
/// The published read contract end to end (ADR-069 §6, PLAN-03 M3) — the Doorstar gate.
/// </summary>
/// <remarks>
/// Every case here goes over HTTP against the real host on a real database with RLS in
/// force. That is the point: a projection bug, a missing entitlement gate or a policy that
/// hides a row would all pass a unit test of the mapper and fail here.
/// </remarks>
[Collection(SchedulingApiCollection.Name)]
public sealed class SchedulingApiTests
{
    private readonly SchedulingApiFixture _fixture;

    public SchedulingApiTests(SchedulingApiFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Seeded = new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);

    private static KernelWorkScope Scope(Guid projectId) => KernelWorkScope.Create(
        ProjectRef.From(projectId),
        EpicRef.From(Guid.NewGuid()),
        TaskRef.From(Guid.NewGuid()));

    /// <summary>Seeds one run with a published, fully attributed revision.</summary>
    private async Task<(Guid RunId, Guid ProjectId, string Hash)> SeedPublishedRunAsync(Guid? tenantId = null)
    {
        var projectId = Guid.NewGuid();
        // ONE instance, shared by both operations on purpose: that is what a caller naturally
        // writes, and it used to persist the second operation with NULL Kernel references.
        var scope = Scope(projectId);
        var tenant = tenantId ?? _fixture.TenantId;

        var run = ScheduleRun.Open(Guid.NewGuid(), tenant, ProjectRef.From(projectId), Seeded);
        var operations = new List<OperationPlan>
        {
            new()
            {
                OperationId = "cut",
                Scope = scope,
                ResourceKey = "saw-1",
                StartMinute = 0m,
                FinishMinute = 120m,
                StandardRevision = 3,
                SourceRevisions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["orderKey"] = "ORD-2026/07-42",
                    ["orderRevision"] = "2",
                    ["calculationRevision"] = "7",
                },
            },
            new()
            {
                OperationId = "assemble",
                Scope = scope,
                ResourceKey = "bench-1",
                StartMinute = 150m,
                FinishMinute = 300m,
            },
        };

        var revision = run.AddProposal(
            Guid.NewGuid(),
            operations,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["saw-1"] = 2, ["bench-1"] = 1 },
            Seeded,
            [
                new PlannedDependency
                {
                    PredecessorOperationId = "cut",
                    SuccessorOperationId = "assemble",
                    Relation = DependencyType.FinishToStart,
                    LagMinutes = 30m,
                    EarliestStartMinute = 150m,
                    StartSource = BoundSource.PartialRelease,
                    Warnings = [DependencyWarning.PartialReleaseDelaysStart],
                },
            ]);
        run.Publish(revision.Id);

        await using var database = _fixture.NewDbContext(tenant);
        database.ScheduleRuns.Add(run);
        await database.SaveChangesAsync();

        return (run.Id, projectId, revision.ContentHash);
    }

    private async Task SeedCalendarAsync(string resourceKey, decimal capacity = 1m, bool approve = true)
    {
        var calendar = ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(), _fixture.TenantId, resourceKey, 1, "Europe/Budapest", capacity,
            CapacityPolicy.Integer, Seeded,
            [new RecurringShift(3, new DayRange(480, 960), [new DayRange(720, 750)])]);
        calendar.AddException(CalendarException.Create(
            Guid.NewGuid(), new DateOnly(2026, 8, 20), CalendarExceptionKind.Closure, reason: "public holiday"));
        if (approve)
        {
            calendar.Approve();
        }

        await using var database = _fixture.NewDbContext();
        database.CalendarRevisions.Add(calendar);
        await database.SaveChangesAsync();
    }

    /// <summary>
    /// GETs and parses, putting the RESPONSE BODY into the failure message. A bare
    /// EnsureSuccessStatusCode reports "500" and nothing else, which turns a one-line
    /// projection bug into a debugging session.
    /// </summary>
    private static async Task<JsonElement> GetJsonAsync(System.Net.Http.HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {url} -> {(int)response.StatusCode}: {(body.Length > 3000 ? body[..3000] : body)}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task An_unentitled_tenant_is_refused_before_any_data_is_read()
    {
        // Fail-closed: the module gate is what turns "authenticated" into "allowed to use
        // THIS module". Without it a tenant who never bought scheduling could read plans.
        using var client = _fixture.CreateUnentitledClient();

        using var response = await client.GetAsync("/api/scheduling/v1/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_challenged()
    {
        using var client = _fixture.CreateEntitledClient();
        client.DefaultRequestHeaders.Remove("X-Test-Tenant");
        client.DefaultRequestHeaders.Remove("X-Test-Modules");

        using var response = await client.GetAsync("/api/scheduling/v1/runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_run_is_listed_with_its_published_revision_hash()
    {
        var seeded = await SeedPublishedRunAsync();
        using var client = _fixture.CreateEntitledClient();

        var body = await GetJsonAsync(client, $"/api/scheduling/v1/runs?projectId={seeded.ProjectId}");

        var run = Assert.Single(body.EnumerateArray().ToArray());
        Assert.Equal(seeded.RunId, run.GetProperty("runId").GetGuid());
        Assert.Equal(seeded.Hash, run.GetProperty("publishedRevisionHash").GetString());
        Assert.Equal(1, run.GetProperty("revisionCount").GetInt32());
    }

    [Fact]
    public async Task The_proposal_carries_operations_dependencies_and_provenance()
    {
        var seeded = await SeedPublishedRunAsync();
        using var client = _fixture.CreateEntitledClient();

        var body = await GetJsonAsync(client, $"/api/scheduling/v1/runs/{seeded.RunId}/proposal");

        Assert.Equal(seeded.Hash, body.GetProperty("revisionHash").GetString());
        Assert.Equal("published", body.GetProperty("state").GetString());
        Assert.Equal("doorstar-contract-v1 (final)", body.GetProperty("ruleSetLabel").GetString());

        // Both operations must carry the scope, not just the first one to be attached.
        foreach (var operation in body.GetProperty("operations").EnumerateArray())
        {
            Assert.Equal(seeded.ProjectId, operation.GetProperty("scope").GetProperty("projectId").GetGuid());
        }

        var cut = body.GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("operationId").GetString() == "cut");
        Assert.Equal(3, cut.GetProperty("standardRevision").GetInt32());

        // Lineage reflected back unchanged — the platform stores, it never resolves.
        var lineage = cut.GetProperty("sourceRevisions");
        Assert.Equal("ORD-2026/07-42", lineage.GetProperty("orderKey").GetString());
        Assert.Equal("7", lineage.GetProperty("calculationRevision").GetString());

        // The Kernel work reference travels as three opaque ids (project → epic → task).
        var scope = cut.GetProperty("scope");
        Assert.Equal(seeded.ProjectId, scope.GetProperty("projectId").GetGuid());
        Assert.NotEqual(Guid.Empty, scope.GetProperty("epicId").GetGuid());

        var edge = Assert.Single(body.GetProperty("dependencies").EnumerateArray().ToArray());
        Assert.Equal("FS", edge.GetProperty("relationCode").GetString());
        Assert.Equal("partial_release", edge.GetProperty("startSource").GetString());
        Assert.Equal(
            "partial_release_delays_fs_start",
            Assert.Single(edge.GetProperty("warnings").EnumerateArray().ToArray()).GetString());

        // The calendar pins: without them the plan could not be recomputed later.
        Assert.Equal(2, body.GetProperty("calendarRevisions").GetProperty("saw-1").GetInt32());
    }

    [Fact]
    public async Task A_missing_run_answers_with_problem_details_and_a_correlation_id()
    {
        using var client = _fixture.CreateEntitledClient();

        using var response = await client.GetAsync($"/api/scheduling/v1/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = Root(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Another_tenants_run_is_not_found_rather_than_forbidden()
    {
        // RLS makes it invisible, and invisible is the right answer: a 403 would confirm that
        // the id exists, which is itself a cross-tenant leak.
        var foreign = await SeedPublishedRunAsync(_fixture.OtherTenantId);
        using var client = _fixture.CreateEntitledClient();

        using var response = await client.GetAsync($"/api/scheduling/v1/runs/{foreign.RunId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_calendar_is_returned_with_its_pattern_and_exceptions()
    {
        await SeedCalendarAsync("cnc-detail");
        using var client = _fixture.CreateEntitledClient();

        var body = await GetJsonAsync(client, "/api/scheduling/v1/resources/cnc-detail/calendar");

        Assert.Equal("Europe/Budapest", body.GetProperty("timeZoneId").GetString());
        Assert.True(body.GetProperty("isApproved").GetBoolean());

        var shift = Assert.Single(body.GetProperty("shifts").EnumerateArray().ToArray());
        Assert.Equal(3, shift.GetProperty("isoWeekday").GetInt32());
        Assert.Equal(480, shift.GetProperty("shift").GetProperty("startMinuteOfDay").GetInt32());
        Assert.Single(shift.GetProperty("breaks").EnumerateArray().ToArray());

        var exception = Assert.Single(body.GetProperty("exceptions").EnumerateArray().ToArray());
        Assert.Equal("2026-08-20", exception.GetProperty("date").GetString());
        Assert.Equal("closure", exception.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, exception.GetProperty("span").ValueKind);
    }

    [Fact]
    public async Task Overload_is_reported_for_the_period_that_is_actually_oversubscribed()
    {
        await SeedCalendarAsync("cnc-overload", capacity: 1m);
        var scope = Scope(Guid.NewGuid());
        await using (var database = _fixture.NewDbContext())
        {
            database.CapacityReservations.AddRange(
                CapacityReservation.Hold(
                    Guid.NewGuid(), _fixture.TenantId, "cnc-overload", scope,
                    Seeded, Seeded.AddHours(4), 1m, Seeded, TimeSpan.FromHours(24)),
                CapacityReservation.Hold(
                    Guid.NewGuid(), _fixture.TenantId, "cnc-overload", scope,
                    Seeded.AddHours(2), Seeded.AddHours(6), 1m, Seeded, TimeSpan.FromHours(24)));
            await database.SaveChangesAsync();
        }

        using var client = _fixture.CreateEntitledClient();

        var body = await GetJsonAsync(client, "/api/scheduling/v1/resources/cnc-overload/overload");

        var span = Assert.Single(body.EnumerateArray().ToArray());
        Assert.Equal(2m, span.GetProperty("peakDemand").GetDecimal());
        Assert.Equal(1m, span.GetProperty("peakExcess").GetDecimal());
        Assert.StartsWith("2026-07-28T10:00:00", span.GetProperty("startUtc").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overload_without_a_calendar_is_a_not_found_not_an_empty_list()
    {
        // "No overload" would be a lie in the most dangerous direction: there is no capacity
        // to compare demand against at all.
        using var client = _fixture.CreateEntitledClient();

        using var response = await client.GetAsync("/api/scheduling/v1/resources/never-configured/overload");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Standard_revisions_are_listed_with_their_quarantine_reasons()
    {
        var standard = OperationStandard.Register(
            Guid.NewGuid(), _fixture.TenantId, "op.edge-banding",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["material"] = "class-a" });
        standard.Import(Guid.NewGuid(), unitMinutes: 4.5m, workforce: 1m, defaultDependency: DependencyType.FinishToStart,
            releaseThresholdFraction: 0.5m, importedAtUtc: Seeded);
        standard.Import(Guid.NewGuid(), unitMinutes: null, workforce: 1m, defaultDependency: null,
            releaseThresholdFraction: null, importedAtUtc: Seeded.AddHours(1));

        await using (var database = _fixture.NewDbContext())
        {
            database.OperationStandards.Add(standard);
            await database.SaveChangesAsync();
        }

        using var client = _fixture.CreateEntitledClient();

        var summaries = await GetJsonAsync(client, "/api/scheduling/v1/standards");
        var summary = summaries.EnumerateArray()
            .Single(entry => entry.GetProperty("sourceTaskKey").GetString() == "op.edge-banding");
        Assert.Equal("class-a", summary.GetProperty("qualifiers").GetProperty("material").GetString());

        var revisions = Root(await client.GetStringAsync(
            "/api/scheduling/v1/standards/op.edge-banding/revisions"));
        var listed = revisions.EnumerateArray().ToArray();
        Assert.Equal(2, listed.Length);

        // The incomplete import is visible AND quarantined: hiding it would leave a planner
        // wondering why the norm never arrived.
        var quarantined = listed.Single(revision => revision.GetProperty("state").GetString() == "quarantined");
        Assert.NotEmpty(quarantined.GetProperty("quarantineReasons").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task An_unknown_standard_key_answers_not_found()
    {
        using var client = _fixture.CreateEntitledClient();

        using var response = await client.GetAsync("/api/scheduling/v1/standards/op.does-not-exist/revisions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
