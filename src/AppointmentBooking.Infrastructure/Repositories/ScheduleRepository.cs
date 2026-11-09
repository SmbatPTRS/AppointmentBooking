using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Scheduling;
using AppointmentBooking.Domain.Entities;
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

    public Task<WorkingHours?> GetWorkingHoursAsync(Guid staffMemberId, CancellationToken ct) =>
        db.WorkingHours.SingleOrDefaultAsync(h => h.StaffMemberId == staffMemberId, ct);

    public async Task<IReadOnlyList<StaffDayOff>> ListDaysOffAsync(Guid staffMemberId, CancellationToken ct) =>
        await db.StaffDaysOff.Where(d => d.StaffMemberId == staffMemberId)
            .OrderBy(d => d.Weekday).ThenBy(d => d.Date).ThenBy(d => d.Id)
            .ToListAsync(ct);

    public Task<StaffDayOff?> GetDayOffAsync(Guid staffMemberId, Guid dayOffId, CancellationToken ct) =>
        db.StaffDaysOff.SingleOrDefaultAsync(d => d.StaffMemberId == staffMemberId && d.Id == dayOffId, ct);

    public void Add(WorkingHours hours) => db.WorkingHours.Add(hours);
    public void Add(StaffDayOff dayOff) => db.StaffDaysOff.Add(dayOff);
    public void Remove(StaffDayOff dayOff) => db.StaffDaysOff.Remove(dayOff);
}
