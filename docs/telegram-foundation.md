# Telegram foundation — Member D / Task 19

## Available contracts

The Api project uses Telegram.Bot 22.10.3.3. Handlers depend on ITelegramMessageSender,
not directly on the library client. It supports plain text, menus of callback buttons,
and callback acknowledgements. Chat IDs are destinations, not actor identities.

Program calls AddTelegramInfrastructure. Options are validated at host startup when
Telegram is enabled. The client and sender are reused; handlers/dispatcher/actor resolver
are scoped. Disabled startup needs no token and makes no Telegram requests. Task 20 now adds the polling receiver; see [polling and shutdown](telegram-polling.md). Webhook reception remains Task 21.

Configuration in private environment variables / API .env:

- Telegram__Enabled (default false)
- Telegram__Mode (Polling or Webhook)
- Telegram__BotToken (never commit a real value)
- Telegram__BotUsername (optional, without @; needed for /start@your_bot commands)
- Telegram__WebhookUrl and Telegram__WebhookSecret (required for Webhook configuration)
- Owner__TelegramUserId (positive sender user ID, not chat ID; zero shows no owner menu)

A configured public-looking HTTPS URL is only syntactically validated here; Task 21 must
prove public reachability and implement secret verification/registration. The current
Compose environment mapping does not forward BotUsername; supply it explicitly if using
addressed commands in a container, until deployment wiring is finished.

## Receiving and routing

Both future receivers must use a fresh DI operation scope and await:

```csharp
await using var scope = scopeFactory.CreateAsyncScope();
await scope.ServiceProvider.GetRequiredService<TelegramUpdateDispatcher>()
    .DispatchAsync(update, cancellationToken);
```

Dispose a failed scope before retrying. No loopback HTTP requests and no fire-and-forget
handling. The dispatcher does not implement transport retry or polling offset logic.

It accepts Message and CallbackQuery updates with a human sender. /start registers through
A's actual UserService.GetOrCreateAsync, using sender ID, display name and optional username.
The returned User.Id constructs Actor. Chat ID is used only for reply/state destinations.
Commands addressed to another bot are ignored; plain /start needs no BotUsername setting.

StartHandler shows client options to everyone, staff-management options to active staff,
appointment access to inactive staff, and owner options using the configured Telegram ID.
Menu visibility is not authorization. Feature application services must check permissions.
Unimplemented feature buttons receive a readable not-available answer, not a mutation.

## Adding a feature handler (A/B/C)

Implement ITelegramHandler and register it as scoped:

```csharp
builder.Services.AddScoped<ITelegramHandler, SampleHandler>();

public sealed class SampleHandler(ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "sample";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        // Call application services with context.Caller.Actor, not any submitted user ID.
        context.State.BeginStep(Route, clock.UtcNow);
        context.State.SetAllowedCallbacks(["start"]);
        await sender.SendMenuAsync(context.ChatId, "Sample screen",
            [new TelegramMenuButton("Main menu",
                CallbackDataParser.Format("start", context.State.Revision))], ct);
    }
}
```

Commands /sample route to sample. Non-command text goes to the current Step, which must
name a registered handler. Register one handler for each distinct route; duplicates fail
when constructing the dispatcher. Routes match [a-z][a-z0-9_.]{0,19}.

TelegramHandlerContext provides the trusted caller, destination ChatId, input Text,
parsed Callback (if any), and the exclusively leased ConversationState. Handlers own screen
transitions. Do not acquire another lease inside a handler: the dispatcher already holds it.
Use scalar Selections and optional ConfirmationRequestId; never store EF entities, scopes
or services in state. Never use callback payload as caller authority.

## Button and navigation contract

Route callback format: `1:{revision-as-32-hex-digits}:{route}`; it fits Telegram's 64-byte limit.
Task 22 adds item buttons with short server-side keys and typed feature state; see
[the shared navigation contract and executable example](telegram-navigation.md).
Format/parser validate version, route and revision. Before feature dispatch, the callback
must match the user/chat state's current revision and its explicitly allowed route set.
Malformed/expired/copied/old buttons are acknowledged with a safe recovery message.
Handlers must set allowed callbacks before rendering buttons. BeginStep changes revision
and clears allowed routes, invalidating old screens. Start resets selections/confirmation.

State is per Telegram sender AND chat, serialized for the full interaction, and expires
after 30 minutes. Expired idle states/locks are removed opportunistically on later access;
active/waiting callers keep their entries. State is in memory for one app instance and
resets on process restart. Old buttons after restart fail safely.

Back now restores the previous feature screen and its saved fields. Cancel clears the
flow and returns to the main menu. Feature handlers render restored screens when
IsNavigation is true; they must adopt the shared contract rather than inventing their
own Back/Cancel handling. See telegram-navigation.md for retry and confirmation rules.

Expected AppException business rejections return Handled after a safe reply. Unexpected
processing failures, sending/acknowledgement failures and cancellation propagate so the
transport can retry. Irrelevant updates return Ignored (transport may acknowledge them).
Both Handled and Ignored count as processed for future polling offset progress.

Sending success belongs AFTER database commit. A failed reply does not undo a booking.
For a confirmation, keep ConfirmationRequestId/reconstruct the original intent on retry.
Do not call BeginStep, clear selections or generate a new RequestId merely because sending
failed. Durable request-key protection remains C's database/application responsibility;
conversation state alone cannot guarantee booking idempotency or exactly-once messages.

## Tests and current limits

FakeTelegramClient records replies, menus and callback answers and can FailNextSend.
No automated Telegram test sends real messages. Fake-repository foundation tests use A's
actual UserService, and a separate PostgreSQL fixture test checks actual persistence
across operation scopes. HTTP adapter tests exercise the real Telegram library with an
in-memory HTTP handler and a deliberately fake token.

Run all unit tests and the non-database Telegram/host checks:

```powershell
dotnet test AppointmentBooking.sln --filter 'FullyQualifiedName!~AppointmentBooking.IntegrationTests|FullyQualifiedName~TelegramFoundationTests|FullyQualifiedName~TelegramConfigurationTests|FullyQualifiedName~FakeTelegramClientTests|FullyQualifiedName~TelegramMessageSenderTests|FullyQualifiedName~SharedHostTests'
```

With Docker running, run the real database handoff test:

```powershell
dotnet test tests/AppointmentBooking.IntegrationTests/AppointmentBooking.IntegrationTests.csproj --filter FullyQualifiedName~TelegramUserRegistrationTests
```

This is a shared foundation, not implemented A/B/C feature screens or a running transport.
Task 20 provides the polling receiver on this branch. A real Telegram smoke test still needs configured credentials and a ready database; see telegram-polling.md.
