using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface ICatalogRepository
{
    Task<IReadOnlyList<ServiceDto>> ListActiveServicesAsync(CancellationToken ct);
    Task<IReadOnlyList<StaffServiceDto>> ListActiveOfferingsAsync(Guid staffMemberId, CancellationToken ct);
    Task<Service?> GetServiceAsync(Guid serviceId, CancellationToken ct);
    Task<StaffService?> GetOfferingAsync(Guid staffMemberId, Guid serviceId, CancellationToken ct);
    // Retrives a StaffService by its unique identifier. Returns null if not found.
    Task<StaffService?> GetOfferingByIdAsync(Guid offeringId, CancellationToken ct);
    void Add(Service service);
    void Add(StaffService offering);
}
