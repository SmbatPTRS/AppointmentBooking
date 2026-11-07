namespace AppointmentBooking.Api.Telegram;

/// <summary>
/// Sends replies to a Telegram chat. The chat ID is a destination, not the caller's identity.
/// Implementations must let sending failures reach the caller so delivery can be retried.
/// </summary>
public interface ITelegramMessageSender
{
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default);
}
