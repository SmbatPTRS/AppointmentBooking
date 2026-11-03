namespace AppointmentBooking.Application.Abstractions;

public interface IStaffOperationLock
{
    // Must be called inside an active transaction.
    // Waits if another transaction holds the lock for this employee.
    Task AcquireAsync(Guid staffMemberId, CancellationToken ct);
}