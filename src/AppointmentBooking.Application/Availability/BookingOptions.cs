using System.ComponentModel.DataAnnotations;

namespace AppointmentBooking.Application.Availability;

public sealed class BookingOptions
{
    [Range(1, int.MaxValue)]
    public int SlotStepMinutes { get; set; } = 15;

    [Range(0, int.MaxValue)]
    public int MaxAdvanceDays { get; set; } = 30;
}
