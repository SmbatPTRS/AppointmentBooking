using System.ComponentModel.DataAnnotations;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Catalog;

/// <summary>Employee-scoped catalogue writes serialized with booking confirmation.</summary>
public sealed class OfferingTermsService(ICatalogRepository catalog, IAccessService access,
    IStaffRepository staff, IUnitOfWork unitOfWork, IStaffOperationLock staffLock)
{
    public const string DuplicateOfferingCode = "staff_service.already_exists";

    public Task<StaffServiceDto> CreateAsync(Actor actor, Guid staffId, Guid serviceId,
        OfferingTermsRequest request, CancellationToken ct) =>
        SaveAsync(actor, staffId, serviceId, request, create: true, ct);

    public Task<StaffServiceDto> UpdateAsync(Actor actor, Guid staffId, Guid serviceId,
        OfferingTermsRequest request, CancellationToken ct) =>
        SaveAsync(actor, staffId, serviceId, request, create: false, ct);

    private async Task<StaffServiceDto> SaveAsync(Actor actor, Guid staffId, Guid serviceId,
        OfferingTermsRequest request, bool create, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (staffId == Guid.Empty)
        {
            // The shared lock rejects empty IDs; preserve A's identity/error contract first.
            await access.RequireStaffManagementAsync(actor, staffId, ct);
            throw new AppException(ErrorCodes.StaffNotFound, "Staff member not found.", AppErrorKind.NotFound);
        }
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await staffLock.AcquireAsync(staffId, ct);
        // Adapters may have loaded the profile earlier. Refresh it after waiting,
        // before A's access check can see a stale active flag in the shared context.
        await staff.GetByIdForUpdateAsync(staffId, ct);
        await access.RequireStaffManagementAsync(actor, staffId, ct);
        Validate(request);
        var service = await catalog.GetServiceAsync(serviceId, ct)
            ?? throw new AppException(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);
        var offering = await catalog.GetOfferingForUpdateAsync(staffId, serviceId, ct);
        if (create)
        {
            if (offering is not null)
                throw new AppException(DuplicateOfferingCode,
                    "This employee already has an offering for the service.", AppErrorKind.Conflict);
            offering = new StaffService(staffId, serviceId, request.Price, request.DurationMinutes);
            catalog.Add(offering);
        }
        else
        {
            if (offering is null)
                throw new AppException(ErrorCodes.StaffServiceNotFound, "Offering not found.", AppErrorKind.NotFound);
            // Update only the terms. Disabled offerings remain disabled; accepted
            // bookings retain their own price, duration and interval snapshots.
            offering.UpdateTerms(request.Price, request.DurationMinutes);
        }
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(offering.Id, offering.StaffMemberId, offering.ServiceId, service.Name,
            offering.Price, offering.DurationMinutes, offering.IsActive);
    }

    private static void Validate(OfferingTermsRequest request)
    {
        try
        {
            CatalogRules.Validate(new ConfigureStaffServiceRequest(request.Price, request.DurationMinutes, true));
        }
        catch (ValidationException error)
        {
            throw new AppException(ErrorCodes.ValidationFailed, error.Message, AppErrorKind.Validation);
        }
    }
}

// Activation changes have a separate coordination policy; they are not part of a terms edit.
public sealed record OfferingTermsRequest(decimal Price, int DurationMinutes);
