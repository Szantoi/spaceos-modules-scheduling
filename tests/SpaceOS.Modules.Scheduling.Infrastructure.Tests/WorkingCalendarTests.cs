using System;
using System.Linq;
using NodaTime;
using SpaceOS.Modules.Scheduling.Infrastructure.Calendars;
using Xunit;

namespace SpaceOS.Modules.Scheduling.Infrastructure.Tests;

/// <summary>
/// Calendar projection and DST behaviour (ADR-069 §5, ADR-070 D2).
/// </summary>
/// <remarks>
/// The DST cases are the reason this layer exists at all: with a bare offset they silently
/// produce a wrong instant, and a day's net minutes come out an hour off exactly in the week
/// nobody checks. Hungary switches on the last Sunday of March and October.
/// </remarks>
public sealed class WorkingCalendarTests
{
    private const string Budapest = "Europe/Budapest";

    private static LocalTimeRange Range(int startHour, int startMinute, int endHour, int endMinute) =>
        new(new LocalTime(startHour, startMinute), new LocalTime(endHour, endMinute));

    /// <summary>The Doorstar CNC pattern: 07:00-16:00 with 20 + 30 + 10 minutes of breaks.</summary>
    private static WorkingCalendar DoorstarPattern(IsoDayOfWeek weekday = IsoDayOfWeek.Wednesday) =>
        new(Budapest,
        [
            new ShiftDefinition
            {
                Weekday = weekday,
                Span = Range(7, 0, 16, 0),
                Breaks =
                [
                    Range(9, 0, 9, 20),
                    Range(12, 0, 12, 30),
                    Range(14, 0, 14, 10),
                ],
            },
        ]);

    [Fact]
    public void An_ordinary_day_yields_the_documented_four_hundred_and_eighty_net_minutes()
    {
        var calendar = DoorstarPattern();

        // 2026-07-29 is a Wednesday, no DST transition.
        Assert.Equal(480m, calendar.NetMinutesOn(new LocalDate(2026, 7, 29)));
    }

    [Fact]
    public void Breaks_split_the_shift_into_separate_schedulable_intervals()
    {
        var intervals = DoorstarPattern().IntervalsOn(new LocalDate(2026, 7, 29));

        Assert.Equal(4, intervals.Count);
        Assert.Equal(120m, (decimal)intervals[0].Length.TotalMinutes); // 07:00-09:00
        Assert.All(intervals, interval => Assert.True(interval.End > interval.Start));
    }

    [Fact]
    public void A_day_without_a_shift_is_not_schedulable()
    {
        var calendar = DoorstarPattern(IsoDayOfWeek.Wednesday);

        Assert.Empty(calendar.IntervalsOn(new LocalDate(2026, 8, 1))); // Saturday
        Assert.Equal(0m, calendar.NetMinutesOn(new LocalDate(2026, 8, 1)));
    }

    [Fact]
    public void The_spring_transition_shortens_a_night_shift_by_the_missing_hour()
    {
        // 2026-03-29, 02:00 -> 03:00: the hour simply does not exist, so it cannot be worked.
        var nightShift = new WorkingCalendar(Budapest,
        [
            new ShiftDefinition { Weekday = IsoDayOfWeek.Sunday, Span = Range(0, 0, 8, 0) },
        ]);

        Assert.Equal(420m, nightShift.NetMinutesOn(new LocalDate(2026, 3, 29)));
        Assert.Equal(480m, nightShift.NetMinutesOn(new LocalDate(2026, 3, 22))); // the Sunday before
    }

    [Fact]
    public void The_autumn_transition_lengthens_a_night_shift_by_the_repeated_hour()
    {
        // 2026-10-25, 03:00 -> 02:00: the hour is lived twice, and worked twice.
        var nightShift = new WorkingCalendar(Budapest,
        [
            new ShiftDefinition { Weekday = IsoDayOfWeek.Sunday, Span = Range(0, 0, 8, 0) },
        ]);

        Assert.Equal(540m, nightShift.NetMinutesOn(new LocalDate(2026, 10, 25)));
    }

    [Fact]
    public void A_shift_starting_inside_the_spring_gap_still_produces_a_usable_instant()
    {
        // 02:30 does not exist on that date. Leniently pushes it forward rather than throwing:
        // a once-a-year clock change must not take a whole day's schedule down.
        var calendar = new WorkingCalendar(Budapest,
        [
            new ShiftDefinition { Weekday = IsoDayOfWeek.Sunday, Span = Range(2, 30, 10, 0) },
        ]);

        var intervals = calendar.IntervalsOn(new LocalDate(2026, 3, 29));

        Assert.Single(intervals);
        Assert.True(intervals[0].Length.TotalMinutes > 0);
    }

    [Fact]
    public void A_day_outside_the_transition_is_unaffected_by_it()
    {
        // Guards against a fix that would "correct" every day by an hour.
        var calendar = DoorstarPattern();

        Assert.Equal(480m, calendar.NetMinutesOn(new LocalDate(2026, 3, 25)));
        Assert.Equal(480m, calendar.NetMinutesOn(new LocalDate(2026, 10, 28)));
    }

    [Fact]
    public void An_unknown_time_zone_is_rejected_at_construction()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new WorkingCalendar("Europe/Budapesst", []));

        Assert.Contains("Unknown IANA time zone", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_zone_id_travels_as_a_plain_string_for_the_wire()
    {
        // ADR-070 D2: NodaTime types stay inside this layer; the contract carries the id.
        Assert.Equal(Budapest, DoorstarPattern().ZoneId);
    }

    [Fact]
    public void Two_shifts_on_the_same_weekday_are_rejected()
    {
        var duplicated = new[]
        {
            new ShiftDefinition { Weekday = IsoDayOfWeek.Monday, Span = Range(7, 0, 12, 0) },
            new ShiftDefinition { Weekday = IsoDayOfWeek.Monday, Span = Range(13, 0, 16, 0) },
        };

        Assert.Throws<ArgumentException>(() => new WorkingCalendar(Budapest, duplicated));
    }

    [Theory]
    [InlineData(16, 0, 7, 0)]   // inverted span
    [InlineData(7, 0, 7, 0)]    // empty span
    public void A_malformed_shift_span_is_rejected(int startHour, int startMinute, int endHour, int endMinute)
    {
        var shift = new ShiftDefinition
        {
            Weekday = IsoDayOfWeek.Monday,
            Span = Range(startHour, startMinute, endHour, endMinute),
        };

        Assert.Throws<ArgumentException>(() => new WorkingCalendar(Budapest, [shift]));
    }

    [Fact]
    public void A_break_outside_the_shift_span_is_rejected()
    {
        var shift = new ShiftDefinition
        {
            Weekday = IsoDayOfWeek.Monday,
            Span = Range(7, 0, 16, 0),
            Breaks = [Range(17, 0, 17, 30)],
        };

        Assert.Throws<ArgumentException>(() => new WorkingCalendar(Budapest, [shift]));
    }

    [Fact]
    public void Overlapping_breaks_are_rejected()
    {
        // Otherwise the same minutes would be subtracted twice and the day would look shorter.
        var shift = new ShiftDefinition
        {
            Weekday = IsoDayOfWeek.Monday,
            Span = Range(7, 0, 16, 0),
            Breaks = [Range(9, 0, 9, 30), Range(9, 15, 9, 45)],
        };

        Assert.Throws<ArgumentException>(() => new WorkingCalendar(Budapest, [shift]));
    }
}
