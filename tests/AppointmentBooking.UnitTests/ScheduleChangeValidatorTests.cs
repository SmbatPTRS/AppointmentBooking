using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.UnitTests;

public sealed class ScheduleChangeValidatorTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yerevan");
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);
    private static readonly WorkingHoursModel Hours = new(new(9, 0), new(18, 0), new(13, 0), new(14, 0));
    private static AppointmentInterval Booking(DateOnly date, int hour, int minute = 0, int duration = 40)
    {
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(4)).ToUniversalTime();
        return new(start, start.AddMinutes(duration));
    }
    private static IReadOnlyList<AppointmentInterval> Conflicts(
        WorkingHoursModel hours, DayOffModel[] days, AppointmentInterval[] bookings) =>
        new ScheduleChangeValidator().FindConflicts(hours, days, bookings, Zone, Now);

    [Fact]
    public void ShorterDayReportsOnlyBookingsOutsideNewHours()
    {
        var morning = Booking(Today, 9);
        var afternoon = Booking(Today, 17);
        var proposed = Hours with { OpensAt = new(10, 0), ClosesAt = new(17, 30) };
        Assert.Equal(new[] { morning, afternoon }, Conflicts(proposed, [], [morning, Booking(Today, 11), afternoon]));
    }

    [Fact]
    public void AddedBreakRejectsOverlapAndAllowsTouchingEdges()
    {
        var overlap = Booking(Today, 12, 45, 30);
        var bookings = new[] { Booking(Today, 12, 30, 30), overlap, Booking(Today, 14) };
        Assert.Equal(new[] { overlap }, Conflicts(Hours, [], bookings));
    }

    [Fact]
    public void RecurringDayOffChecksAllSuppliedFutureWeeks()
    {
        var first = Booking(new(2026, 11, 8), 10);
        var later = Booking(new(2026, 11, 29), 10);
        var muchLater = Booking(new(2027, 1, 3), 10);
        Assert.Equal(new[] { first, later, muchLater }, Conflicts(Hours,
            [new(Weekday: DayOfWeek.Sunday)], [first, Booking(Today, 10), later, muchLater]));
    }

    [Fact]
    public void SpecificDayOffAffectsOnlyThatDateAndRemovalReleasesConflict()
    {
        var date = new DateOnly(2026, 11, 12);
        var affected = Booking(date, 10);
        AppointmentInterval[] bookings = [affected, Booking(date.AddDays(1), 10)];
        Assert.Equal(new[] { affected }, Conflicts(Hours, [new(Date: date)], bookings));
        Assert.Empty(Conflicts(Hours, [], bookings));
    }

    [Fact]
    public void UsesBusinessLocalDateEvenWhenUtcDateDiffers()
    {
        var booking = Booking(Today.AddDays(1), 1);
        Assert.Equal(Today, DateOnly.FromDateTime(booking.StartsAtUtc.UtcDateTime));
        Assert.Equal(new[] { booking }, Conflicts(new(new(0, 0), new(6, 0)),
            [new(Date: Today.AddDays(1))], [booking]));
    }

    [Fact]
    public void PastAndAlreadyStartedBookingsAreIgnoredButStartAtNowIsProtected()
    {
        var atNow = new AppointmentInterval(Now, Now.AddMinutes(40));
        var ongoing = new AppointmentInterval(Now.AddMinutes(-10), Now.AddMinutes(30));
        Assert.Equal(new[] { atNow }, Conflicts(Hours, [new(Date: Today)],
            [Booking(Today.AddDays(-1), 10), ongoing, atNow]));
    }

    [Fact]
    public void ExistingSnapshotsAreCheckedWithoutRequiringANewStartGrid()
    {
        var booking = Booking(Today, 9, 7, 40);
        Assert.Empty(Conflicts(Hours, [], [booking]));
        Assert.Equal(new[] { booking }, Conflicts(Hours with { ClosesAt = new(9, 45), BreakStartsAt = null, BreakEndsAt = null }, [], [booking]));
    }

    [Fact]
    public void AllowsOpeningClosingAndBreakEdges()
    {
        Assert.Empty(Conflicts(Hours, [], [Booking(Today, 9), Booking(Today, 17, 20),
            Booking(Today, 12, 20), Booking(Today, 14)]));
    }

    [Fact]
    public void RejectsInvalidProposalsEvenWithoutBookings()
    {
        Assert.Throws<ArgumentException>(() => Conflicts(Hours with { OpensAt = new(19, 0) }, [], []));
        Assert.Throws<ArgumentException>(() => Conflicts(Hours, [new()], []));
        Assert.Throws<ArgumentException>(() => Conflicts(Hours, [], [new(Now, Now)]));
    }

    [Fact]
    public void BookingAcrossMidnightCannotFitFixedSameDayHours()
    {
        Assert.Single(Conflicts(new(new(0, 0), new(23, 59)), [], [Booking(Today, 23, 30, 60)]));
    }

    [Fact]
    public void RejectsBookingCrossingTimezoneOffsetChange()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = new DateTimeOffset(2027, 3, 27, 0, 0, 0, TimeSpan.Zero);
        var booking = new AppointmentInterval(
            new(2027, 3, 28, 0, 30, 0, TimeSpan.Zero),
            new(2027, 3, 28, 1, 30, 0, TimeSpan.Zero));
        var conflicts = new ScheduleChangeValidator().FindConflicts(
            new(new(0, 0), new(6, 0)), [], [booking], zone, now);
        Assert.Equal(new[] { booking }, conflicts);
    }
}
