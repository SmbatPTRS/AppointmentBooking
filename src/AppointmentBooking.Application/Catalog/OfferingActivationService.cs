using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Application.Catalog;

/// <summary>Offering eligibility changes using A's staff-version publication and C's staff lock.</summary>
public sealed class OfferingActivationService(ICatalogRepository catalog, IAccessService access,
    IStaffRepository staff, IBookingCoordinationRepository coordination, IUnitOfWork unitOfWork, IStaffOperationLock staffLock)
{
    public async Task<StaffServiceDto> SetActiveAsync(Actor actor, Guid staffId, Guid serviceId, bool isActive, CancellationToken ct)
    {
        // Check current permissions before publishing an independently committed invalidation.
        // A previous adapter read must not let a cached active profile bypass this check.
        await staff.GetByIdForUpdateAsync(staffId, ct);
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        var service = await catalog.GetServiceSnapshotAsync(serviceId, ct)
            ?? throw new AppException(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);
        var snapshot = await catalog.GetOfferingSnapshotAsync(staffId, serviceId, ct)
            ?? throw MissingOffering();
        if (!isActive)
        {
            if (!snapshot.IsActive)
                return new(snapshot.Id, staffId, serviceId, service.Name, snapshot.Price, snapshot.DurationMinutes, false);
            // Like staff deactivation, publish before waiting for the employee lock.
            // Publication survives cancellation or a later mutation failure intentionally.
            await coordination.IncrementStaffVersionAsync(staffId, ct);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffId, ct);
        await staff.GetByIdForUpdateAsync(staffId, ct);
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        var offering = await catalog.GetOfferingForUpdateAsync(staffId, serviceId, ct)
            ?? throw MissingOffering();
        service = await catalog.GetServiceSnapshotAsync(serviceId, ct)
            ?? throw new AppException(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);
        if (isActive)
        {
            if (!service.IsActive)
                throw new AppException(ErrorCodes.ServiceInactive,
                    "This shared service is inactive. Activate the shared service first.", AppErrorKind.Conflict);
            offering.Activate();
        }
        else offering.Deactivate();
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(offering.Id, staffId, serviceId, service.Name, offering.Price, offering.DurationMinutes, offering.IsActive);
    }

    private static AppException MissingOffering() =>
        new(ErrorCodes.StaffServiceNotFound, "Offering not found.", AppErrorKind.NotFound);
}
