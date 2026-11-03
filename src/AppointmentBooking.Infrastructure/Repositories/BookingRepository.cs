using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class BookingRepository(AppDbContext context) : IBookingRepository
{
    public Task<Booking?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return context.Bookings.SingleOrDefaultAsync(b => b.Id == id, ct);
    }

    public Task<Booking?> GetByClientAndRequestIdAsync(Guid clientUserId, Guid requestId, CancellationToken ct)
    {
        return context.Bookings
            .SingleOrDefaultAsync(
                b =>
                    b.ClientUserId == clientUserId &&
                    b.RequestId == requestId,
                ct);
    }
    
    public async Task<IReadOnlyList<Booking>> GetConfirmedOverlappingAsync(Guid staffId, DateTime startUtc, DateTime endUtc,
        CancellationToken ct)
    {
        return await context.Bookings
            .Where(b =>
                b.StaffMemberId == staffId &&
                b.Status == BookingStatus.Confirmed &&
                b.StartsAtUtc < endUtc &&
                b.EndsAtUtc > startUtc)
            .ToListAsync(ct);
    }
    
    public async Task<IReadOnlyList<Booking>> GetFutureConfirmedAsync(Guid staffId, DateTime fromUtc, CancellationToken ct)
    {
        return await context.Bookings
            .Where(b =>
                b.StaffMemberId == staffId &&
                b.Status == BookingStatus.Confirmed &&
                b.StartsAtUtc >= fromUtc)
            .ToListAsync(ct);
    }
    
    public async Task<IReadOnlyList<Booking>> GetByStaffAndRangeAsync(Guid staffId, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        return await context.Bookings
            .Where(b =>
                b.StaffMemberId == staffId &&
                b.StartsAtUtc >= fromUtc &&
                b.StartsAtUtc < toUtc)
            .ToListAsync(ct);
    }
    
    public async Task<IReadOnlyList<Booking>> GetByClientAsync(Guid clientUserId, CancellationToken ct)
    {
        return await context.Bookings
            .Where(b => b.ClientUserId == clientUserId)
            .ToListAsync(ct);
    }
    
    public async Task<bool> AnyBookingExistsAsync(CancellationToken ct)
    {
        return await context.Bookings.AnyAsync(ct);
    }
    
    public void Add(Booking booking)
    {
        context.Bookings.Add(booking);
    }
}

