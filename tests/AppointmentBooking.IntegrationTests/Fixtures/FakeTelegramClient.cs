using System.Collections.Concurrent;
using AppointmentBooking.Api.Telegram;

namespace AppointmentBooking.IntegrationTests.Fixtures;

/// <summary>Records operations and injects delivery failures without contacting Telegram.</summary>
public sealed class FakeTelegramClient : ITelegramMessageSender
{
    private readonly ConcurrentQueue<SentTelegramMessage> messages = new();
    private readonly ConcurrentQueue<SentTelegramMenu> menus = new();
    private readonly ConcurrentQueue<AnsweredTelegramCallback> answers = new();
    private Exception? nextSendFailure;
    public IReadOnlyList<SentTelegramMessage> SentMessages => messages.ToArray();
    public IReadOnlyList<SentTelegramMenu> SentMenus => menus.ToArray();
    public IReadOnlyList<AnsweredTelegramCallback> CallbackAnswers => answers.ToArray();

    public void FailNextSend(Exception failure) => nextSendFailure = failure;

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref nextSendFailure, null) is { } error) throw error;
        messages.Enqueue(new SentTelegramMessage(chatId, text));
        return Task.CompletedTask;
    }

    public async Task SendMenuAsync(long chatId, string text, IReadOnlyList<TelegramMenuButton> buttons,
        CancellationToken cancellationToken = default)
    {
        await SendTextAsync(chatId, text, cancellationToken);
        menus.Enqueue(new SentTelegramMenu(chatId, text, buttons.ToArray()));
    }

    public Task AnswerCallbackAsync(string callbackQueryId, string? text = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        answers.Enqueue(new AnsweredTelegramCallback(callbackQueryId, text));
        return Task.CompletedTask;
    }
}

public sealed record SentTelegramMessage(long ChatId, string Text);
public sealed record SentTelegramMenu(long ChatId, string Text, IReadOnlyList<TelegramMenuButton> Buttons);
public sealed record AnsweredTelegramCallback(string Id, string? Text);
