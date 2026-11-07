using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    public async Task<StaffMember?> GetByIdForUpdateAsync(Guid staffMemberId, CancellationToken ct)
    {
        var tracked = context.StaffMembers.Local.SingleOrDefault(staff => staff.Id == staffMemberId);
        if (tracked is null)
        {
            return await GetByIdAsync(staffMemberId, ct);
        }

        var entry = context.Entry(tracked);
        await entry.ReloadAsync(ct);
        return entry.State == EntityState.Detached ? null : tracked;
    }

    public Task<StaffMember?> GetByIdSnapshotAsync(Guid staffMemberId, CancellationToken ct) =>
        context.StaffMembers.AsNoTracking().SingleOrDefaultAsync(staff => staff.Id == staffMemberId, ct);

    public async Task CreateWithCoordinationAsync(StaffMember staffMember, CancellationToken ct)
    {
        if (await context.StaffMembers.AnyAsync(staff => staff.UserId == staffMember.UserId, ct))
        {
            throw DuplicateProfile();
        }

        var coordination = new BookingCoordination(staffMember);
        context.StaffMembers.Add(staffMember);
        context.BookingCoordinationRows.Add(coordination);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_staff_members_user_id" })
        {
            context.Entry(coordination).State = EntityState.Detached;
            context.Entry(staffMember).State = EntityState.Detached;
            throw DuplicateProfile();
        }
    }

    private static AppException DuplicateProfile() =>
        new(ErrorCodes.StaffAlreadyExists, "This user already has a staff profile.", AppErrorKind.Conflict);

    public void Add(StaffMember staffMember)
    {
        context.StaffMembers.Add(staffMember);
    }
}
