using AppointmentBooking.Application.Availability;

namespace AppointmentBooking.Application.Scheduling;

/// <summary>
/// Evaluates a proposed complete schedule against the selected employee's future
/// confirmed appointments. The caller must load all affected future dates under
/// the shared transaction/employee lock; this class performs no persistence.
/// </summary>
public sealed class ScheduleChangeValidator
{
    public IReadOnlyList<AppointmentInterval> FindConflicts(
        WorkingHoursModel proposedHours,
        IReadOnlyList<DayOffModel> proposedDaysOff,
        IReadOnlyList<AppointmentInterval> confirmedBookings,
        TimeZoneInfo businessTimeZone,
        DateTimeOffset nowUtc)
    {
        ScheduleRules.Validate(proposedHours);
        ArgumentNullException.ThrowIfNull(proposedDaysOff);
        ArgumentNullException.ThrowIfNull(confirmedBookings);
        ArgumentNullException.ThrowIfNull(businessTimeZone);
        ScheduleRules.ValidateDaysOff(proposedDaysOff);

        var conflicts = new List<AppointmentInterval>();
        foreach (var booking in confirmedBookings)
        {
            if (booking.EndsAtUtc <= booking.StartsAtUtc)
                throw new ArgumentException("Booking intervals must end after they start.", nameof(confirmedBookings));
            // Historical and already-started appointments are not future bookings.
            if (booking.StartsAtUtc < nowUtc)
                continue;
            if (!Fits(booking, proposedHours, proposedDaysOff, businessTimeZone))
                conflicts.Add(booking);
        }
        return conflicts;
    }

    private static bool Fits(
        AppointmentInterval booking,
        WorkingHoursModel hours,
        IReadOnlyList<DayOffModel> daysOff,
        TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(booking.StartsAtUtc, zone);
        var end = TimeZoneInfo.ConvertTime(booking.EndsAtUtc, zone);
        var date = DateOnly.FromDateTime(start.DateTime);
        if (DateOnly.FromDateTime(end.DateTime) != date ||
            start.Offset != end.Offset ||
            zone.IsAmbiguousTime(start.DateTime) || zone.IsAmbiguousTime(end.DateTime) ||
            ScheduleRules.IsDayOff(date, daysOff))
            return false;

        // Existing bookings retain their saved duration; no new start grid or
        // current offering duration is applied when evaluating a schedule edit.
        return ScheduleRules.FitsWorkingInterval(hours, start.DateTime, end.DateTime);
    }
}
