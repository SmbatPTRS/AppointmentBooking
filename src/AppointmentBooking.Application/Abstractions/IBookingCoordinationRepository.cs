namespace AppointmentBooking.Application.Abstractions;

public interface IBookingCoordinationRepository
{
    Task IncrementBusinessVersionAsync(CancellationToken ct);
    Task LockBusinessAsync(CancellationToken ct);
    Task IncrementStaffVersionAsync(Guid staffMemberId, CancellationToken ct);
}
