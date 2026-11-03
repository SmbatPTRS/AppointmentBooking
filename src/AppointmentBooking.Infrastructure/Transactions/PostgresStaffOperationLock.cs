using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Transactions;

public sealed class PostgresStaffOperationLock(AppDbContext db) : IStaffOperationLock
{
    public async Task AcquireAsync(Guid staffMemberId, CancellationToken ct)
    {
        
        if (staffMemberId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staff member ID is required.",
                nameof(staffMemberId));
        }
        
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "The employee lock requires an active transaction.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({staffMemberId.ToString()}, 0::bigint))",
            ct);
    }
}