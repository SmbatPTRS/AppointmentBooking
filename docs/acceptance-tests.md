# Acceptance criteria

These are required scenarios, not claims that tests already exist or pass.
Use real PostgreSQL for database constraints, transactions and concurrency tests.
Recorded results, owners and pending release gates are in [Task 06 release checklist](task06-release-checklist.md).

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

- /start and a full booking/cancellation flow work in polling mode.
- Webhook is deferred by project decision and is not an acceptance requirement.
- Conversation navigation handles Back, Cancel, invalid input and expiration.
- Old buttons, another user's callbacks and state loss on restart are handled safely.
- Repeated update delivery and confirmation clicks do not duplicate bookings.
- Send failure after commit does not undo or duplicate the appointment on retry.
- Polling offsets, graceful shutdown and removal of a previously configured webhook preserve pending work.
- HTTP and Telegram handlers invoke the same application services.
- Full setup works from the README; tests run on CI with a real test database.

## Task 23 delivery evidence (polling-only scope)

The team chose polling only. Webhook-secret, cross-transport and mode-switch acceptance
items from the original plan remain outside the current scope; they are not
implemented or claimed as passing.

### First scenario: reply failure after commit

`Task23BookingServiceDeliveryTests.FailedConfirmationSendAfterCommitRetriesWithoutCreatingAnotherBooking`
uses a test-only confirmation adapter, the shared dispatcher, C's real BookingService and
temporary PostgreSQL. It fails the reply after commit, verifies the saved booking, and
redelivers the exact update in a fresh scope. Both attempts must return the same booking
ID with exactly one saved row. This scenario first passed on November 11, before C's
real Telegram handler was available; it remains an isolated service/dispatcher check.

### Second scenario: repeated confirmation clicks through the real handler

`Task23BookingConfirmationTests.ClickingTheRealConfirmationButtonTwiceKeepsOneSavedBooking`
uses the registered production booking handlers to choose an employee, service, date and
time. Reaching the review screen must not save a booking. The first Confirm click saves
one confirmed booking; a new click on the same old button must leave its booking ID,
RequestId and status unchanged, without another success reply. Both clicks are acknowledged,
and the second receives an expired-button rejection.

### Third scenario: failed confirmation reply through the real handler

`Task23BookingConfirmationTests.FailedRealConfirmationReplyRetriesTheCommittedBookingWithTheSameRequestId`
walks the production booking screens and fails the confirmation reply. A fresh database
observer must find one confirmed booking despite the delivery exception, while the
conversation retains its RequestId. Redelivering that exact update in a fresh operation
scope must return the saved result with the same booking ID and RequestId, then clear
the completed flow. The two attempts acknowledge the same callback and only the successful
reply is recorded by the fake sender.

### Fourth scenario: automatic polling retry and offset ordering

`Task23BookingConfirmationTests.PollingRetriesFailedBookingReplyBeforeProcessingTheNextUpdate`
prepares the real booking review, then gives the real polling receiver a confirmation
(update 500) followed by /start (501). A fake Telegram HTTP adapter returns the updates;
the real dispatcher, booking handler and BookingService process them against temporary
PostgreSQL. The confirmation result send fails after commit.

Before redelivery, an independent database observer must find one confirmed booking, the
conversation must retain its RequestId, and neither the confirmation success nor the later
welcome reply may have been sent. Polling automatically requests the same batch again.
The retry keeps the saved booking ID/RequestId, sends one successful confirmation, then
handles /start. Requested offsets are null, null, 502. Both confirmation attempts clear
its callback spinner. Receiver shutdown is awaited with cancellation.

### Fifth scenario: old confirmation after Cancel or expiry

`Task23BookingConfirmationTests.OldConfirmationAfterCancelOrExpiryCannotCreateABooking`
runs twice through the real booking screens. After reaching review, it either presses the
shared Cancel button or advances a controlled clock by exactly 30 minutes. Clicking the
previous Confirm button must receive an expired-screen acknowledgement, send no new reply
or booking-success message, and leave no booking row for that client. State must be reset
with no booking selection, RequestId or history. /back must still open the welcome menu.
The clock is simulated; the expiry test does not wait 30 real minutes.

### Sixth scenario: old confirmation after restart

`Task23BookingConfirmationTests.OldConfirmationAfterRestartCannotCreateABooking`
prepares a review, disposes that app host and starts a fresh host/provider against the
same temporary database. The old Confirm button must be acknowledged as expired with no
new messages or booking. The conversation has no remembered selection, RequestId or history,
but the registered user remains persisted. /back opens the welcome menu without duplicating
the user. This simulates lost in-memory state with a new host; it does not launch OS processes.

### Seventh scenario: another sender using the same chat

