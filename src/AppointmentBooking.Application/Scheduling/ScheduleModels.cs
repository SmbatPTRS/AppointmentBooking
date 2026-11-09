namespace AppointmentBooking.Application.Scheduling;

public sealed record WorkingHoursModel(
    TimeOnly OpensAt,
    TimeOnly ClosesAt,
    TimeOnly? BreakStartsAt = null,
    TimeOnly? BreakEndsAt = null);

// DayOfWeek follows .NET: Sunday = 0, Saturday = 6.
public sealed record DayOffModel(DayOfWeek? Weekday = null, DateOnly? Date = null);

public sealed record DayOffDto(Guid Id, DayOfWeek? Weekday, DateOnly? Date);
