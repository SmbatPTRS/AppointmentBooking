namespace AppointmentBooking.Application.Abstractions;

public interface IBookingCoordinationRepository
{
    Task IncrementStaffVersionAsync(Guid staffMemberId, CancellationToken ct);
}
