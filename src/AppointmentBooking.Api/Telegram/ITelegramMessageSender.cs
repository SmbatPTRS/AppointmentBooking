namespace AppointmentBooking.Api.Telegram;

public sealed record TelegramMenuButton(string Text, string CallbackData);

public interface ITelegramMessageSender
{
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default);
    Task SendMenuAsync(long chatId, string text, IReadOnlyList<TelegramMenuButton> buttons,
        CancellationToken cancellationToken = default);
    Task AnswerCallbackAsync(string callbackQueryId, string? text = null,
        CancellationToken cancellationToken = default);
}
