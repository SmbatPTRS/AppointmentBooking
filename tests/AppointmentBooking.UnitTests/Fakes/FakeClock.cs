using AppointmentBooking.Application.Common;

namespace AppointmentBooking.UnitTests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; private set; }

    public FakeClock(DateTime initialUtc)
    {
        SetUtcNow(initialUtc);
    }

    public void SetUtcNow(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "The timestamp must be UTC.",
                nameof(utcNow));
        }

        UtcNow = utcNow;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "The duration cannot be negative.");
        }

        UtcNow = UtcNow.Add(duration);
    }
}