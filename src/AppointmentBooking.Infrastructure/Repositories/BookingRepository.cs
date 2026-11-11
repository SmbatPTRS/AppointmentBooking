using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class BookingRepository(AppDbContext context, IClock clock) : IBookingRepository
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

    public async Task<BookingOffering?> GetBookingOfferingAsync(Guid staffServiceId, CancellationToken ct)
    {
        var query = from offering in context.StaffServices
                    join service in context.Services on offering.ServiceId equals service.Id
                    join staff in context.StaffMembers on offering.StaffMemberId equals staff.Id
                    where offering.Id == staffServiceId
                    select new BookingOffering(
                        offering.Id,
                        staff.Id,
                        offering.Price,
                        offering.DurationMinutes,
                        offering.IsActive,
                        staff.IsActive,
                        service.IsActive);
        return await query.SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<BookingDto>> ListByClientAsync(
        Guid clientUserId,
        CancellationToken ct)
    {
        return await (
            from booking in context.Bookings
            join offering in context.StaffServices
                on booking.StaffServiceId equals offering.Id
            join service in context.Services
                on offering.ServiceId equals service.Id
            where booking.ClientUserId == clientUserId
            select new BookingDto(
                booking.Id,
                booking.StaffServiceId,
                service.Name,
                booking.Price,
                booking.DurationMinutes,
                booking.StartsAtUtc,
                booking.EndsAtUtc,
                booking.Status)
        ).ToListAsync(ct);
    }
    
    public async Task<IReadOnlyList<BookingDto>> ListByStaffAndRangeAsync(
        Guid staffMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        return await (
            from booking in context.Bookings
            join offering in context.StaffServices
                on booking.StaffServiceId equals offering.Id
            join service in context.Services
                on offering.ServiceId equals service.Id
            where booking.StaffMemberId == staffMemberId &&
                  booking.StartsAtUtc >= fromUtc &&
                  booking.StartsAtUtc < toUtc
            select new BookingDto(
                booking.Id,
                booking.StaffServiceId,
                service.Name,
                booking.Price,
                booking.DurationMinutes,
                booking.StartsAtUtc,
                booking.EndsAtUtc,
                booking.Status)
        ).ToListAsync(ct);
    }

    public async Task<BookingDto?> Cancel(
        Guid clientUserId,
        Guid bookingId,
        CancellationToken ct)
    {
        var booking = await context.Bookings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                b => b.Id == bookingId && b.ClientUserId == clientUserId,
                ct);

        if (booking is null)
            return null;

        if (booking.Status == BookingStatus.Confirmed)
        {
            var cancelledAtUtc = clock.UtcNow;
            if (cancelledAtUtc >= booking.StartsAtUtc)
                throw new InvalidOperationException(
                    "A booking can only be cancelled before its start.");

            await context.Bookings
                .Where(b => b.Id == bookingId &&
                            b.ClientUserId == clientUserId &&
                            b.Status == BookingStatus.Confirmed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(b => b.Status, BookingStatus.Cancelled)
                    .SetProperty(b => b.CancelledAtUtc, cancelledAtUtc), ct);
        }
        else if (booking.Status != BookingStatus.Cancelled)
        {
            throw new InvalidOperationException("Only a confirmed booking can be cancelled.");
        }

        return await GetBookingDtoAsync(bookingId, ct);
    }
    
    public async Task<BookingDto?> Complete(
        Guid staffMemberId,
        Guid bookingId,
        CancellationToken ct)
    {
        var booking = await context.Bookings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                b => b.Id == bookingId && b.StaffMemberId == staffMemberId,
                ct);

        if (booking is null)
            return null;

        if (booking.Status == BookingStatus.Confirmed)
        {
            var completedAtUtc = clock.UtcNow;
            if (completedAtUtc < booking.EndsAtUtc)
                throw new InvalidOperationException(
                    "A booking can only be completed at or after its end.");

            await context.Bookings
                .Where(b => b.Id == bookingId &&
                            b.StaffMemberId == staffMemberId &&
                            b.Status == BookingStatus.Confirmed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(b => b.Status, BookingStatus.Completed)
                    .SetProperty(b => b.CompletedAtUtc, completedAtUtc), ct);
        }
        else if (booking.Status != BookingStatus.Completed)
        {
            throw new InvalidOperationException("Only a confirmed booking can be completed.");
        }

        return await GetBookingDtoAsync(bookingId, ct);
    }

    private Task<BookingDto?> GetBookingDtoAsync(Guid bookingId, CancellationToken ct)
    {
        return (
            from booking in context.Bookings
            join offering in context.StaffServices
                on booking.StaffServiceId equals offering.Id
            join service in context.Services
                on offering.ServiceId equals service.Id
            where booking.Id == bookingId
            select new BookingDto(
                booking.Id,
                booking.StaffServiceId,
                service.Name,
                booking.Price,
                booking.DurationMinutes,
                booking.StartsAtUtc,
                booking.EndsAtUtc,
                booking.Status)
        ).SingleOrDefaultAsync(ct);
    }
}
