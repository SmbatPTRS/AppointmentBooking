using System.ComponentModel.DataAnnotations;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Catalog;

public sealed class CatalogService(ICatalogRepository catalog, IAccessService access, IUnitOfWork unitOfWork)
{
    public async Task<ServiceDto> CreateServiceAsync(Actor actor, CreateServiceRequest request, CancellationToken ct)
    {
        await access.RequireOwnerAsync(actor, ct);
        ArgumentNullException.ThrowIfNull(request);
        try { CatalogRules.Validate(request); }
        catch (ValidationException ex)
        {
            throw new AppException(ErrorCodes.ValidationFailed, ex.Message, AppErrorKind.Validation);
        }

        var service = new Service(request.Name);
        catalog.Add(service);
        await unitOfWork.SaveChangesAsync(ct);
        return new ServiceDto(service.Id, service.Name, service.IsActive);
    }
}
