# Telegram foundation (Task 19, in progress)

Outgoing text replies use ITelegramMessageSender.SendTextAsync(chatId, text, cancellationToken).
Chat ID is the message destination; it is not the Actor/user identity.

TelegramMessageSender wraps Telegram.Bot's ITelegramBotClient. Telegram.Bot is pinned to
22.10.3.3, compatible with the project's net8.0 target:
https://www.nuget.org/packages/Telegram.Bot/22.10.3.3

The sender uses plain text (no HTML/Markdown parsing), passes cancellation to the client
and lets delivery errors propagate. Call it after the application operation has committed;
a failed Telegram reply must not be treated as a failed/undone database commit.

A caller can receive ITelegramMessageSender without depending on the Telegram library.
Tests can supply Fixtures/FakeTelegramClient to record replies. Sender adapter tests use
the real library with an in-memory HTTP handler, a deliberately fake token and no live
Telegram calls or database.

This is a partial foundation. Client/options registration in Program.cs, configured bot
credentials, dispatcher, /start, role-aware keyboards and state are not implemented yet.
The sender is not connected to application startup. Polling/webhooks are later tasks.
Do not manually construct a new client per outgoing message; upcoming host wiring will
provide a reused client and keep its token in external configuration.
