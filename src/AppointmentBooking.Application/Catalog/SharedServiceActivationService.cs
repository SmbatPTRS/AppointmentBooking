using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using Microsoft.Extensions.DependencyInjection;

namespace AppointmentBooking.Application.Catalog;

/// <summary>Owner-only shared eligibility changes with stable membership and ordered staff locks.</summary>
public sealed class SharedServiceActivationService(ICatalogRepository catalog, IAccessService access,
    IUnitOfWork unitOfWork, IStaffOperationLock staffLock, IServiceScopeFactory scopes, ICatalogServiceLock catalogLock)
{
    public async Task<ServiceDto> SetActiveAsync(Actor actor, Guid serviceId, bool isActive, CancellationToken ct)
    {
        await access.RequireOwnerAsync(actor, ct);
        if (serviceId == Guid.Empty) throw MissingService();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        await catalogLock.AcquireAsync(serviceId, ct);
        await access.RequireOwnerAsync(actor, ct);
        var service = await catalog.GetServiceForUpdateAsync(serviceId, ct) ?? throw MissingService();
        if (service.IsActive == isActive) return new(service.Id, service.Name, service.IsActive);

        // Holding the service gate prevents new links from escaping this set. Include
        // inactive staff/offerings: their individual activation can change while we wait.
        var staffIds = (await catalog.ListStaffIdsForServiceAsync(serviceId, ct)).OrderBy(id => id).ToArray();
        if (!isActive && staffIds.Length != 0)
        {
            // Version publication must commit outside the mutation transaction, as in
            // A's staff deactivation. Use another context while retaining the service gate.
            await using var publication = scopes.CreateAsyncScope();
            var coordination = publication.ServiceProvider.GetRequiredService<IBookingCoordinationRepository>();
            foreach (var staffId in staffIds) await coordination.IncrementStaffVersionAsync(staffId, ct);
        }
        // Complete every publication before taking any employee lock. The same order
        // across service mutations avoids cycles when two services share employees.
        foreach (var staffId in staffIds) await staffLock.AcquireAsync(staffId, ct);
        await access.RequireOwnerAsync(actor, ct);
        service = await catalog.GetServiceForUpdateAsync(serviceId, ct) ?? throw MissingService();
        if (isActive) service.Activate();
        else service.Deactivate();
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(service.Id, service.Name, service.IsActive);
    }

    private static AppException MissingService() =>
        new(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);
}
