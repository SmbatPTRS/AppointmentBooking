using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}