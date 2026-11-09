using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Availability;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Scheduling;

/// <summary>Authorized schedule mutations using the shared booking transaction lock.</summary>
public sealed class ScheduleService(
    IAccessService access, IScheduleRepository schedules, IBookingRepository bookings,
    IBusinessSettingsRepository settings, IUnitOfWork unitOfWork,
    IStaffOperationLock staffLock, IClock clock, ScheduleChangeValidator validator)
{
    public async Task SetHoursAsync(Actor actor, Guid staffId, WorkingHoursModel proposed, CancellationToken ct)
    {
        await RejectEmptyStaffIdAsync(actor, staffId, ct);
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffId, ct);
        // Authorize only after waiting: a pre-lock tracked staff read can be stale.
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        try { ScheduleRules.Validate(proposed); }
        catch (ArgumentException ex)
        {
            throw new AppException(ErrorCodes.InvalidWorkingHours, ex.Message, AppErrorKind.Validation);
        }

        var now = clock.UtcNow;
        var business = await settings.GetAsync(ct)
            ?? throw new AppException(ErrorCodes.BusinessSettingsNotFound, "Missing business settings.", AppErrorKind.NotFound);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new AppException(ErrorCodes.InvalidTimeZone, "Invalid business timezone.", AppErrorKind.Validation);
        }
        var days = await schedules.ListDaysOffAsync(staffId, ct);
        var future = await bookings.GetFutureConfirmedAsync(staffId, now, ct);
        var conflicts = validator.FindConflicts(proposed,
            days.Select(d => new DayOffModel(d.Weekday, d.Date)).ToArray(),
            future.Select(b => new AppointmentInterval(b.StartsAtUtc, b.EndsAtUtc)).ToArray(), zone, now);
        if (conflicts.Count != 0)
            throw new AppException(ErrorCodes.ScheduleConflictsWithBookings,
                "The proposed schedule conflicts with future confirmed bookings.", AppErrorKind.Conflict);

        var hours = await schedules.GetWorkingHoursAsync(staffId, ct);
        if (hours is null)
            schedules.Add(new WorkingHours(staffId, proposed.OpensAt, proposed.ClosesAt,
                proposed.BreakStartsAt, proposed.BreakEndsAt));
        else
            hours.Update(proposed.OpensAt, proposed.ClosesAt, proposed.BreakStartsAt, proposed.BreakEndsAt);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<DayOffDto>> ListDaysOffAsync(Actor actor, Guid staffId, CancellationToken ct)
    {
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        return (await schedules.ListDaysOffAsync(staffId, ct))
            .Select(d => new DayOffDto(d.Id, d.Weekday, d.Date)).ToArray();
    }

    public async Task<DayOffDto> AddDayOffAsync(Actor actor, Guid staffId, DayOffModel proposed, CancellationToken ct)
    {
        await RejectEmptyStaffIdAsync(actor, staffId, ct);
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffId, ct);
        // Authorize only after waiting: a pre-lock tracked staff read can be stale.
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        try { ScheduleRules.Validate(proposed); }
        catch (ArgumentException ex)
        {
            throw new AppException(ErrorCodes.InvalidDayOff, ex.Message, AppErrorKind.Validation);
        }
        var existing = await schedules.ListDaysOffAsync(staffId, ct);
        if (existing.Any(d => d.Weekday == proposed.Weekday && d.Date == proposed.Date))
            throw new AppException(ErrorCodes.DuplicateDayOff, "This day off already exists.", AppErrorKind.Conflict);
        var future = await bookings.GetFutureConfirmedAsync(staffId, clock.UtcNow, ct);
        if (future.Count != 0)
        {
            var business = await settings.GetAsync(ct)
                ?? throw new AppException(ErrorCodes.BusinessSettingsNotFound, "Missing business settings.", AppErrorKind.NotFound);
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new AppException(ErrorCodes.InvalidTimeZone, "Invalid business timezone.", AppErrorKind.Validation);
            }
            foreach (var booking in future)
            {
                var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(booking.StartsAtUtc, zone));
                var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(booking.EndsAtUtc.AddTicks(-1), zone));
                for (var date = start; ; date = date.AddDays(1))
                {
                    if (proposed.Date == date || proposed.Weekday == date.DayOfWeek)
                        throw new AppException(ErrorCodes.ScheduleConflictsWithBookings,
                            "The day off conflicts with a future confirmed booking.", AppErrorKind.Conflict);
                    if (date >= end) break;
                }
            }
        }
        var day = new StaffDayOff(staffId, proposed.Weekday, proposed.Date);
        schedules.Add(day);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DayOffDto(day.Id, day.Weekday, day.Date);
    }

    public async Task RemoveDayOffAsync(Actor actor, Guid staffId, Guid dayOffId, CancellationToken ct)
    {
        await RejectEmptyStaffIdAsync(actor, staffId, ct);
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffId, ct);
        // Authorize only after waiting: a pre-lock tracked staff read can be stale.
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        var day = await schedules.GetDayOffAsync(staffId, dayOffId, ct)
            ?? throw new AppException(ErrorCodes.DayOffNotFound, "Day off not found.", AppErrorKind.NotFound);
        schedules.Remove(day);
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task RejectEmptyStaffIdAsync(Actor actor, Guid staffId, CancellationToken ct)
    {
        if (staffId != Guid.Empty)
            return;

        // Preserve the access contract's caller-identity check before reporting a missing profile.
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        throw new AppException(ErrorCodes.StaffNotFound, "Staff member not found.", AppErrorKind.NotFound);
    }
}
