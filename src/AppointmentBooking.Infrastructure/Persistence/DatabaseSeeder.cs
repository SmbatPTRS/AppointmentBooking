using AppointmentBooking.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Persistence;

public sealed class DatabaseSeeder(AppDbContext context)
{
    public async Task SeedBusinessSettingsAsync(BusinessSettings defaults, CancellationToken ct = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO business_settings (id, display_name, staff_label, time_zone_id, currency_code)
            VALUES ({defaults.Id}, {defaults.DisplayName}, {defaults.StaffLabel},
                    {defaults.TimeZoneId}, {defaults.CurrencyCode})
            ON CONFLICT (id) DO NOTHING
            """, ct);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO booking_coordination (business_settings_id)
            VALUES ({defaults.Id})
            ON CONFLICT (business_settings_id) DO NOTHING
            """, ct);

        await transaction.CommitAsync(ct);
    }
}
