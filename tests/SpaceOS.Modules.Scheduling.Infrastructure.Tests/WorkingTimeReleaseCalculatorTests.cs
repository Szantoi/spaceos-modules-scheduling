using System;
using NodaTime;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Threshold → release instant, working-time proportional (ADR-069 §4, final rule).
/// </summary>
public sealed class WorkingTimeReleaseCalculatorTests
{
    private const string Budapest = "Europe/Budapest";

    private static LocalTimeRange Range(int startHour, int startMinute, int endHour, int endMinute) =>
        new(new LocalTime(startHour, startMinute), new LocalTime(endHour, endMinute));

    /// <summary>07:00-16:00 with a 60-minute lunch break: 480 net minutes a day.</summary>
    private static WorkingTimeReleaseCalculator Calculator() =>
        new(new WorkingCalendar(Budapest,
        [
            new ShiftDefinition
            {
                Weekday = IsoDayOfWeek.Wednesday,
                Span = Range(7, 0, 16, 0),
                Breaks = [Range(12, 0, 13, 0)],
            },
            new ShiftDefinition
            {
                Weekday = IsoDayOfWeek.Thursday,
                Span = Range(7, 0, 16, 0),
                Breaks = [Range(12, 0, 13, 0)],
            },
        ]));

    private static Instant Local(int year, int month, int day, int hour, int minute) =>
        new LocalDateTime(year, month, day, hour, minute)
            .InZoneLeniently(DateTimeZoneProviders.Tzdb[Budapest])
            .ToInstant();

    [Fact]
    public void Half_of_a_full_day_lands_after_the_break_not_at_noon()
    {
        // 2026-07-29 is a Wednesday. Wall-clock half of 07:00-16:00 is 11:30, but half of the
        // WORKING time (480 min) is 240 min, and the morning alone offers 300 (07:00-12:00) --
        // so the release lands at 11:00, half an hour EARLIER than the naive answer.
        var release = Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 7, 0), Local(2026, 7, 29, 16, 0), 0.5m);

        Assert.Equal(Local(2026, 7, 29, 11, 0), release);
    }

    [Fact]
    public void A_threshold_past_the_morning_skips_the_break()
    {
        // 80% of 480 = 384 minutes. The morning covers 300, leaving 84 minutes after the
        // break: 13:00 + 84 min = 14:24. A wall-clock calculation would say 14:12 and release
        // against output that does not exist yet.
        var release = Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 7, 0), Local(2026, 7, 29, 16, 0), 0.8m);

        Assert.Equal(Local(2026, 7, 29, 14, 24), release);
    }

    [Fact]
    public void An_overnight_job_ignores_the_hours_nobody_works()
    {
        // Wednesday 07:00 to Thursday 16:00 = 960 working minutes. Half is 480, i.e. the whole
        // of Wednesday, so the release is Wednesday's finish.
        var release = Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 7, 0), Local(2026, 7, 30, 16, 0), 0.5m);

        Assert.Equal(Local(2026, 7, 29, 16, 0), release);
    }

    [Fact]
    public void A_full_threshold_lands_on_the_finish()
    {
        var release = Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 7, 0), Local(2026, 7, 29, 16, 0), 1m);

        Assert.Equal(Local(2026, 7, 29, 16, 0), release);
    }

    [Fact]
    public void A_fractional_minute_rounds_up_never_down()
    {
        // 1/7 of 480 = 68.57 minutes -> 69. Releasing at 68 would release against output that
        // is not finished; a minute late costs a minute, a minute early can cost a batch.
        var release = Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 7, 0), Local(2026, 7, 29, 16, 0), 1m / 7m);

        Assert.Equal(Local(2026, 7, 29, 8, 9), release);
    }

    [Fact]
    public void An_interval_with_no_working_time_is_refused()
    {
        // A release point inside a shutdown would be fiction, and silently returning the
        // finish would hide a broken calendar revision.
        var exception = Assert.Throws<InvalidOperationException>(() => Calculator().CalculateReleaseInstant(
            Local(2026, 8, 1, 7, 0), Local(2026, 8, 1, 16, 0), 0.5m)); // Saturday

        Assert.Contains("no working time", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5d)]
    [InlineData(1.5d)]
    public void A_threshold_outside_the_zero_to_one_range_is_refused(double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 7, 0), Local(2026, 7, 29, 16, 0), (decimal)threshold));
    }

    [Fact]
    public void An_inverted_interval_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Calculator().CalculateReleaseInstant(
            Local(2026, 7, 29, 16, 0), Local(2026, 7, 29, 7, 0), 0.5m));
    }
}
