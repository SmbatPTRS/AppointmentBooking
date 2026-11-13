using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Staff;

public sealed class StaffManagementService(
    IAccessService access,
    IUserRepository users,
    IStaffRepository staff,
    IBookingCoordinationRepository coordination,
    IStaffOperationLock staffLock,
    IUnitOfWork unitOfWork)
{
    public async Task<StaffProfile> CreateAsync(
        Actor actor, Guid userId, string displayName, CancellationToken ct = default)
    {
        await access.RequireOwnerAsync(actor, ct);

        if (await users.GetByIdAsync(userId, ct) is null)
        {
            throw new AppException(ErrorCodes.UserNotFound, "User not found.", AppErrorKind.NotFound);
        }

        var member = new StaffMember(userId, ValidateName(displayName));
        await staff.CreateWithCoordinationAsync(member, ct);
        return StaffProfile.From(member);
    }

    public async Task<IReadOnlyList<StaffProfile>> ListAsync(
        Actor actor, bool includeInactive = false, CancellationToken ct = default)
    {
        await access.RequireOwnerAsync(actor, ct);
        var members = await staff.ListAsync(includeInactive, ct);
        return members.Select(StaffProfile.From).ToArray();
    }

    public async Task<IReadOnlyList<StaffListItem>> BrowseAsync(
        Actor actor, bool includeInactive = false, CancellationToken ct = default)
    {
        if (includeInactive)
        {
            await access.RequireOwnerAsync(actor, ct);
        }

        var members = await staff.ListAsync(includeInactive, ct);
        return members.Select(member => new StaffListItem(member.Id, member.DisplayName, member.IsActive)).ToArray();
    }

    public async Task<IReadOnlyList<StaffListItem>> BrowseForAppointmentsAsync(
        Actor actor, CancellationToken ct = default)
    {
        var isOwner = await access.IsOwnerAsync(actor, ct);
        var members = (await staff.ListAsync(isOwner, ct)).ToList();
        if (!isOwner)
        {
            var ownProfile = await staff.GetByUserIdAsync(actor.UserId, ct);
            if (ownProfile is { IsActive: false })
                members.Add(ownProfile);
        }

        return members
            .Select(member => new StaffListItem(member.Id, member.DisplayName, member.IsActive))
            .ToArray();
    }

    public async Task<StaffProfile> RenameAsync(
        Actor actor, Guid staffMemberId, string displayName, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffMemberId, ct);
        var member = await staff.GetByIdForUpdateAsync(staffMemberId, ct);
        await access.RequireStaffManagementAsync(actor, staffMemberId, ct);
        if (member is null)
        {
            throw StaffNotFound();
        }
        member.Rename(ValidateName(displayName));
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return StaffProfile.From(member);
    }

    public async Task<StaffProfile> SetActiveAsync(
        Actor actor, Guid staffMemberId, bool isActive, CancellationToken ct = default)
    {
        await access.RequireOwnerAsync(actor, ct);

        if (!isActive)
        {
            // Commit the submission before waiting for the staff lock held by a booking.
            var current = await staff.GetByIdSnapshotAsync(staffMemberId, ct)
                ?? throw StaffNotFound();

            if (!current.IsActive)
            {
                return StaffProfile.From(current);
            }

            await coordination.IncrementStaffVersionAsync(staffMemberId, ct);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffMemberId, ct);

        var member = await GetMemberAsync(staffMemberId, ct);
        if (isActive)
        {
            member.Activate();
        }
        else
        {
            member.Deactivate();
        }

        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return StaffProfile.From(member);
    }

    private async Task<StaffMember> GetMemberAsync(Guid id, CancellationToken ct) =>
        await staff.GetByIdForUpdateAsync(id, ct) ?? throw StaffNotFound();

    private static AppException StaffNotFound() =>
        new(ErrorCodes.StaffNotFound, "Staff member not found.", AppErrorKind.NotFound);

    private static string ValidateName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
        {
            throw new AppException(ErrorCodes.ValidationFailed,
                "Display name must contain between 1 and 200 characters.", AppErrorKind.Validation);
        }

        return displayName;
    }
}
