using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace AppointmentBooking.Api.Telegram;

/// <summary>Sends plain-text replies; delivery errors propagate for transport-level retry.</summary>
public sealed class TelegramMessageSender(ITelegramBotClient client) : ITelegramMessageSender
{
    public async Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMessage(chatId, text, cancellationToken: cancellationToken);
    }

    public async Task SendMenuAsync(long chatId, string text, IReadOnlyList<TelegramMenuButton> buttons,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyboard = new InlineKeyboardMarkup(buttons.Select(button => new[]
        {
            InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData)
        }));
        await client.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: cancellationToken);
    }

    public async Task AnswerCallbackAsync(string callbackQueryId, string? text = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await client.AnswerCallbackQuery(callbackQueryId, text: text, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException error) when (!cancellationToken.IsCancellationRequested && IsExpiredCallback(error))
        {
            // Only this terminal answerCallbackQuery rejection is handled specially.
            // Abort dispatch rather than continuing with a feature mutation after an expired click.
            throw new TelegramCallbackExpiredException();
        }
    }

    private static bool IsExpiredCallback(ApiRequestException error) => error.ErrorCode == 400 &&
        (string.Equals(error.Message,
            "Bad Request: query is too old and response timeout expired or query ID is invalid", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(error.Message,
            "Bad Request: query is too old and response timeout expired", StringComparison.OrdinalIgnoreCase));
}
