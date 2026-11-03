using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Application.Availability;

public sealed class AvailabilityService(
    ICatalogRepository catalog, IStaffRepository staff, IScheduleRepository schedules,
    IBusinessSettingsRepository settings, IBookingRepository bookings, IClock clock,
    AvailabilityCalculator calculator)
{
    public async Task<IReadOnlyList<AvailableSlot>> ListAsync(Guid staffId, Guid offeringId, DateOnly date, CancellationToken ct)
    {
        var employee = await staff.GetByIdAsync(staffId, ct)
            ?? throw new AppException(ErrorCodes.StaffNotFound, "Employee not found.", AppErrorKind.NotFound);
        var offering = await catalog.GetOfferingByIdAsync(offeringId, ct)
            ?? throw new AppException(ErrorCodes.StaffServiceNotFound, "Offering not found.", AppErrorKind.NotFound);
        if (offering.StaffMemberId != staffId)
            throw new AppException(ErrorCodes.StaffServiceMismatch, "Offering belongs to another employee.", AppErrorKind.Validation);
        var service = await catalog.GetServiceAsync(offering.ServiceId, ct)
            ?? throw new AppException(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);
        var business = await settings.GetAsync(ct)
            ?? throw new AppException(ErrorCodes.BusinessSettingsNotFound, "Missing business settings.", AppErrorKind.NotFound);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(business.TimeZoneId); }
        catch (Exception ex) when (ex is InvalidTimeZoneException or TimeZoneNotFoundException)
        {
            throw new AppException(ErrorCodes.InvalidTimeZone, "Invalid business timezone.", AppErrorKind.Validation);
        }
        var input = new AvailabilityInput(date, zone, offering.DurationMinutes,
            await schedules.GetHoursAsync(staffId, ct), await schedules.GetDaysOffForDateAsync(staffId, date, ct),
            [], employee.IsActive, service.IsActive, offering.IsActive);
        var now = new DateTimeOffset(clock.UtcNow);
        IReadOnlyList<AvailableSlot> candidates;
        try { candidates = calculator.Calculate(input, now); }
        catch (ArgumentOutOfRangeException)
        {
            throw new AppException(ErrorCodes.BookingOutsideWindow, "Date outside the booking window.", AppErrorKind.Validation);
        }
        if (candidates.Count == 0) return candidates;

        var busy = await bookings.GetConfirmedOverlappingAsync(staffId,
            candidates.Min(s => s.StartsAtUtc).UtcDateTime, candidates.Max(s => s.EndsAtUtc).UtcDateTime, ct);
        return candidates.Where(slot => !busy.Any(b =>
            b.StartsAtUtc < slot.EndsAtUtc.UtcDateTime && b.EndsAtUtc > slot.StartsAtUtc.UtcDateTime)).ToArray();
    }
}
