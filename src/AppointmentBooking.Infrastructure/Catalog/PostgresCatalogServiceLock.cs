using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Catalog;

/// <summary>Serializes shared-service status changes and creation of employee links.</summary>
public sealed class PostgresCatalogServiceLock(AppDbContext db) : ICatalogServiceLock
{
    public async Task AcquireAsync(Guid serviceId, CancellationToken ct)
    {
        if (serviceId == Guid.Empty) throw new ArgumentException("A service ID is required.", nameof(serviceId));
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The catalogue service lock requires an active transaction.");
        // Separate key space from C's employee keys. PostgreSQL releases it with the transaction.
        var key = "catalog-service:" + serviceId.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0::bigint))", ct);
    }
}
