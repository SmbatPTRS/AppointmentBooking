using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Identity;

public sealed class AccessService(
    IUserRepository users,
    IStaffRepository staff,
    long ownerTelegramUserId) : IAccessService
{
    public async Task<bool> IsOwnerAsync(Actor actor, CancellationToken ct) =>
        (await GetCallerAsync(actor, ct)).TelegramUserId == ownerTelegramUserId;

    public async Task RequireOwnerAsync(Actor actor, CancellationToken ct)
    {
        User user = await GetCallerAsync(actor, ct);
        if (user.TelegramUserId != ownerTelegramUserId)
        {
            throw new AppException(ErrorCodes.Forbidden, "Owner access is required.", AppErrorKind.Forbidden);
        }
    }

    public Task RequireStaffManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
        RequireStaffAccessAsync(actor, staffMemberId, requireActive: true, ct);

    public Task RequireAppointmentManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct) =>
        RequireStaffAccessAsync(actor, staffMemberId, requireActive: false, ct);

    private async Task RequireStaffAccessAsync(
        Actor actor,
        Guid staffMemberId,
        bool requireActive,
        CancellationToken ct)
    {
        User user = await GetCallerAsync(actor, ct);
        StaffMember? profile = await staff.GetByIdAsync(staffMemberId, ct);
        if (profile is null)
        {
            throw new AppException(ErrorCodes.StaffNotFound, "Staff member was not found.", AppErrorKind.NotFound);
        }

        if (user.TelegramUserId == ownerTelegramUserId)
        {
            return;
        }

        if (profile.UserId != user.Id)
        {
            throw new AppException(ErrorCodes.Forbidden, "Access to this staff member is forbidden.", AppErrorKind.Forbidden);
        }

        if (requireActive && !profile.IsActive)
        {
            throw new AppException(ErrorCodes.StaffInactive, "The staff member is inactive.", AppErrorKind.Forbidden);
        }
    }

    private async Task<User> GetCallerAsync(Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return await users.GetByIdAsync(actor.UserId, ct)
            ?? throw new AppException(ErrorCodes.Unauthorized, "The caller was not found.", AppErrorKind.Unauthorized);
    }
}
