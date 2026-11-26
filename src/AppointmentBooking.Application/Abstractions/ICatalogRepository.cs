using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface ICatalogRepository
{
    Task<IReadOnlyList<ServiceDto>> ListServicesAsync(CancellationToken ct);
    Task<IReadOnlyList<ServiceDto>> ListActiveServicesAsync(CancellationToken ct);
    Task<IReadOnlyList<StaffServiceDto>> ListActiveOfferingsAsync(Guid staffMemberId, CancellationToken ct);
    Task<IReadOnlyList<ManagedOfferingDto>> ListManagedOfferingsAsync(Guid staffMemberId, CancellationToken ct);
    Task<Service?> GetServiceAsync(Guid serviceId, CancellationToken ct);
    Task<Service?> GetServiceSnapshotAsync(Guid serviceId, CancellationToken ct);
    Task<StaffService?> GetOfferingAsync(Guid staffMemberId, Guid serviceId, CancellationToken ct);
    // Reloads a previously tracked offering after the caller acquires the staff lock.
    Task<StaffService?> GetOfferingForUpdateAsync(Guid staffMemberId, Guid serviceId, CancellationToken ct);
    Task<StaffService?> GetOfferingByIdAsync(Guid offeringId, CancellationToken ct);
    void Add(Service service);
    void Add(StaffService offering);
}
