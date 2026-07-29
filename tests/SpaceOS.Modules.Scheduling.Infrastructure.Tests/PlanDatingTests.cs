using System;
using System.Collections.Generic;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Domain.Resources;
using SpaceOS.Modules.Scheduling.Domain.Schedules;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Dating a stored plan from the calendars it was PINNED to.
/// </summary>
/// <remarks>
/// Root's condition on the contract round (2026-07-29): the reproducibility of these dates
/// holds only if a pinned calendar revision cannot change underneath a plan. That is asserted
/// here rather than assumed — the whole decision to resolve dates at read time rests on it.
/// </remarks>
public sealed class PlanDatingTests
{
    private const string Zone = "Europe/Budapest";

    private static readonly ProjectRef Project = ProjectRef.From(Guid.Parse("77777777-8888-4999-8aaa-bbbbbbbbbbbb"));
    private static readonly KernelWorkScope Scope = KernelWorkScope.Create(
        Project,
        EpicRef.From(Guid.Parse("22222222-3333-4444-8555-666666666666")),
        TaskRef.From(Guid.Parse("33333333-4444-4555-8666-777777777777")));

    /// <summary>Monday 2026-08-03, midnight local — the plan's timeline origin.</summary>
    private static readonly DateTimeOffset Origin =
        new LocalDateTime(2026, 8, 3, 0, 0).InZoneLeniently(DateTimeZoneProviders.Tzdb[Zone]).ToInstant()
            .ToDateTimeOffset();

    private static ResourceCalendarRevision Calendar(int revision, int fromHour, int toHour)
    {
        var shifts = new[]
            {
                IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Wednesday,
                IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday,
            }
            .Select(day => new RecurringShift((int)day, new DayRange(fromHour * 60, toHour * 60), []))
            .ToArray();

        var calendar = ResourceCalendarRevision.CreateDraft(
            Guid.NewGuid(),
            tenantId: Guid.NewGuid(),
            resourceKey: "r1",
            revision: revision,
            timeZoneId: Zone,
            capacity: 1m,
            capacityPolicy: CapacityPolicy.Integer,
            effectiveFromUtc: Origin,
            shifts: shifts);

        calendar.Approve();
        return calendar;
    }

    private static ScheduleRevision PlanPinnedToRevision(int pinnedRevision)
    {
        var run = ScheduleRun.Open(Guid.NewGuid(), Guid.NewGuid(), Project, Origin);

        return run.AddProposal(
            Guid.NewGuid(),
            [
                new OperationPlan
                {
                    OperationId = "a",
                    Scope = Scope,
                    ResourceKey = "r1",
                    StartMinute = 0m,
                    FinishMinute = 120m,
                    AutomaticallyPlanned = true,
                },
            ],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = pinnedRevision },
            Origin,
            dependencies: null,
            timelineOriginUtc: Origin);
    }

    private static IReadOnlyList<DatedOperation> Resolve(
        ScheduleRevision revision,
        ResourceCalendarRevision calendar) =>
        PlanDating.Resolve(
            revision,
            new Dictionary<string, ResourceCalendarRevision>(StringComparer.Ordinal) { ["r1"] = calendar },
            Duration.FromDays(30));

    [Fact]
    public void Working_minutes_resolve_against_the_pinned_calendar()
    {
        // Shift 08:00–16:00: the first two working hours are Monday 08:00–10:00.
        var dated = Assert.Single(Resolve(PlanPinnedToRevision(1), Calendar(1, 8, 16)));

        Assert.Equal("a", dated.OperationId);
        Assert.Equal(LocalInstant(2026, 8, 3, 8), dated.StartUtc);
        Assert.Equal(LocalInstant(2026, 8, 3, 10), dated.FinishUtc);
    }

    [Fact]
    public void A_newer_calendar_revision_does_not_move_an_existing_plan()
    {
        // ROOT'S CONDITION. Revision 2 starts the day two hours earlier, so a plan resolved
        // against it WOULD start at 06:00. The published plan is pinned to revision 1 and must
        // keep its dates: a shop floor does not find its schedule shifted because somebody
        // approved a new calendar this morning.
        var plan = PlanPinnedToRevision(1);

        var asPinned = Assert.Single(Resolve(plan, Calendar(1, 8, 16)));
        var asNewer = Assert.Single(Resolve(plan, Calendar(2, 6, 14)));

        Assert.Equal(LocalInstant(2026, 8, 3, 8), asPinned.StartUtc);

        // The difference is real — this is not a test that passes because nothing happens.
        Assert.Equal(LocalInstant(2026, 8, 3, 6), asNewer.StartUtc);
        Assert.NotEqual(asPinned.StartUtc, asNewer.StartUtc);
    }

    [Fact]
    public void An_approved_calendar_revision_refuses_to_be_edited()
    {
        // The other half of the same guarantee: the pin can only stay honest if the revision it
        // points at is frozen. Adding an exception to an approved revision would rewrite the
        // history of every plan computed against it.
        var calendar = Calendar(1, 8, 16);

        var act = () => calendar.AddException(CalendarException.Create(
            Guid.NewGuid(), new DateOnly(2026, 8, 5), CalendarExceptionKind.Closure));

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void A_revision_without_an_origin_is_not_dated_at_all()
    {
        // Rather than inventing one. A plausible-but-arbitrary date in front of a planner is
        // worse than an absent one.
        var run = ScheduleRun.Open(Guid.NewGuid(), Guid.NewGuid(), Project, Origin);
        var undated = run.AddProposal(
            Guid.NewGuid(),
            [
                new OperationPlan
                {
                    OperationId = "a",
                    Scope = Scope,
                    ResourceKey = "r1",
                    StartMinute = 0m,
                    FinishMinute = 60m,
                    AutomaticallyPlanned = true,
                },
            ],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["r1"] = 1 },
            Origin);

        Assert.Empty(Resolve(undated, Calendar(1, 8, 16)));
    }

    [Fact]
    public void A_missing_pinned_calendar_is_refused_rather_than_guessed()
    {
        var plan = PlanPinnedToRevision(1);

        Assert.Throws<ArgumentException>(() => PlanDating.Resolve(
            plan,
            new Dictionary<string, ResourceCalendarRevision>(StringComparer.Ordinal),
            Duration.FromDays(30)));
    }

    private static Instant LocalInstant(int year, int month, int day, int hour) =>
        new LocalDateTime(year, month, day, hour, 0)
            .InZoneLeniently(DateTimeZoneProviders.Tzdb[Zone])
            .ToInstant();
}
