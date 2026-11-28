namespace AppointmentBooking.Application.Abstractions;

/// <summary>B's service-membership gate; acquire in the transaction before any employee lock.</summary>
public interface ICatalogServiceLock
{
    Task AcquireAsync(Guid serviceId, CancellationToken ct);
}
