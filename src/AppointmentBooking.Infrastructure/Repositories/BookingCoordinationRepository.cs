using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class BookingCoordinationRepository(AppDbContext context) : IBookingCoordinationRepository
{
    public Task IncrementStaffVersionAsync(Guid staffMemberId, CancellationToken ct) =>
        IncrementAsync(context.BookingCoordinationRows.Where(row => row.StaffMemberId == staffMemberId),
            new InvalidOperationException("The staff coordination row is missing."), ct);

    public Task IncrementBusinessVersionAsync(CancellationToken ct) =>
        IncrementAsync(context.BookingCoordinationRows.Where(row => row.BusinessSettingsId == BusinessSettings.SingletonId),
            MissingBusinessScope(), ct);

    public async Task LockBusinessAsync(CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The business coordination lock requires an active transaction.");
        }

        var rows = await context.Database.SqlQuery<long>($"""
            SELECT id FROM booking_coordination
            WHERE business_settings_id = {BusinessSettings.SingletonId}
            FOR UPDATE
            """).ToListAsync(ct);
        if (rows.Count != 1)
        {
            throw MissingBusinessScope();
        }
    }

    private async Task IncrementAsync(
        IQueryable<BookingCoordination> scope, Exception missingScope, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("The version increment must commit independently.");
        }

        var updated = await scope.ExecuteUpdateAsync(setters => setters.SetProperty(
                row => row.ChangeVersion, row => row.ChangeVersion + 1), ct);

        if (updated != 1)
        {
            throw missingScope;
        }
    }
    private static AppException MissingBusinessScope() =>
        new(ErrorCodes.BusinessSettingsNotFound, "Business settings coordination has not been initialized.",
            AppErrorKind.NotFound);
}
