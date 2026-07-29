using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SpaceOS.Modules.Hosting.Authorization;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Host.Contracts;
using SpaceOS.Modules.Scheduling.Host.Projections;
using NodaTime;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using SpaceOS.Modules.Scheduling.Infrastructure.Persistence;

namespace SpaceOS.Modules.Scheduling.Host;

/// <summary>
/// The read-only published API (ADR-069 §6, PLAN-03 M3) — the Doorstar consumption gate.
/// </summary>
/// <remarks>
/// <para>
/// Read-only on purpose: writes arrive in phase 2 with idempotency keys and FSM-guarded
/// approvals. Shipping the read half first lets a consumer build against a stable contract
/// while the write semantics are still being decided, and nothing here can corrupt a plan.
/// </para>
/// <para>
/// Every endpoint sits behind <c>RequireEnabledModule</c>: authentication proves who is
/// calling, but only the entitlement gate proves that this tenant bought this module. It is
/// applied to the GROUP rather than per endpoint, so a route added later cannot forget it.
/// </para>
/// <para>
/// Tenant filtering is NOT written here at all. The EF query filter and the RLS policies do
/// it, and a hand-written <c>Where(tenantId)</c> in an endpoint would be a third, silently
/// diverging copy of the rule.
/// </para>
/// </remarks>
public static class SchedulingEndpoints
{
    /// <summary>Maps the published read contract under <c>/api/scheduling/v1</c>.</summary>
    public static IEndpointRouteBuilder MapSchedulingApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints
            .MapGroup(SchedulingContractInfo.BasePath)
            .RequireEnabledModule(SchedulingModule.Descriptor.ModuleId)
            .WithTags("scheduling");

        group.MapGet("/runs", GetRunsAsync)
            .WithName("listScheduleRuns")
            .WithSummary("Lists scheduling runs, newest first.");

        group.MapGet("/runs/{runId:guid}", GetRunAsync)
            .WithName("getScheduleRun")
            .WithSummary("Gets one scheduling run and the state of its revision chain.");

        group.MapGet("/runs/{runId:guid}/proposal", GetProposalAsync)
            .WithName("getProposal")
            .WithSummary("Gets the active plan of a run: operations, resolved dependencies and provenance.");

        group.MapGet("/resources", GetResourcesAsync)
            .WithName("listResources")
            .WithSummary("Lists resources with their current calendar revision.");

        group.MapGet("/resources/{resourceKey}/calendar", GetCalendarAsync)
            .WithName("getResourceCalendar")
            .WithSummary("Gets a resource's current calendar revision in full.");

        group.MapGet("/resources/{resourceKey}/overload", GetOverloadAsync)
            .WithName("getResourceOverload")
            .WithSummary("Lists periods where committed demand exceeds the resource's capacity.");

        group.MapGet("/standards", GetStandardsAsync)
            .WithName("listOperationStandards")
            .WithSummary("Lists operation standards and the state of their revision chains.");

        group.MapGet("/standards/{sourceTaskKey}/revisions", GetStandardRevisionsAsync)
            .WithName("listStandardRevisions")
            .WithSummary("Lists every revision of one operation standard, quarantine reasons included.");

