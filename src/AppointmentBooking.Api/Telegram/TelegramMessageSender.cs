using Telegram.Bot;

namespace AppointmentBooking.Api.Telegram;

/// <summary>Sends plain-text replies through the Telegram Bot API client.</summary>
public sealed class TelegramMessageSender(ITelegramBotClient client) : ITelegramMessageSender
{
    public async Task SendTextAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Leave parse mode unset so user-supplied text is not interpreted as HTML/Markdown.
        // Sending failures must propagate; a failed reply is not a successful delivery.
        await client.SendMessage(chatId, text, cancellationToken: cancellationToken);
    }
}
