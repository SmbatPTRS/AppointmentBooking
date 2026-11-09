namespace AppointmentBooking.Application.Abstractions;

public readonly record struct BookingCoordinationVersions(long BusinessVersion, long StaffVersion);

public interface IBookingCoordinationRepository
{
    Task<BookingCoordinationVersions> ReadVersionsAsync(Guid staffMemberId, CancellationToken ct);

    // Requires the booking transaction and locks business before staff until transaction end.
    Task<BookingCoordinationVersions> LockAndReadVersionsAsync(Guid staffMemberId, CancellationToken ct);

    Task IncrementBusinessVersionAsync(CancellationToken ct);
    Task LockBusinessAsync(CancellationToken ct);
    Task IncrementStaffVersionAsync(Guid staffMemberId, CancellationToken ct);
}