        return endpoints;
    }

    private static async Task<IResult> GetRunsAsync(
        SchedulingDbContext database,
        CancellationToken cancellationToken,
        Guid? projectId = null)
    {
        var query = database.ScheduleRuns.AsNoTracking();
        if (projectId is { } project)
        {
            query = query.Where(run => run.Project == ProjectRef.From(project));
        }

        var runs = await query
            .Include(run => run.Revisions)
            .OrderByDescending(run => run.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Results.Ok(runs.Select(run => run.ToSummary()).ToArray());
    }

    private static async Task<IResult> GetRunAsync(
        Guid runId,
        SchedulingDbContext database,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(database, runId, cancellationToken);

        return run is null
            ? NotFound(context, $"Schedule run {runId} was not found.")
            : Results.Ok(run.ToSummary());
    }

    private static async Task<IResult> GetProposalAsync(
        Guid runId,
        SchedulingDbContext database,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(database, runId, cancellationToken);
        if (run is null)
        {
            return NotFound(context, $"Schedule run {runId} was not found.");
        }

        // Published first, then the newest live revision. A consumer asking for "the plan"
        // means the one in force; only when nothing is published yet is the latest proposal
        // the honest answer — and a discarded or superseded revision is never either.
        var revision = run.PublishedRevision
            ?? run.Revisions
                .Where(candidate => !candidate.IsTerminal)
                .OrderByDescending(candidate => candidate.Sequence)
                .FirstOrDefault();

        if (revision is null)
        {
            return NotFound(context, $"Schedule run {runId} has no active revision.");
        }

        // Dates are resolved against the calendars the plan is PINNED to, not today's. That is
        // what makes them reproducible: the same revision answers with the same instants even
        // after a newer calendar is approved (PlanDatingTests proves it).
        var pinned = await PinnedCalendarsAsync(database, revision.CalendarRevisions, cancellationToken);
        var dates = PlanDating
            .Resolve(revision, pinned, DatingHorizon)
            .ToDictionary(dated => dated.OperationId, StringComparer.Ordinal);

        // What the plan would collide with: its own operations plus everything already reserved
        // on those resources. The plan alone respects capacity — the solver saw to that — so
        // reporting only that would be an empty answer dressed up as an answer.
        var conflicts = await CapacityConflictsAsync(database, dates, pinned, cancellationToken);

        return Results.Ok(revision.ToProposal(run.Id, dates, conflicts));
    }

    private static async Task<IResult> GetResourcesAsync(
        SchedulingDbContext database,
        CancellationToken cancellationToken)
    {
        var calendars = await CurrentCalendarsAsync(database, cancellationToken);

        return Results.Ok(calendars
            .OrderBy(calendar => calendar.ResourceKey, StringComparer.Ordinal)
            .Select(calendar => calendar.ToDto())
            .ToArray());
    }

    private static async Task<IResult> GetCalendarAsync(
        string resourceKey,
        SchedulingDbContext database,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var calendar = await CurrentCalendarAsync(database, resourceKey, cancellationToken);

        return calendar is null
            ? NotFound(context, $"Resource '{resourceKey}' has no calendar revision.")
            : Results.Ok(calendar.ToDetail());
    }

    private static async Task<IResult> GetOverloadAsync(
        string resourceKey,
        SchedulingDbContext database,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var calendar = await CurrentCalendarAsync(database, resourceKey, cancellationToken);
        if (calendar is null)
        {
            // Without a calendar there is no capacity to compare demand against. Answering
            // "no overload" would be a lie in the most dangerous direction.
            return NotFound(context, $"Resource '{resourceKey}' has no calendar revision.");
        }

        var reservations = await database.CapacityReservations
            .AsNoTracking()
            .Where(reservation => reservation.ResourceKey == resourceKey)
            .ToListAsync(cancellationToken);

        return Results.Ok(OverloadDetector
            .Detect(reservations, calendar.Capacity)
            .Select(span => span.ToDto())
            .ToArray());
    }

    private static async Task<IResult> GetStandardsAsync(
        SchedulingDbContext database,
        CancellationToken cancellationToken)
    {
        var standards = await database.OperationStandards
            .AsNoTracking()
            .Include(standard => standard.Revisions)
            .OrderBy(standard => standard.SourceTaskKey)
            .ToListAsync(cancellationToken);

        return Results.Ok(standards.Select(standard => standard.ToSummary()).ToArray());
    }

    private static async Task<IResult> GetStandardRevisionsAsync(
        string sourceTaskKey,
        SchedulingDbContext database,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var standards = await database.OperationStandards
            .AsNoTracking()
            .Include(standard => standard.Revisions)
            .Where(standard => standard.SourceTaskKey == sourceTaskKey)
            .ToListAsync(cancellationToken);

        if (standards.Count == 0)
        {
            return NotFound(context, $"Operation standard '{sourceTaskKey}' was not found.");
        }

        // One source key can identify several standards, told apart by their qualifiers.
        // Flattening them is correct here: the caller asked for the revisions of that key,
        // and each revision carries the key it belongs to.
        return Results.Ok(standards
            .SelectMany(standard => standard.Revisions.Select(revision => revision.ToDto(standard.SourceTaskKey)))
            .OrderBy(revision => revision.Revision)
            .ToArray());
    }

    private static Task<ScheduleRun?> LoadRunAsync(
        SchedulingDbContext database,
        Guid runId,
        CancellationToken cancellationToken) =>
        database.ScheduleRuns
            .AsNoTracking()
            .Include(run => run.Revisions)
            .ThenInclude(revision => revision.Operations)
            .FirstOrDefaultAsync(run => run.Id == runId, cancellationToken);

    /// <summary>
    /// The calendar revision currently in force per resource: the highest revision number
    /// that has not been closed.
    /// </summary>
    /// <summary>
    /// How far ahead working time is materialised when dating a plan.
    /// </summary>
    /// <remarks>
    /// A year covers any plan this module produces today, and the axis is built per request —
    /// generous is cheap here, while a horizon that is too short would refuse to date a
    /// perfectly valid long plan.
    /// </remarks>
    private static readonly Duration DatingHorizon = Duration.FromDays(365);

    /// <summary>Detects where the dated plan collides with committed reservations.</summary>
    private static async Task<IReadOnlyList<ResourceOverload>> CapacityConflictsAsync(
        SchedulingDbContext database,
        IReadOnlyDictionary<string, DatedOperation> dates,
        IReadOnlyDictionary<string, ResourceCalendarRevision> pinnedCalendars,
        CancellationToken cancellationToken)
    {
        if (dates.Count == 0)
        {
            // An undated plan cannot be compared with anything on the clock. Returning an empty
            // conflict list would read as "no conflicts", so the caller sees the dates are null
            // and knows the question was not answered.
            return [];
        }

        var demands = new Dictionary<string, List<CapacityDemand>>(StringComparer.Ordinal);
        var capacities = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var (resourceKey, calendar) in pinnedCalendars)
        {
            demands[resourceKey] = [];
            capacities[resourceKey] = calendar.Capacity;
        }

        foreach (var dated in dates.Values)
        {
            if (demands.TryGetValue(dated.ResourceKey, out var bucket))
            {
                bucket.Add(new CapacityDemand(
                    dated.StartUtc.ToDateTimeOffset(), dated.FinishUtc.ToDateTimeOffset(), 1m));
            }
        }

        var resourceKeys = demands.Keys.ToArray();
        var reservations = await database.CapacityReservations
            .AsNoTracking()
            .Where(reservation => resourceKeys.Contains(reservation.ResourceKey))
            .ToListAsync(cancellationToken);

        foreach (var reservation in reservations.Where(item => item.OccupiesCapacity))
        {
            if (demands.TryGetValue(reservation.ResourceKey, out var bucket))
            {
                bucket.Add(new CapacityDemand(reservation.StartUtc, reservation.EndUtc, reservation.Quantity));
            }
        }

        return ProposalOverloadAnalyzer.Detect(
            demands.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<CapacityDemand>)entry.Value, StringComparer.Ordinal),
            capacities);
    }

    /// <summary>Loads exactly the calendar revisions a plan was pinned to.</summary>
    /// <remarks>
    /// Not "the current calendar of each resource": that would date a published plan against a
    /// calendar approved after it, and the shop floor would see its schedule move on its own.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, ResourceCalendarRevision>> PinnedCalendarsAsync(
        SchedulingDbContext database,
        IReadOnlyDictionary<string, int> pins,
        CancellationToken cancellationToken)
    {
        if (pins.Count == 0)
        {
            return new Dictionary<string, ResourceCalendarRevision>(StringComparer.Ordinal);
        }

        var resourceKeys = pins.Keys.ToArray();
        var candidates = await database.CalendarRevisions
            .AsNoTracking()
            .Include(calendar => calendar.Exceptions)
            .Where(calendar => resourceKeys.Contains(calendar.ResourceKey))
            .ToListAsync(cancellationToken);

        // Filtered in memory against the pin map: a composite (key, revision) IN clause would
        // be less readable than the handful of rows this loads for one plan.
        return candidates
            .Where(calendar => pins.TryGetValue(calendar.ResourceKey, out var pinnedRevision)
                               && calendar.Revision == pinnedRevision)
            .ToDictionary(calendar => calendar.ResourceKey, StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<ResourceCalendarRevision>> CurrentCalendarsAsync(
        SchedulingDbContext database,
        CancellationToken cancellationToken)
    {
        var calendars = await database.CalendarRevisions
            .AsNoTracking()
            .Include(calendar => calendar.Exceptions)
            .Where(calendar => calendar.EffectiveToUtc == null)
            .ToListAsync(cancellationToken);

        // Grouped in memory after a narrow filter, not with a window function: an open
        // revision per resource is a handful of rows, and the readable version is the one a
        // maintainer can still verify against the aggregate's rules.
        return calendars
            .GroupBy(calendar => calendar.ResourceKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(calendar => calendar.Revision).First())
            .ToArray();
    }

    private static async Task<ResourceCalendarRevision?> CurrentCalendarAsync(
        SchedulingDbContext database,
        string resourceKey,
        CancellationToken cancellationToken)
    {
        var calendars = await database.CalendarRevisions
            .AsNoTracking()
            .Include(calendar => calendar.Exceptions)
            .Where(calendar => calendar.ResourceKey == resourceKey && calendar.EffectiveToUtc == null)
            .ToListAsync(cancellationToken);

        return calendars.OrderByDescending(calendar => calendar.Revision).FirstOrDefault();
    }

    /// <summary>
    /// RFC 9457 ProblemDetails carrying the same correlationId the hosting package uses for
    /// its 401/403 responses, so one identifier follows a request across every layer.
    /// </summary>
    private static IResult NotFound(HttpContext context, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            type: "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.5",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["correlationId"] = context.TraceIdentifier,
            });
}
