using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class CatalogRepository(AppDbContext db) : ICatalogRepository
{
    public async Task<IReadOnlyList<ServiceDto>> ListActiveServicesAsync(CancellationToken ct) =>
        await db.Services.Where(s => s.IsActive)
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Select(s => new ServiceDto(s.Id, s.Name, s.IsActive))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StaffServiceDto>> ListActiveOfferingsAsync(Guid staffMemberId, CancellationToken ct)
    {
        var query = from offering in db.StaffServices
                    join service in db.Services on offering.ServiceId equals service.Id
                    join staff in db.StaffMembers on offering.StaffMemberId equals staff.Id
                    where staff.Id == staffMemberId && staff.IsActive && service.IsActive && offering.IsActive
                    orderby service.Name, offering.Id
                    select new StaffServiceDto(offering.Id, staff.Id, service.Id, service.Name,
                        offering.Price, offering.DurationMinutes, offering.IsActive);
        return await query.ToListAsync(ct);
    }

    public Task<Service?> GetServiceAsync(Guid serviceId, CancellationToken ct) =>
        db.Services.SingleOrDefaultAsync(s => s.Id == serviceId, ct);

    public Task<StaffService?> GetOfferingAsync(Guid staffMemberId, Guid serviceId, CancellationToken ct) =>
        db.StaffServices.SingleOrDefaultAsync(o => o.StaffMemberId == staffMemberId && o.ServiceId == serviceId, ct);

    public Task<StaffService?> GetOfferingByIdAsync(Guid offeringId, CancellationToken ct) =>
        db.StaffServices.AsNoTracking().SingleOrDefaultAsync(o => o.Id == offeringId, ct);

    public void Add(Service service) => db.Services.Add(service);
    public void Add(StaffService offering) => db.StaffServices.Add(offering);
}
