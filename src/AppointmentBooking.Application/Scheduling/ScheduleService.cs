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
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        try { ScheduleRules.Validate(proposed); }
        catch (ArgumentException ex)
        {
            throw new AppException(ErrorCodes.InvalidWorkingHours, ex.Message, AppErrorKind.Validation);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffId, ct);
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
}
