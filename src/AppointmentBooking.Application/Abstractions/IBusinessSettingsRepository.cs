using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface IBusinessSettingsRepository
{
    Task<BusinessSettings?> GetAsync(
        CancellationToken cancellationToken = default);

    void Add(BusinessSettings settings);
}