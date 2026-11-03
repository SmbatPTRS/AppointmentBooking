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

// for staff/day lists (Task 15)
    Task<IReadOnlyList<Booking>> GetByStaffAndRangeAsync(Guid staffId, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct);

    Task<IReadOnlyList<Booking>> GetByClientAsync(Guid clientUserId, CancellationToken ct);

// B/A need this for the "timezone locked after first booking" rule
    Task<bool> AnyBookingExistsAsync(CancellationToken ct);

    void Add(Booking booking); // stages only; never commits
}