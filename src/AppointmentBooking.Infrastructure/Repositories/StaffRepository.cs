using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class StaffRepository(AppDbContext context)
    : IStaffRepository
{
    public Task<StaffMember?> GetByIdAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken = default)
    {
        return context.StaffMembers.SingleOrDefaultAsync(
            staff => staff.Id == staffMemberId,
            cancellationToken);
    }

    public Task<StaffMember?> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return context.StaffMembers.SingleOrDefaultAsync(
            staff => staff.UserId == userId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<StaffMember>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<StaffMember> query = context.StaffMembers;

        if (!includeInactive)
        {
            query = query.Where(staff => staff.IsActive);
        }

        return await query
            .OrderBy(staff => staff.DisplayName)
            .ThenBy(staff => staff.Id)
            .ToListAsync(cancellationToken);
    }

    public void Add(StaffMember staffMember)
    {
        context.StaffMembers.Add(staffMember);
    }
}