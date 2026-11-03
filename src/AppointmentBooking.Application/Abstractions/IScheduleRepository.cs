using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.Application.Abstractions;

public interface IScheduleRepository
{
    Task<WorkingHoursModel?> GetHoursAsync(Guid staffMemberId, CancellationToken ct);
    Task<IReadOnlyList<DayOffModel>> GetDaysOffForDateAsync(Guid staffMemberId, DateOnly date, CancellationToken ct);
}
