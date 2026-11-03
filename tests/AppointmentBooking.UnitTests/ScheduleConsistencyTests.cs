using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.UnitTests;

public sealed class ScheduleConsistencyTests
{
    private static readonly DateOnly Date = new(2026, 11, 2);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
    private static readonly WorkingHoursModel Hours = new(new(9, 0), new(18, 0), new(13, 0), new(14, 0));
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(9, 0, 9, 40, true)]
    [InlineData(8, 59, 9, 40, false)]
    [InlineData(12, 20, 13, 0, true)]
    [InlineData(12, 21, 13, 1, false)]
    [InlineData(13, 0, 13, 40, false)]
    [InlineData(13, 59, 14, 39, false)]
    [InlineData(14, 0, 14, 40, true)]
    [InlineData(17, 20, 18, 0, true)]
    [InlineData(17, 21, 18, 1, false)]
    [InlineData(10, 0, 10, 0, false)]
    [InlineData(11, 0, 10, 0, false)]
    public void SharedRuleHonorsCompleteIntervalAndTouchingEdges(
        int startHour, int startMinute, int endHour, int endMinute, bool expected)
    {
        Assert.Equal(expected, ScheduleRules.FitsWorkingInterval(Hours,
            Date.ToDateTime(new(startHour, startMinute)), Date.ToDateTime(new(endHour, endMinute))));
    }

    [Fact]
    public void SharedRuleRejectsCrossDateIntervals()
    {
        Assert.False(ScheduleRules.FitsWorkingInterval(Hours,
            Date.ToDateTime(new(17, 0)), Date.AddDays(1).ToDateTime(new(10, 0))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(90)]
    public void AvailabilityAndScheduleMutationAgreeForEveryGridStart(int duration)
    {
        var input = new AvailabilityInput(Date, Zone, duration, Hours, [], []);
        var calculator = new AvailabilityCalculator(new BookingOptions());
        var validator = new ScheduleChangeValidator();
        for (var minute = 9 * 60; minute < 18 * 60; minute += 15)
        {
            var local = Date.ToDateTime(new(minute / 60, minute % 60));
            var start = new DateTimeOffset(local, TimeSpan.FromHours(4)).ToUniversalTime();
            var booking = new AppointmentInterval(start, start.AddMinutes(duration));
            var conflicts = validator.FindConflicts(Hours, [], [booking], Zone, Now);
            Assert.Equal(calculator.IsAvailable(input, start, Now), conflicts.Count == 0);
        }
    }

    [Fact]
    public void ChangedOpeningDoesNotInvalidateAnExistingBookingOnlyBecauseOfNewGrid()
    {
        var proposed = new WorkingHoursModel(new(9, 5), new(18, 0));
        var start = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
        var booking = new AppointmentInterval(start, start.AddMinutes(40));
        var input = new AvailabilityInput(Date, Zone, 40, proposed, [], []);
        Assert.False(new AvailabilityCalculator(new BookingOptions()).IsAvailable(input, start, Now));
        Assert.Empty(new ScheduleChangeValidator().FindConflicts(proposed, [], [booking], Zone, Now));
    }
}
