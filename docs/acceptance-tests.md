# Acceptance criteria

These are required scenarios, not claims that tests already exist or pass.
Use real PostgreSQL for database constraints, transactions and concurrency tests.

## Foundation

- Clean clone restores and builds with the documented SDK.
- Installed PostgreSQL and Docker PostgreSQL both work with their connection strings.
- /health responds and /health/db verifies connection.
- A database created from migrations contains all required constraints.
- No .env, API key, bot token or database password is committed or included in the image.

## Identity and permissions

- Concurrent first visits create one user for one Telegram ID.
- One staff profile per user; one seeded business settings row.
- Client cannot manage staff or edit a schedule.
- Employee cannot edit another employee by changing route/payload IDs.
- Per-user API keys resolve different users correctly; owner status is not client-supplied.
- Existing appointments remain accessible for authorized management after deactivation.

## Catalogue and scheduling

- Two employees offer the same service with different prices/durations.
- Negative price, nonpositive duration and duplicate offering are rejected.
- Invalid hours/partial breaks are rejected.
- Weekly/specific days off work; duplicates and both/neither fields are rejected.
- Opening/closing, break boundaries, past starts and booking-window boundary are tested.
- Full duration fits; adjacent appointments are accepted.
- Timezone/UTC conversion and the documented DST policy are tested.
- Recurring day-off edits check all affected future confirmed appointments.

## Booking and database concurrency

- Two overlapping bookings for one employee: exactly one commits.
- Two employees at the same time: both commit.
- Adjacent appointments for one employee: both commit.
- Repeated confirmation with same RequestId/payload: one booking, same ID.
- Reused RequestId with altered payload: conflict, no extra booking.
- Concurrent duplicate requests, including different staff under the same client/key,
  cannot bypass uniqueness. Recover after rollback using a fresh context.
- Retry after catalogue changes or cancellation returns the original booking.
- Price/duration changed before confirmation requires renewed confirmation.
- Price/duration changed after booking leaves its snapshots intact.
- Authorized cancellation is idempotent and frees availability; unauthorized cancellation
  remains forbidden even when already cancelled.
- Completion is rejected before end time and accepted at/after end by an authorized actor.
- Adding a day off concurrently with booking creation cannot commit an inconsistent pair.
- Direct conflicting SQL inserts are rejected by the exclusion constraint.

## Telegram and deployment

- /start and a full booking/cancellation flow work in polling and webhook modes.
- Missing/incorrect webhook secrets are rejected.
- Conversation navigation handles Back, Cancel, invalid input and expiration.
- Old buttons, another user's callbacks and state loss on restart are handled safely.
- Repeated update delivery and confirmation clicks do not duplicate bookings.
- Send failure after commit does not undo or duplicate the appointment on retry.
- Polling offsets, graceful shutdown and mode switching preserve pending work.
- Both transports invoke the same feature/application logic.
- Full setup works from the README; tests run on CI with a real test database.

## Demonstration order

1. Start application/database; show business name/timezone.
2. Add two employees and a shared service with different offerings.
3. Configure hours, break, weekly day off and specific date off.
4. Book with employee A; reject overlap for A; accept same time for B.
5. Cancel and show released availability.
6. Show snapshots survive offering changes.
7. Reject unauthorized schedule edit; repeat confirmation without duplicates.
8. Switch transport and repeat the core flow; show Swagger and test results.
