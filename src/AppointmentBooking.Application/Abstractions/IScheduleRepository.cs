using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface IScheduleRepository
{
    Task<WorkingHoursModel?> GetHoursAsync(Guid staffMemberId, CancellationToken ct);
    Task<IReadOnlyList<DayOffModel>> GetDaysOffForDateAsync(Guid staffMemberId, DateOnly date, CancellationToken ct);
    // Tracked entities for mutations. The caller owns authorization, transaction,
    // employee locking, conflict checks and SaveChanges.
    Task<WorkingHours?> GetWorkingHoursAsync(Guid staffMemberId, CancellationToken ct);
    Task<IReadOnlyList<StaffDayOff>> ListDaysOffAsync(Guid staffMemberId, CancellationToken ct);
    Task<StaffDayOff?> GetDayOffAsync(Guid staffMemberId, Guid dayOffId, CancellationToken ct);
    void Add(WorkingHours hours);
    void Add(StaffDayOff dayOff);
    void Remove(StaffDayOff dayOff);
}
