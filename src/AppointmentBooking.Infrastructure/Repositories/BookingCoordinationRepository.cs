using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class BookingCoordinationRepository(AppDbContext context) : IBookingCoordinationRepository
{
    public async Task IncrementStaffVersionAsync(Guid staffMemberId, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("The version increment must commit independently.");
        }

        var updated = await context.BookingCoordinationRows
            .Where(row => row.StaffMemberId == staffMemberId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                row => row.ChangeVersion, row => row.ChangeVersion + 1), ct);

        if (updated != 1)
        {
            throw new InvalidOperationException("The staff coordination row is missing.");
        }
    }
}
