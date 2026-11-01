namespace AppointmentBooking.Application.Scheduling;

/// <summary>Pure schedule validation; permissions and database mutations belong to services.</summary>
public static class ScheduleRules
{
    public static void Validate(WorkingHoursModel hours)
    {
        ArgumentNullException.ThrowIfNull(hours);
        if (hours.OpensAt >= hours.ClosesAt)
            throw new ArgumentException("Opening must be before closing; overnight hours are unsupported.", nameof(hours));
        if (hours.BreakStartsAt.HasValue != hours.BreakEndsAt.HasValue)
            throw new ArgumentException("Both break endpoints must be supplied together.", nameof(hours));
        if (hours.BreakStartsAt is { } start && hours.BreakEndsAt is { } end &&
            (start < hours.OpensAt || end > hours.ClosesAt || start >= end))
            throw new ArgumentException("The break must be ordered and fit inside working hours.", nameof(hours));
    }

    public static void Validate(DayOffModel dayOff)
    {
        ArgumentNullException.ThrowIfNull(dayOff);
        if (dayOff.Weekday.HasValue == dayOff.Date.HasValue)
            throw new ArgumentException("Supply exactly one weekday or date.", nameof(dayOff));
        if (dayOff.Weekday is { } weekday && !Enum.IsDefined(weekday))
            throw new ArgumentException("Weekday must be between Sunday and Saturday.", nameof(dayOff));
    }

    /// <summary>Validates the complete day-off set for one employee.</summary>
    public static void ValidateDaysOff(IReadOnlyList<DayOffModel> daysOff)
    {
        ArgumentNullException.ThrowIfNull(daysOff);
        var weekdays = new HashSet<DayOfWeek>();
        var dates = new HashSet<DateOnly>();
        foreach (var dayOff in daysOff)
        {
            Validate(dayOff);
            if (dayOff.Weekday is { } weekday && !weekdays.Add(weekday))
                throw new ArgumentException("Duplicate recurring weekday off.", nameof(daysOff));
            if (dayOff.Date is { } date && !dates.Add(date))
                throw new ArgumentException("Duplicate specific date off.", nameof(daysOff));
        }
    }

    /// <summary>
    /// Checks an addition against the employee's current days off. The future
    /// service must still persist under the agreed lock and unique constraints.
    /// </summary>
    public static void ValidateDayOffAddition(DayOffModel proposed, IReadOnlyList<DayOffModel> existing)
    {
        Validate(proposed);
        ValidateDaysOff(existing);
        if (existing.Contains(proposed))
            throw new ArgumentException("This day off already exists for the employee.", nameof(proposed));
    }

    public static bool IsDayOff(DateOnly date, IReadOnlyList<DayOffModel> daysOff)
    {
        ArgumentNullException.ThrowIfNull(daysOff);
        var matches = false;
        foreach (var dayOff in daysOff)
        {
            Validate(dayOff);
            matches |= dayOff.Date == date || dayOff.Weekday == date.DayOfWeek;
        }
        return matches;
    }
}
