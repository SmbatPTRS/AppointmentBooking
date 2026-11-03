using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.UnitTests;

public sealed class AvailabilityCalculatorTests
{
    private static readonly TimeZoneInfo Yerevan = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
    private static readonly DateOnly Date = new(2026, 11, 2);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);
    private static AvailabilityCalculator Calculator(int step = 15, int advance = 30) =>
        new(new BookingOptions { SlotStepMinutes = step, MaxAdvanceDays = advance });
    private static AvailabilityInput Input(int duration = 40) => new(
        Date, Yerevan, duration, new WorkingHoursModel(new(9, 0), new(18, 0), new(13, 0), new(14, 0)),
        Array.Empty<DayOffModel>(), Array.Empty<AppointmentInterval>());
    private static DateTimeOffset Utc(int hour, int minute = 0) =>
        new DateTimeOffset(2026, 11, 2, hour, minute, 0, TimeSpan.FromHours(4)).ToUniversalTime();

    [Fact]
    public void FortyMinuteServiceUsesFifteenMinuteStartsAndFitsEntireInterval()
    {
        var slots = Calculator().Calculate(Input(), Now);
        Assert.Equal(Utc(9), slots.First().StartsAtUtc);
        Assert.Equal(Utc(9, 40), slots.First().EndsAtUtc);
        Assert.Contains(slots, s => s.StartsAtUtc == Utc(9, 15));
        Assert.DoesNotContain(slots, s => s.StartsAtUtc == Utc(12, 30));
        Assert.DoesNotContain(slots, s => s.StartsAtUtc == Utc(17, 30));
        Assert.All(slots, s => Assert.Equal(TimeSpan.Zero, s.StartsAtUtc.Offset));
    }

    [Fact]
    public void AllowsTouchingBreakAndClosingBoundaries()
    {
        var slots = Calculator().Calculate(Input(30), Now);
        Assert.Contains(slots, s => s.StartsAtUtc == Utc(12, 30));
        Assert.Contains(slots, s => s.StartsAtUtc == Utc(14));
        Assert.Contains(slots, s => s.EndsAtUtc == Utc(18));
        Assert.DoesNotContain(slots, s => s.StartsAtUtc == Utc(13));
    }

    [Fact]
    public void BusyIntervalsBlockOverlapsButAllowAdjacency()
    {
        var input = Input(30) with { ConfirmedBookings = [new(Utc(10), Utc(10, 40))] };
        var slots = Calculator().Calculate(input, Now);
        Assert.Contains(slots, s => s.StartsAtUtc == Utc(9, 30));
        Assert.DoesNotContain(slots, s => s.StartsAtUtc == Utc(9, 45));
        Assert.DoesNotContain(slots, s => s.StartsAtUtc == Utc(10, 30));
        Assert.Contains(slots, s => s.StartsAtUtc == Utc(10, 45));
        Assert.True(Calculator(10).IsAvailable(input, Utc(10, 40), Now));
    }

    [Fact]
    public void RejectsStartsAtNowAndBeforeNow()
    {
        var slots = Calculator().Calculate(Input(30), Utc(10));
        Assert.All(slots, s => Assert.True(s.StartsAtUtc > Utc(10)));
        Assert.False(Calculator().IsAvailable(Input(), Utc(10), Utc(10)));
    }

    [Fact]
    public void UsesBusinessDateRatherThanUtcDateAndIncludesLastAdvanceDate()
    {
        var now = new DateTimeOffset(2026, 11, 1, 21, 0, 0, TimeSpan.Zero);
        var calculator = Calculator();
        Assert.NotEmpty(calculator.Calculate(Input(), now));
        Assert.NotEmpty(calculator.Calculate(Input() with { Date = Date.AddDays(30) }, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => calculator.Calculate(Input() with { Date = Date.AddDays(31) }, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => calculator.Calculate(Input() with { Date = Date.AddDays(-1) }, now));
    }

    [Fact]
    public void MissingHoursDaysOffAndInactiveSelectionsReturnEmpty()
    {
        var inputs = new[]
        {
            Input() with { Hours = null }, Input() with { StaffIsActive = false },
            Input() with { ServiceIsActive = false }, Input() with { OfferingIsActive = false },
            Input() with { DaysOff = [new(Date: Date)] },
            Input() with { DaysOff = [new(Weekday: Date.DayOfWeek)] }
        };
        foreach (var input in inputs)
        {
            Assert.Empty(Calculator().Calculate(input, Now));
            Assert.False(Calculator().IsAvailable(input, Utc(9), Now));
        }
    }

    [Fact]
    public void ExactValidationAgreesWithListForEveryMinuteOfDay()
    {
        var input = Input() with { ConfirmedBookings = [new(Utc(10), Utc(10, 40))] };
        var calculator = Calculator();
        var starts = calculator.Calculate(input, Now).Select(s => s.StartsAtUtc).ToHashSet();
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var start = Utc(0).AddMinutes(minute);
            Assert.Equal(starts.Contains(start), calculator.IsAvailable(input, start, Now));
        }
        Assert.False(calculator.IsAvailable(input, Utc(9).AddSeconds(1), Now));
    }

    [Fact]
    public void GridIsAnchoredAtOpeningAndLargeDurationsOrStepsDoNotWrap()
    {
        var input = Input(30) with { Hours = new(new(9, 7), new(10, 7)) };
        Assert.True(Calculator().IsAvailable(input, Utc(9, 7), Now));
        Assert.False(Calculator().IsAvailable(input, Utc(9, 15), Now));
        Assert.Empty(Calculator().Calculate(input with { DurationMinutes = int.MaxValue }, Now));
        Assert.Single(Calculator(int.MaxValue).Calculate(input, Now));
    }

    [Theory]
    [InlineData(2026, 3, 29)]
    [InlineData(2026, 10, 25)]
    public void SkipsInvalidAmbiguousAndCrossOffsetIntervals(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var input = new AvailabilityInput(date, zone, 120,
            new(new(0, 0), new(6, 0)), [], []);
        var slots = Calculator().Calculate(input, now);
        Assert.NotEmpty(slots);
        foreach (var slot in slots)
        {
            var start = TimeZoneInfo.ConvertTime(slot.StartsAtUtc, zone);
            var end = TimeZoneInfo.ConvertTime(slot.EndsAtUtc, zone);
            Assert.Equal(start.Offset, end.Offset);
            Assert.False(zone.IsInvalidTime(start.DateTime));
            Assert.False(zone.IsAmbiguousTime(start.DateTime));
            Assert.False(zone.IsInvalidTime(end.DateTime));
            Assert.False(zone.IsAmbiguousTime(end.DateTime));
            Assert.Equal(TimeSpan.FromMinutes(120), slot.EndsAtUtc - slot.StartsAtUtc);
        }
        var crossing = new DateTimeOffset(date.ToDateTime(new TimeOnly(1, 30)), zone.GetUtcOffset(date.ToDateTime(new TimeOnly(1, 30))));
        Assert.False(Calculator().IsAvailable(input, crossing, now));
    }

    [Fact]
    public void FullyBookedDayReturnsEmptyAndDoesNotAffectAnotherEmployeeSnapshot()
    {
        var busy = Input() with { ConfirmedBookings = [new(Utc(9), Utc(18))] };
        Assert.Empty(Calculator().Calculate(busy, Now));
        Assert.NotEmpty(Calculator().Calculate(Input(), Now));
    }

    [Fact]
    public void LongerServiceRemovesLateStartsWithoutChangingExistingBusySnapshots()
    {
        AppointmentInterval[] bookings = [new(Utc(10), Utc(10, 40))];
        var shortService = Input(30) with { ConfirmedBookings = bookings };
        var longService = shortService with { DurationMinutes = 60 };
        Assert.True(Calculator().IsAvailable(shortService, Utc(9, 30), Now));
        Assert.False(Calculator().IsAvailable(longService, Utc(9, 30), Now));
        Assert.True(Calculator().IsAvailable(longService, Utc(10, 45), Now));
        Assert.Equal(Utc(10, 40), bookings[0].EndsAtUtc);
    }

    [Fact]
    public void ZeroAdvanceDaysAllowsTodayOnly()
    {
        Assert.NotEmpty(Calculator(advance: 0).Calculate(Input(), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculator(advance: 0).Calculate(
            Input() with { Date = Date.AddDays(1) }, Now));
    }

    [Fact]
    public void ExactValidationComparesInstantsAndRejectsAnotherDate()
    {
        Assert.True(Calculator().IsAvailable(Input(), Utc(9).ToOffset(TimeSpan.FromHours(4)), Now));
        Assert.False(Calculator().IsAvailable(Input(), Utc(9).AddDays(1), Now));
        Assert.False(Calculator().IsAvailable(Input(), Utc(9).AddDays(-1), Now));
    }

    [Fact]
    public void DuplicateDaysOffAreInvalidInputRatherThanSilentEmptyAvailability()
    {
        var input = Input() with { DaysOff = [new(Date: Date), new(Date: Date)] };
        Assert.Throws<ArgumentException>(() => Calculator().Calculate(input, Now));
        Assert.Throws<ArgumentException>(() => Calculator().IsAvailable(input, Utc(9), Now));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(15, -1)]
    public void RejectsInvalidOptions(int step, int advance) =>
        Assert.Throws<ArgumentException>(() => Calculator(step, advance));

    [Fact]
    public void InvalidDurationAndBusyIntervalsAreNotTreatedAsEmptyAvailability()
    {
        Assert.Throws<ArgumentException>(() => Calculator().Calculate(Input(0), Now));
        Assert.Throws<ArgumentException>(() => Calculator().Calculate(
            Input() with { ConfirmedBookings = [new(Utc(10), Utc(9))] }, Now));
    }
}
