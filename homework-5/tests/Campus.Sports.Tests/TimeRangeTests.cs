using Campus.Sports.Api.Domain;
using Xunit;

namespace Campus.Sports.Tests;

public class TimeRangeTests
{
    [Fact]
    public void ParseLegacy_ConvertsMoscowLocalTimeToUtc()
    {
        var range = TimeRange.ParseLegacy("2026-11-05 18:00-19:30");

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 16, 30, 0, TimeSpan.Zero), range.End);
        Assert.Equal(TimeSpan.FromMinutes(90), range.Duration);
    }

    [Theory]
    [InlineData("2026-11-05 18:00-19:30")]
    [InlineData("2027-01-31 07:15-08:00")]
    public void LegacyFormat_RoundTrip(string slot) =>
        Assert.Equal(slot, TimeRange.ParseLegacy(slot).ToLegacyString());

    [Theory]
    [InlineData("")]
    [InlineData("18:00-19:30")]              // нет даты
    [InlineData("2026-11-05 19:30-18:00")]   // конец раньше начала
    [InlineData("2026-11-05 18:00-18:05")]   // меньше 15 минут
    [InlineData("2026-13-05 18:00-19:00")]   // несуществующая дата
    [InlineData("2026-11-05 25:00-26:00")]   // некорректное время
    public void TryParseLegacy_RejectsInvalid(string slot) =>
        Assert.False(TimeRange.TryParseLegacy(slot, out _));

    [Fact]
    public void AdjacentSlots_DoNotOverlap_HalfOpenInterval()
    {
        var a = TimeRange.ParseLegacy("2026-11-05 18:00-19:30");
        var b = TimeRange.ParseLegacy("2026-11-05 19:30-21:00");
        var c = TimeRange.ParseLegacy("2026-11-05 19:00-20:00");

        Assert.False(a.Overlaps(b));
        Assert.True(a.Overlaps(c));
        Assert.True(c.Overlaps(b));
    }
}
