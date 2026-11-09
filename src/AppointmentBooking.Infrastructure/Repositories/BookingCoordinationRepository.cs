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
            MissingStaffScope(), ct);

    public Task IncrementBusinessVersionAsync(CancellationToken ct) =>
        IncrementAsync(context.BookingCoordinationRows.Where(row => row.BusinessSettingsId == BusinessSettings.SingletonId),
            MissingBusinessScope(), ct);

    public async Task<BookingCoordinationVersions> ReadVersionsAsync(Guid staffMemberId, CancellationToken ct)
    {
        var rows = await context.BookingCoordinationRows
            .Where(row => row.BusinessSettingsId == BusinessSettings.SingletonId || row.StaffMemberId == staffMemberId)
            .Select(row => new { row.BusinessSettingsId, row.StaffMemberId, row.ChangeVersion })
            .ToListAsync(ct);

        var business = rows.SingleOrDefault(row => row.BusinessSettingsId == BusinessSettings.SingletonId)
            ?? throw MissingBusinessScope();
        var staff = rows.SingleOrDefault(row => row.StaffMemberId == staffMemberId)
            ?? throw MissingStaffScope();
        return new BookingCoordinationVersions(business.ChangeVersion, staff.ChangeVersion);
    }

    public async Task<BookingCoordinationVersions> LockAndReadVersionsAsync(Guid staffMemberId, CancellationToken ct)
    {
        var businessVersion = await LockAndReadBusinessVersionAsync(ct);
        var rows = await context.Database.SqlQuery<long>($"""
            SELECT change_version FROM booking_coordination
            WHERE staff_member_id = {staffMemberId}
            FOR UPDATE
            """).ToListAsync(ct);
        if (rows.Count != 1)
        {
            throw MissingStaffScope();
        }

        return new BookingCoordinationVersions(businessVersion, rows[0]);
    }

    public async Task LockBusinessAsync(CancellationToken ct)
    {
        await LockAndReadBusinessVersionAsync(ct);
    }

    private async Task<long> LockAndReadBusinessVersionAsync(CancellationToken ct)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The business coordination lock requires an active transaction.");
        }

        var rows = await context.Database.SqlQuery<long>($"""
            SELECT change_version FROM booking_coordination
            WHERE business_settings_id = {BusinessSettings.SingletonId}
            FOR UPDATE
            """).ToListAsync(ct);
        if (rows.Count != 1)
        {
            throw MissingBusinessScope();
        }

        return rows[0];
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

    private static InvalidOperationException MissingStaffScope() =>
        new("The staff coordination row is missing.");

    private static AppException MissingBusinessScope() =>
        new(ErrorCodes.BusinessSettingsNotFound, "Business settings coordination has not been initialized.",
            AppErrorKind.NotFound);
}
