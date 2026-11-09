using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface IBusinessSettingsRepository
{
    Task<BusinessSettings?> GetAsync(
        CancellationToken cancellationToken = default);

    Task<BusinessSettings?> GetForUpdateAsync(CancellationToken ct);

    void Add(BusinessSettings settings);
}
