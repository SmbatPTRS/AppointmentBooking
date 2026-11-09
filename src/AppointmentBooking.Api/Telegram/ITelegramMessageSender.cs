namespace AppointmentBooking.Api.Telegram;

public sealed record TelegramMenuButton(string Text, string CallbackData);

public interface ITelegramMessageSender
{
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default);
    Task SendMenuAsync(long chatId, string text, IReadOnlyList<TelegramMenuButton> buttons,
        CancellationToken cancellationToken = default);
    /// <exception cref="TelegramCallbackExpiredException">The callback is permanently expired.</exception>
    Task AnswerCallbackAsync(string callbackQueryId, string? text = null,
        CancellationToken cancellationToken = default);
}
