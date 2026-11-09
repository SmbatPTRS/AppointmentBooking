# Task 20 — polling and shutdown

## What the new receiver does

TelegramPollingService is a hosted background service. With Telegram enabled and Mode=Polling,
it removes the webhook with dropPendingUpdates=false and then calls the Telegram library's
GetUpdates. Long polling waits up to PollingTimeoutSeconds (default 25) for incoming work.
No receiver runs when Telegram is disabled; this polling service also stays idle in Webhook
mode. The actual webhook receiver remains Task 21.

A batch is handled sequentially, ordered by update ID. Every update gets a fresh async DI
scope and the same TelegramUpdateDispatcher used by the shared foundation. Repositories,
DbContext and handler instances therefore belong to that operation. The scope is disposed
before offset progress, the next operation, or recovery after a failure.

Offset is the next update ID to request. Only a completed dispatch and successful scope
disposal advance it. Telegram considers an update acknowledged when a later GetUpdates
request uses an offset greater than its ID. If an update fails, later updates in that batch
are not handled. The next receive request preserves the failed update's boundary.

Expected business rejections and ignored updates count as handled. Processing/sending
failures retry with exponential delay (default 1s, 2s, 4s, up to 30s). Telegram 429 RetryAfter
can extend that wait. Empty immediate replies get a short idle delay. Invalid credentials
or a conflicting receiver (401/409) stop the service so the operator can fix configuration.
Logs deliberately omit raw request URLs, tokens, update payloads and raw exception text.

One terminal acknowledgement error also counts as handled: answerCallbackQuery returning
400 with Telegram's specific "query is too old and response timeout expired" description
(including its "or query ID is invalid" variant). The sender identifies this error and the
dispatcher abandons that click before running its feature handler. Polling then advances
past it, allowing a fresh /start in the same batch to run. Other 400 responses, temporary
failures and rate limits still propagate to the existing retry policy. Expiry-like text on
another API method is not treated as an expired callback.

Shutdown cancellation is passed through receiving, dispatch, database work and waits.
The service stops receiving; an active operation is cancelled and its scope is disposed.
One application instance only. Stop the old receiver before changing modes or starting a
second bot process. Switching to polling removes the webhook without deleting pending work.

Offset is in memory. After restart Telegram may redeliver an unacknowledged update. User
registration remains protected in PostgreSQL. Booking confirmations must retain/reconstruct
C's original RequestId; this receiver does not provide booking idempotency by itself.
Duplicate Telegram messages are possible. Full booking-flow send-after-commit acceptance
still depends on C's handlers and Task 23.

## Local setup

Apply A's committed database migrations and required setup first. Keep real credentials in
the ignored API .env or process environment, never in tracked files. Configure:

```text
Telegram__Enabled=true
Telegram__Mode=Polling
Telegram__BotToken=<your development bot token>
Telegram__BotUsername=<optional bot username without @>
Telegram__PollingTimeoutSeconds=25
Telegram__PollingRetryDelaySeconds=1
Telegram__PollingMaxRetryDelaySeconds=30
```

Run the API from its directory with the Development http launch profile. Only use a bot
whose other receiver is stopped. Sending /start from Telegram should now reach the shared
dispatcher and A's persisted registration once credentials/database are configured. This
instruction is not a claim that a live phone smoke test has been performed.

The host reads local .env in Development as before. Never expose the token in screenshots
or pasted logs. A development bot/token has not been supplied during implementation.
Mode is read at receiver startup: restart the application after changing it.

## Verification

TelegramTransportTests exercises the actual library and dispatcher using an in-memory HTTP
handler: ordering, offset boundaries, scope/DbContext disposal, processing and sending retries,
business rejection, webhook-removal retry, non-busy receiving, inactive modes and shutdown.
It does not contact Telegram or open a database connection.

TelegramPollingPersistenceTests uses C's separate PostgreSQL container and A's real UserService:
registration survives a send failure and two fresh receiver instances with one stored user.
No real Telegram messages are sent. Run the full suite with Docker available, or select:

```powershell
dotnet test tests/AppointmentBooking.IntegrationTests/AppointmentBooking.IntegrationTests.csproj --filter 'FullyQualifiedName~TelegramTransportTests|FullyQualifiedName~TelegramPollingPersistenceTests'
```

A live /start smoke test, persisted-user restart proof and B's review are acceptance steps;
record their actual results rather than assuming them from implementation or compilation.

## Reference

Telegram documents GetUpdates offset acknowledgement and webhook exclusion at
https://core.telegram.org/bots/api#getupdates. The implementation uses the pinned
Telegram.Bot 22.10.3.3 API signatures. Default named-client HTTP loggers are removed because
Telegram credentials occur in request paths; application polling logs retain only safe
failure types and retry delays.

## Verification evidence — November 9, 2026

- Developer terminal full-suite run passed on the polling implementation: 144 unit tests
  and 68 integration tests, no failures/skips. The real PostgreSQL polling restart test also
  passed when selected separately.
- After bringing in the merged Task 19 logging fix/tests, 144 unit tests and 55 selected
  non-database integration checks passed in the assistant session, no failures/skips.
- Developer applied committed migrations to the development database, started the API in
  Polling mode and confirmed a welcome menu after sending /start from their development bot.
  The developer then stopped the API with Ctrl+C, restarted it and confirmed the menu again.
- The live check verifies reception/reply and restart behaviour. Database uniqueness evidence
  comes from the PostgreSQL tests, not from observing a menu. Full booking-retry acceptance
  remains dependent on C's handlers and Task 23.
