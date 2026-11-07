using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Transactions;

public sealed class PostgresStaffOperationLock(AppDbContext db) : IStaffOperationLock
{
    /// <summary>
    /// Acquires a transaction-scoped PostgreSQL advisory lock for the given employee.
    /// Concurrent operations that lock the same employee run one at a time; the lock
    /// is released automatically on commit or rollback. Must be called inside an
    /// active transaction on the same connection, before reading data that the
    /// operation depends on.
    /// </summary>
    /// <param name="staffMemberId">The employee whose calendar is being protected.</param>
    /// <param name="ct">Cancellation token.</param>
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