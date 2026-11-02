using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class BusinessSettingsRepository(AppDbContext context)
    : IBusinessSettingsRepository
{
    public Task<BusinessSettings?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        return context.BusinessSettings.SingleOrDefaultAsync(
            settings => settings.Id == BusinessSettings.SingletonId,
            cancellationToken);
    }

    public void Add(BusinessSettings settings)
    {
        context.BusinessSettings.Add(settings);
    }
}