`Task23BookingConfirmationTests.AnotherSenderInTheSameChatCannotUseTheClientsConfirmation`
sends the captured Confirm data as another registered person while keeping the original
chat ID. It must be rejected before any booking is stored. The actual client's screen,
revision and selected booking remain unchanged; their subsequent legitimate click can
save exactly one booking for their own internal user ID.

### Eighth scenario: Back invalidates the previous review

`Task23BookingConfirmationTests.BackRejectsOldConfirmationAndAllowsANewSelectedTime`
prepares a real review, presses Back to the time list and submits the old Confirm data.
That click must be acknowledged as expired without a new reply or database row. The user
then chooses another available time and reaches a new review. The original confirmation
must remain invalid even though the screen route is book.confirm again. Only the new
Confirm button may save one confirmed booking, at the newly selected UTC start.

### Ninth scenario: A/B feature replies through automatic polling retry

`Task23FeatureDeliveryTests.PollingRetriesCommittedFeatureReplyWithoutOverwritingALaterChange`
runs two cases using the real registered handlers, application services, polling receiver
and temporary PostgreSQL: A's employee self-rename and B's day-off creation. Each input
(update 500) is followed by /start (501), and the result send fails after the write commits.

Before returning the retry batch, an independent database observer verifies the committed
change. The test then simulates a later edit in that temporary database: another rename,
or removal of the newly added day off. Redelivery must not repeat the earlier mutation.
The later name remains intact and the removed day off is not recreated. A returns its
original saved result; B renders the current schedule. Both finish before the later
welcome reply, and polling requests offsets null, null, 502.

November 18 A/B verification: all eleven targeted Task 23 cases passed (11 passed,
0 failed, 0 skipped). This includes the previous booking checks and both feature cases.
No real Telegram request or message is sent. These checks now cover the polling receive
loop, with simulated HTTP. The original branch Release suite passed; PR #31 is under
review. See the combined-development verification below.

November 17 verification: both Task 23 tests passed against temporary PostgreSQL
(2 passed, 0 failed, 0 skipped). This was a targeted run, not a full-suite run.


November 18 verification: the three Task 23 tests passed (3 passed, 0 failed, 0 skipped),
including the real-handler failed-reply retry. Only the targeted Task 23 cases were run;
this is not a full-suite result.

November 18 polling verification: all four targeted Task 23 tests passed (4 passed,
0 failed, 0 skipped), including automatic polling retry and offset ordering. No full-suite
result is claimed for this step.

November 18 stale-button verification: all six targeted Task 23 cases passed (6 passed,
0 failed, 0 skipped), including Cancel and exact-boundary expiry. No full-suite result
is claimed for this step.

November 18 restart/ownership verification: all eight targeted Task 23 cases passed
(8 passed, 0 failed, 0 skipped). No full-suite result is claimed for this step.

November 18 Back verification: all nine targeted Task 23 cases passed (9 passed,
0 failed, 0 skipped). No full-suite result is claimed for this step.

### Test isolation after review

Each `Task23BookingConfirmationTests` case owns a separate disposable PostgreSQL
container, initialized and disposed by xUnit for that case. It does not use the shared
Postgres collection database. This keeps `/book` independent of employees created by
other tests, including large catalogue/pagination fixtures. The real dispatcher,
booking handlers, application services and booking assertions remain unchanged.

### Full Release verification — November 18, 2026

`dotnet test -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`
with TRX logging passed all 450 tests: 170 unit and 280 integration, zero failures/skips.
This includes eleven Task 23 cases. PostgreSQL tests used isolated temporary containers;
Telegram delivery and Bot API responses were simulated. No production code was changed
for these additions. The code and local verification are ready; B's review and C's
co-review of booking-retry evidence remain required before final acceptance.

### Combined-development verification after review — November 18, 2026

PR #31 was updated with development `3dab2a6` (including Task 06 and its Windows
line-ending fix). All eleven targeted Task 23 cases passed after per-case database
isolation. The full combined Release suite then passed **557 tests: 170 unit and
387 integration**, with zero failures or skipped tests.

Command: `dotnet test -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`
with TRX logging (`task23-combined-development.trx`). This supersedes the original
450-test branch-only result for integration acceptance. Tests use disposable PostgreSQL
and fake Telegram delivery. Reviewer acceptance remains separate from automated results.

## Demonstration order

1. Start application/database; show business name/timezone.
2. Add two employees and a shared service with different offerings.
3. Configure hours, break, weekly day off and specific date off.
4. Book with employee A; reject overlap for A; accept same time for B.
5. Cancel and show released availability.
6. Show snapshots survive offering changes.
7. Reject unauthorized schedule edit; repeat confirmation without duplicates.
8. Show the same operations through Swagger with separate role identities and present recorded test results.

Live demonstration steps remain pending until the team executes and records them;
catalogue setup through the UI depends on B completing offering management.

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
