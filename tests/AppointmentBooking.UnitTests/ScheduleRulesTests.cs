using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.UnitTests;

public sealed class ScheduleRulesTests
{
    [Theory]
    [InlineData(9, 9)]
    [InlineData(18, 9)]
    public void RejectsEmptyOrOvernightHours(int opening, int closing) =>
        Assert.Throws<ArgumentException>(() => ScheduleRules.Validate(
            new WorkingHoursModel(new TimeOnly(opening, 0), new TimeOnly(closing, 0))));

    [Theory]
    [InlineData(13, null)]
    [InlineData(null, 14)]
    [InlineData(8, 10)]
    [InlineData(17, 19)]
    [InlineData(14, 13)]
    [InlineData(13, 13)]
    public void RejectsInvalidBreaks(int? start, int? end) =>
        Assert.Throws<ArgumentException>(() => ScheduleRules.Validate(new WorkingHoursModel(
            new(9, 0), new(18, 0), start.HasValue ? new TimeOnly(start.Value, 0) : null,
            end.HasValue ? new TimeOnly(end.Value, 0) : null)));

    [Fact]
    public void AcceptsAbsentBreakAndBreakAtScheduleEdges()
    {
        ScheduleRules.Validate(new WorkingHoursModel(new(9, 0), new(18, 0)));
        ScheduleRules.Validate(new WorkingHoursModel(new(9, 0), new(18, 0), new(9, 0), new(10, 0)));
        ScheduleRules.Validate(new WorkingHoursModel(new(9, 0), new(18, 0), new(17, 0), new(18, 0)));
    }

    [Fact]
    public void RejectsNeitherOrBothDayOffFieldsAndInvalidWeekday()
    {
        Assert.Throws<ArgumentException>(() => ScheduleRules.Validate(new DayOffModel()));
        Assert.Throws<ArgumentException>(() => ScheduleRules.Validate(new DayOffModel(DayOfWeek.Monday, new(2026, 11, 2))));
        Assert.Throws<ArgumentException>(() => ScheduleRules.Validate(new DayOffModel((DayOfWeek)7)));
    }

    [Fact]
    public void MatchesRecurringAndSpecificDaysWithoutAffectingOtherDates()
    {
        DayOffModel[] days = [new(DayOfWeek.Sunday), new(Date: new(2026, 11, 12))];
        Assert.True(ScheduleRules.IsDayOff(new(2026, 11, 8), days));
        Assert.True(ScheduleRules.IsDayOff(new(2026, 11, 29), days));
        Assert.True(ScheduleRules.IsDayOff(new(2026, 11, 12), days));
        Assert.False(ScheduleRules.IsDayOff(new(2026, 11, 13), days));
    }
}
