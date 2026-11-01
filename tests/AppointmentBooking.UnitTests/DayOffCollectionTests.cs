using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.UnitTests;

public sealed class DayOffCollectionTests
{
    [Fact]
    public void RejectsRepeatedWeekdayAndRepeatedDate()
    {
        Assert.Throws<ArgumentException>(() => ScheduleRules.ValidateDaysOff([
            new(Weekday: DayOfWeek.Sunday), new(Weekday: DayOfWeek.Sunday)]));
        Assert.Throws<ArgumentException>(() => ScheduleRules.ValidateDaysOff([
            new(Date: new(2026, 11, 12)), new(Date: new(2026, 11, 12))]));
    }

    [Fact]
    public void RecurringAndSpecificDayOffMayCoincide()
    {
        ScheduleRules.ValidateDaysOff([new(Weekday: DayOfWeek.Sunday), new(Date: new(2026, 11, 8))]);
    }

    [Fact]
    public void AcceptsEmptySetAllWeekdaysAndDistinctDates()
    {
        ScheduleRules.ValidateDaysOff([]);
        ScheduleRules.ValidateDaysOff(Enum.GetValues<DayOfWeek>().Select(d => new DayOffModel(Weekday: d)).ToArray());
        ScheduleRules.ValidateDaysOff([new(Date: new(2026, 11, 12)), new(Date: new(2026, 11, 13))]);
    }

    [Fact]
    public void AdditionRejectsDuplicateButAllowsSameRuleForAnotherEmployee()
    {
        var proposed = new DayOffModel(Weekday: DayOfWeek.Sunday);
        Assert.Throws<ArgumentException>(() => ScheduleRules.ValidateDayOffAddition(proposed, [proposed]));
        ScheduleRules.ValidateDayOffAddition(proposed, []);
    }

    [Fact]
    public void AdditionRejectsDuplicateDateButAllowsOtherDates()
    {
        var proposed = new DayOffModel(Date: new(2026, 11, 12));
        Assert.Throws<ArgumentException>(() => ScheduleRules.ValidateDayOffAddition(proposed, [proposed]));
        ScheduleRules.ValidateDayOffAddition(proposed, [new(Date: new(2026, 11, 13))]);
    }

    [Fact]
    public void MalformedEntriesAreRejectedEvenWhenDateWouldNotMatch()
    {
        Assert.Throws<ArgumentException>(() => ScheduleRules.ValidateDaysOff([new()]));
        Assert.Throws<ArgumentException>(() => ScheduleRules.ValidateDayOffAddition(new(), []));
    }
}
