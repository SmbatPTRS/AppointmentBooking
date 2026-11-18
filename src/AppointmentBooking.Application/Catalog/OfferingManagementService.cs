using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Application.Catalog;

/// <summary>Management reads include disabled rows; public booking eligibility is a separate concern.</summary>
public sealed class OfferingManagementService(ICatalogRepository catalog, IAccessService access, IStaffRepository staff)
{
    public async Task<IReadOnlyList<CatalogStaffDto>> ListStaffAsync(Actor actor, CancellationToken ct)
    {
        if (await access.IsOwnerAsync(actor, ct))
            return (await staff.ListAsync(includeInactive: true, cancellationToken: ct))
                .Select(s => new CatalogStaffDto(s.Id, s.DisplayName, s.IsActive))
                .OrderBy(s => s.DisplayName, StringComparer.Ordinal).ThenBy(s => s.Id).ToArray();

        var own = await staff.GetByUserIdAsync(actor.UserId, ct)
            ?? throw new AppException(ErrorCodes.Forbidden, "Staff access is required.", AppErrorKind.Forbidden);
        await access.RequireStaffManagementAsync(actor, own.Id, ct);
        return [new CatalogStaffDto(own.Id, own.DisplayName, own.IsActive)];
    }

    public async Task<IReadOnlyList<ManagedOfferingDto>> ListAsync(Actor actor, Guid staffId, CancellationToken ct)
    {
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        return await catalog.ListManagedOfferingsAsync(staffId, ct);
    }
}

public sealed record CatalogStaffDto(Guid Id, string DisplayName, bool IsActive);

// All three flags are independent. They are not a promise that a booking slot is available.
public sealed record ManagedOfferingDto(Guid Id, Guid StaffMemberId, Guid ServiceId,
    string ServiceName, decimal Price, int DurationMinutes, bool IsActive,
    bool ServiceIsActive, bool StaffIsActive);
