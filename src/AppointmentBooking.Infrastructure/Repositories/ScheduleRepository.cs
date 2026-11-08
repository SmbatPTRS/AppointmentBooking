using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class ScheduleRepository(AppDbContext db) : IScheduleRepository
{
    public Task<WorkingHoursModel?> GetHoursAsync(Guid staffMemberId, CancellationToken ct) =>
        db.WorkingHours.Where(h => h.StaffMemberId == staffMemberId)
            .Select(h => new WorkingHoursModel(h.OpensAt, h.ClosesAt, h.BreakStartsAt, h.BreakEndsAt))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<DayOffModel>> GetDaysOffForDateAsync(
        Guid staffMemberId, DateOnly date, CancellationToken ct)
    {
        var weekday = date.DayOfWeek;
        return await db.StaffDaysOff
            .Where(d => d.StaffMemberId == staffMemberId && (d.Date == date || d.Weekday == weekday))
            .Select(d => new DayOffModel(d.Weekday, d.Date))
            .ToListAsync(ct);
    }
}
