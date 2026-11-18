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

### PostgreSQL concurrency evidence

The integration suite uses the Testcontainers PostgreSQL fixture only; each
operation gets its own `AppDbContext` and connection. `BookingConcurrencyTests`
coordinates independent requests with a start gate and verifies committed rows
for same-staff overlap, different staff, adjacent appointments, cancellation
slot release, and concurrent lifecycle updates. `BookingIdempotencyTests`
verifies concurrent identical requests return one persisted ID, fresh-context
recovery after the unique-key race, altered payload rejection (including a
different employee), offering snapshot stability, and retries after
cancellation. `ScheduleConcurrencyTests` holds the PostgreSQL advisory lock
while the opposing operation starts, then commits the booking before the
day-off mutation proceeds; the mutation observes the committed booking and
leaves no day-off row. `SchemaConstraintsTests` also attempts direct SQL
inserts and asserts PostgreSQL reports the exclusion and unique constraints.

The booking test host is initialized once during setup, before concurrent
requests are released. Each booking operation creates and disposes its own
service scope and `AppDbContext`; the initialized host is disposed only after
all operations finish.

The unique-conflict recovery test deterministically inserts a competing booking
through a separate PostgreSQL context immediately before the attempted save.
The competing booking is cancelled, preserving its RequestId while avoiding an
unrelated overlap violation. The test asserts the real `23505` error and
`ux_bookings_client_request` constraint, rollback/disposal of the failed
transaction, and recovery through a different context. Matching payloads return
the persisted winner; changed payloads return `RequestIdConflict`. This is a
controlled database-conflict test; the separate concurrent-request test retains
the real employee lock.

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


### Scheduling and availability demonstration (Task 12)

1. Configure an employee to work 09:00–18:00 in Asia/Yerevan, with a 12:00–13:00
   break. On November 10, 2026, the 09:00 local start is 05:00 UTC. A 30-minute
   appointment may end exactly at the start of the break or at closing.
2. Read availability with the booking date in the business timezone. The default
   window includes today and the local date 30 days ahead. A start at the current
   instant is already unavailable.
3. Add a day off and query availability again. Removing it restores the employee's
   original slots, while another employee's day off remains in effect.
4. Run the schedule-versus-booking tests with PostgreSQL. They verify both lock
   orders for shortened hours, specific dates off and weekly days off. Only the
   operation that acquires the lock first persists when the two changes conflict.
5. For a two-hour service on Berlin's spring and autumn clock-change dates, using
   hours 00:00–06:00 and a 15-minute step, the first valid local start is 03:00.
   It is 01:00 UTC on March 29, 2026 and 02:00 UTC on October 25, 2026. The tests
   compare the exact UTC slots with final single-slot validation, including both
   occurrences of the repeated autumn hour.

### Catalogue/schedule integration coverage (Task 12)

`AvailabilityEndpointsTests` exercises the following against PostgreSQL and the
authenticated HTTP adapters, using the real booking service:

- Changing an offering from 30 minutes to 15 or 60 refreshes slots. New bookings
  require current terms; an existing booking retains its original price/duration.
- An inactive employee, service or offering has no available slots or visible
  employee offering. Reactivation restores only unoccupied slots; cancellation
  then restores the full original availability.
- Two employees offering the same service retain different prices/durations and
  independent calendars. Different offerings of one employee share occupied time.
- A break or later opening that overlaps a confirmed appointment is rejected
  without changing persisted hours or available slots. It succeeds after cancellation.
- When a specific date and weekly day off overlap, deleting either one does not
  reopen that date until the other is removed. The next week respects only the
  recurring rule. Both deletion orders are covered.

Offering term/status changes in these tests are fixture setup through existing
entities. They do not prove catalogue mutation authorization, lock ordering or
coordination-version updates. Those require separate management-flow acceptance.
These automated checks also do not replace a live Telegram demonstration.
