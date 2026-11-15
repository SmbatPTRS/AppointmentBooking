using System.ComponentModel.DataAnnotations;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Catalog;

public sealed class CatalogService(ICatalogRepository catalog, IAccessService access, IUnitOfWork unitOfWork)
{
    /// <summary>Customer catalogue; owner privileges are not required.</summary>
    public Task<IReadOnlyList<ServiceDto>> ListActiveServicesAsync(CancellationToken ct) =>
        catalog.ListActiveServicesAsync(ct);

    /// <summary>
    /// Customer offerings with employee-specific price and duration. An unknown or
    /// inactive employee has no visible offerings. This is not final booking validation.
    /// </summary>
    public Task<IReadOnlyList<StaffServiceDto>> ListActiveOfferingsAsync(Guid staffId, CancellationToken ct) =>
        catalog.ListActiveOfferingsAsync(staffId, ct);

    public async Task<IReadOnlyList<ServiceDto>> ListServicesForOwnerAsync(Actor actor, CancellationToken ct)
    {
        await access.RequireOwnerAsync(actor, ct);
        return await catalog.ListServicesAsync(ct);
    }

    public async Task<ServiceDto> RenameServiceAsync(Actor actor, Guid serviceId, CreateServiceRequest request,
        CancellationToken ct)
    {
        await access.RequireOwnerAsync(actor, ct);
        ValidateName(request);
        var service = await catalog.GetServiceAsync(serviceId, ct)
            ?? throw new AppException(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);

        service.Rename(request.Name);
        await unitOfWork.SaveChangesAsync(ct);
        return new ServiceDto(service.Id, service.Name, service.IsActive);
    }

    public async Task<ServiceDto> CreateServiceAsync(Actor actor, CreateServiceRequest request, CancellationToken ct)
    {
        await access.RequireOwnerAsync(actor, ct);
        ValidateName(request);

        var service = new Service(request.Name);
        catalog.Add(service);
        await unitOfWork.SaveChangesAsync(ct);
        return new ServiceDto(service.Id, service.Name, service.IsActive);
    }

    private static void ValidateName(CreateServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { CatalogRules.Validate(request); }
        catch (ValidationException ex)
        {
            throw new AppException(ErrorCodes.ValidationFailed, ex.Message, AppErrorKind.Validation);
        }
    }
}
