namespace AppointmentBooking.Application.Common;

public interface IClock
{
    DateTime UtcNow { get; }
}