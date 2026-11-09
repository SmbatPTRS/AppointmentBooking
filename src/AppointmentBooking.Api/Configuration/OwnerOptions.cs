namespace AppointmentBooking.Api.Configuration;

public sealed class OwnerOptions
{
    public const string SectionName = "Owner";

    // Zero leaves owner access disabled.
    public long TelegramUserId { get; set; }
}
