using AppointmentBooking.Api.Telegram;

namespace AppointmentBooking.IntegrationTests.Fixtures;

/// <summary>Records outgoing replies without contacting Telegram.</summary>
public sealed class FakeTelegramClient : ITelegramMessageSender
{
    private readonly List<SentTelegramMessage> sentMessages = [];

    public IReadOnlyList<SentTelegramMessage> SentMessages => sentMessages.AsReadOnly();

    public Task SendTextAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sentMessages.Add(new SentTelegramMessage(chatId, text));
        return Task.CompletedTask;
    }
}

public sealed record SentTelegramMessage(long ChatId, string Text);
