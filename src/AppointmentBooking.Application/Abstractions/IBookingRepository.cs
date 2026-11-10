using AppointmentBooking.Application.Bookings;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Booking?> GetByClientAndRequestIdAsync(Guid clientUserId, Guid requestId, CancellationToken ct);

// confirmed bookings overlapping [startUtc, endUtc) for one employee
    Task<IReadOnlyList<Booking>> GetConfirmedOverlappingAsync(Guid staffId, DateTime startUtc, DateTime endUtc,
        CancellationToken ct);

// all confirmed bookings starting at/after fromUtc (B uses this for schedule edits)
    Task<IReadOnlyList<Booking>> GetFutureConfirmedAsync(Guid staffId, DateTime fromUtc, CancellationToken ct);

    Task<IReadOnlyList<Booking>> GetByClientAsync(Guid clientUserId, CancellationToken ct);

// B/A need this for the "timezone locked after first booking" rule
    Task<bool> AnyBookingExistsAsync(CancellationToken ct);

    void Add(Booking booking); // stages only; never commits

    Task<BookingOffering?> GetBookingOfferingAsync(Guid staffServiceId, CancellationToken ct);
    
    Task<IReadOnlyList<BookingDto>> ListByClientAsync(Guid clientUserId, CancellationToken ct);
    
    Task<BookingDto?> Cancel(Guid clientUserId, Guid bookingId, CancellationToken ct);
    
    Task<BookingDto?> Complete(Guid staffMemberId, Guid bookingId, CancellationToken ct);
    
    Task<IReadOnlyList<BookingDto>> ListByStaffAndRangeAsync(
        Guid staffMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}