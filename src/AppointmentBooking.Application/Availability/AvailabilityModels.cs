using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.Application.Availability;

public sealed record AppointmentInterval(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

public sealed record AvailableSlot(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc);

/// <summary>
/// Snapshot supplied by the future application service. Busy intervals must contain
/// only confirmed bookings for the selected employee. Null hours means no schedule.
/// </summary>
public sealed record AvailabilityInput(
    DateOnly Date,
    TimeZoneInfo BusinessTimeZone,
    int DurationMinutes,
    WorkingHoursModel? Hours,
    IReadOnlyList<DayOffModel> DaysOff,
    IReadOnlyList<AppointmentInterval> ConfirmedBookings,
    bool StaffIsActive = true,
    bool ServiceIsActive = true,
    bool OfferingIsActive = true);
