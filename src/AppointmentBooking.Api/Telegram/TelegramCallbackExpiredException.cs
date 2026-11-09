namespace AppointmentBooking.Api.Telegram;

/// <summary>The callback acknowledgement is permanently expired; abandon this update.</summary>
public sealed class TelegramCallbackExpiredException()
    : Exception("Telegram no longer accepts this callback acknowledgement.");
