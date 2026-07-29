using System;
using SpaceOS.Modules.Scheduling.Domain.Solving;
using Xunit;
using static SpaceOS.Modules.Scheduling.Domain.Tests.Solving.SolverScenarios;

namespace SpaceOS.Modules.Scheduling.Domain.Tests.Solving;

/// <summary>
/// The shared answer to "can this request be scheduled at all", which must not be a
/// per-strategy opinion.
/// </summary>
/// <remarks>
/// The fixed-start rules below are the follow-up to the M4/2 review (business owner decision,
/// 2026-07-29): the contradiction is resolved UPWARDS, in the validator, rather than left to
/// diverge between the reference strategy and the optimiser.
/// </remarks>
public sealed class SchedulingRequestValidatorTests
{
    [Fact]
    public void Two_pins_on_the_same_minute_of_a_single_capacity_resource_are_refused()
    {
        var request = Request([Operation("a", fixedStart: 0m), Operation("b", fixedStart: 0m)]);

        var error = Assert.Throws<ArgumentException>(() => SchedulingRequestValidator.Validate(request));

        // The message has to name both operations and the resource: a planner who pinned two
        // jobs by hand needs to know WHICH two, not that "the request is invalid".
        Assert.Contains("'a'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'b'", error.Message, StringComparison.Ordinal);
        Assert.Contains("r1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Overlapping_pins_are_refused_even_when_they_start_at_different_minutes()
    {
        // 'b' starts halfway through 'a': still two jobs at once on a one-capacity resource.
        var request = Request([Operation("a"), Operation("b", fixedStart: 30m)], capacity: 1m);
        var withBothPinned = request with
        {
            Operations = [Operation("a", fixedStart: 0m), Operation("b", fixedStart: 30m)],
        };

        Assert.Throws<ArgumentException>(() => SchedulingRequestValidator.Validate(withBothPinned));
    }

    [Fact]
    public void Pins_that_fit_the_capacity_are_accepted()
    {
        var request = Request(
            [Operation("a", fixedStart: 0m), Operation("b", fixedStart: 0m)], capacity: 2m);

        var validated = SchedulingRequestValidator.Validate(request);

        Assert.Equal(2, validated.Operations.Count);
    }

    [Fact]
    public void Pins_that_follow_each_other_do_not_contend()
    {
        // Half-open intervals: work starting exactly when the previous finishes is a handover,
        // the same rule the strategies apply when they queue work themselves.
        var request = Request([Operation("a", fixedStart: 0m), Operation("b", fixedStart: 60m)]);

        var validated = SchedulingRequestValidator.Validate(request);

        Assert.Equal(2, validated.Operations.Count);
    }

    [Fact]
    public void A_pinned_milestone_never_contends_for_capacity()
    {
        // A zero-length milestone consumes nothing, so pinning one onto a busy minute is not
        // a contradiction — refusing it would reject a perfectly ordinary plan.
        var request = Request(
            [Operation("a", fixedStart: 0m), Operation("m", duration: 0m, fixedStart: 10m)]);

        var validated = SchedulingRequestValidator.Validate(request);

        Assert.Equal(2, validated.Operations.Count);
    }

    [Fact]
    public void A_fractional_capacity_admits_its_whole_units()
    {
        // 2.5 admits two concurrent operations — the same rounding the strategies use, so the
        // validator cannot refuse a request they would have accepted.
        var request = Request(
            [Operation("a", fixedStart: 0m), Operation("b", fixedStart: 0m)], capacity: 2.5m);

        var validated = SchedulingRequestValidator.Validate(request);

        Assert.Equal(2, validated.Operations.Count);
    }
}
