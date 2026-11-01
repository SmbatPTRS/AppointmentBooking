using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.Application.Availability;

/// <summary>
/// Shared pure interval calculation for listing and final validation.
/// It does not reserve time or provide transaction/locking guarantees.
/// </summary>
public sealed class AvailabilityCalculator
{
    private readonly int slotStepMinutes;
    private readonly int maxAdvanceDays;

    public AvailabilityCalculator(BookingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SlotStepMinutes <= 0 || options.MaxAdvanceDays < 0)
            throw new ArgumentException("Slot step must be positive and advance days nonnegative.", nameof(options));
        slotStepMinutes = options.SlotStepMinutes;
        maxAdvanceDays = options.MaxAdvanceDays;
    }

    public IReadOnlyList<AvailableSlot> Calculate(AvailabilityInput input, DateTimeOffset nowUtc)
    {
        Validate(input, nowUtc);
        if (!IsWorkingDate(input))
            return Array.Empty<AvailableSlot>();

        var slots = new List<AvailableSlot>();
        var opening = input.Date.ToDateTime(input.Hours!.OpensAt);
        var closing = input.Date.ToDateTime(input.Hours.ClosesAt);
        // Use ticks so a large configured step cannot wrap TimeOnly into another day.
        for (long ticks = opening.Ticks; ticks < closing.Ticks;
             ticks += (long)slotStepMinutes * TimeSpan.TicksPerMinute)
        {
            var slot = TryCreateSlot(input, new DateTime(ticks, DateTimeKind.Unspecified), nowUtc);
            if (slot is not null)
                slots.Add(slot);
        }
        return slots;
    }

    public bool IsAvailable(AvailabilityInput input, DateTimeOffset startsAtUtc, DateTimeOffset nowUtc)
    {
        Validate(input, nowUtc);
        if (!IsWorkingDate(input))
            return false;
        var localStart = TimeZoneInfo.ConvertTime(startsAtUtc, input.BusinessTimeZone).DateTime;
        if (DateOnly.FromDateTime(localStart) != input.Date)
            return false;
        var opening = input.Date.ToDateTime(input.Hours!.OpensAt);
        var ticksFromOpening = localStart.Ticks - opening.Ticks;
        var stepTicks = (long)slotStepMinutes * TimeSpan.TicksPerMinute;
        if (ticksFromOpening < 0 || ticksFromOpening % stepTicks != 0)
            return false;
        var slot = TryCreateSlot(input, localStart, nowUtc);
        return slot is not null && slot.StartsAtUtc == startsAtUtc;
    }

    private void Validate(AvailabilityInput input, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.BusinessTimeZone);
        ArgumentNullException.ThrowIfNull(input.DaysOff);
        ArgumentNullException.ThrowIfNull(input.ConfirmedBookings);
        if (input.DurationMinutes <= 0)
            throw new ArgumentException("Duration must be positive.", nameof(input));
        if (input.Hours is not null)
            ScheduleRules.Validate(input.Hours);
        ScheduleRules.ValidateDaysOff(input.DaysOff);
        foreach (var booking in input.ConfirmedBookings)
            if (booking.EndsAtUtc <= booking.StartsAtUtc)
                throw new ArgumentException("Busy intervals must end after they start.", nameof(input));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, input.BusinessTimeZone).DateTime);
        var daysAhead = input.Date.DayNumber - today.DayNumber;
        if (daysAhead < 0 || daysAhead > maxAdvanceDays)
            throw new ArgumentOutOfRangeException(nameof(input), "Date is outside the booking window.");
    }

    private static bool IsWorkingDate(AvailabilityInput input) =>
        input.StaffIsActive && input.ServiceIsActive && input.OfferingIsActive &&
        input.Hours is not null && !ScheduleRules.IsDayOff(input.Date, input.DaysOff);

    private static AvailableSlot? TryCreateSlot(
        AvailabilityInput input, DateTime startLocal, DateTimeOffset nowUtc)
    {
        var hours = input.Hours!;
        var opening = input.Date.ToDateTime(hours.OpensAt);
        var closing = input.Date.ToDateTime(hours.ClosesAt);
        var durationTicks = (long)input.DurationMinutes * TimeSpan.TicksPerMinute;
        if (startLocal < opening || startLocal >= closing || durationTicks > closing.Ticks - startLocal.Ticks)
            return null;
        var endLocal = startLocal.AddTicks(durationTicks);
        if (hours.BreakStartsAt is { } breakStart && hours.BreakEndsAt is { } breakEnd &&
            startLocal < input.Date.ToDateTime(breakEnd) && endLocal > input.Date.ToDateTime(breakStart))
            return null;

        var zone = input.BusinessTimeZone;
        if (zone.IsInvalidTime(startLocal) || zone.IsAmbiguousTime(startLocal) ||
            zone.IsInvalidTime(endLocal) || zone.IsAmbiguousTime(endLocal))
            return null;
        var startOffset = zone.GetUtcOffset(startLocal);
        var endOffset = zone.GetUtcOffset(endLocal);
        if (startOffset != endOffset)
            return null;
        var start = new DateTimeOffset(startLocal, startOffset).ToUniversalTime();
        var end = new DateTimeOffset(endLocal, endOffset).ToUniversalTime();
        if (start <= nowUtc || input.ConfirmedBookings.Any(b => b.StartsAtUtc < end && b.EndsAtUtc > start))
            return null;
        return new AvailableSlot(start, end);
    }
}
