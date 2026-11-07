# Shared foundation and Task 01 handoff

## Status and ownership

This describes implemented interfaces and local integration checks, not a claim of
team approval. B reviews catalogue/schedule contracts and mappings; C reviews the
shared foundation, booking constraints and transaction integration; D reviews the
identity/host handoff. Their approval is still to be recorded by the team.

A maintains all eight entities, BookingStatus, all eight EF configurations,
DbSets/model registration, migrations and snapshot. B owns catalogue/schedule
repository implementations and service orchestration. C owns booking repositories,
transactions, locking and the PostgreSQL fixture. D owns shared Telegram transport,
state and routing. Feature owners do not create alternate domain models.

## Compile-time contracts

Exact signatures live in Application/Abstractions; depend on those interfaces,
never another feature's concrete repository. Async methods accept CancellationToken.

- IUserRepository: get by internal ID or Telegram ID; stage Add.
- IStaffRepository: get by staff ID or user ID, list with explicit inactive inclusion;
  stage Add. Client-facing callers request active profiles only.
- IBusinessSettingsRepository: get singleton record; stage Add. This is persistence,
  not authorization or seed orchestration.
- B owns ICatalogRepository and IScheduleRepository. Their concrete contracts and
  implementations are provided by B's feature/erik-catalog work (PR #5), not this
  change. On the current development base those files remain placeholders. B's
  catalogue API uses ListActiveServicesAsync/ListActiveOfferingsAsync DTO reads and
  GetServiceAsync/GetOfferingAsync/GetOfferingByIdAsync entity reads. B's schedule
  read API uses GetHoursAsync and GetDaysOffForDateAsync. Preserve these existing
  signatures when integrating B's PR; coordinate additional mutation/query needs
  with B rather than introducing a competing interface. A date-filtered day-off
  read alone cannot validate every future recurring schedule change.
- IBookingRepository: confirmed overlaps use start < rangeEnd AND end > rangeStart;
  future-confirmed reads include starts at the supplied instant, with no advance-window
  cutoff. Lists use half-open UTC ranges. AnyBookingExistsAsync includes all statuses.
- IUnitOfWork: save and begin a transaction. AppDbContext is the implementation.
  IStaffOperationLock.AcquireAsync runs inside that transaction on the same scope.
- IAccessService: RequireOwnerAsync, RequireStaffManagementAsync and
  RequireAppointmentManagementAsync accept trusted Actor and CancellationToken.
  The latter permits the relevant inactive staff profile to manage old appointments.
  Exact access/error rules and call examples are in [access contract](access-contract.md).
  A implements these checks in Task 03; this interface is not registered until then.

The access interface is available for compilation and review; its implementation
is future Task 03 work. B's repository interfaces and implementations arrive through
B's own PR, which must be integrated for B's services to compile against them. No placeholder is registered as a
working service. Existing user/staff/settings/booking repositories are scoped;
repositories stage writes and never commit. IClock/SystemClock is registered once;
tests may override IClock after infrastructure registration.

## Identity, errors and time

Actor.UserId is the internal Guid resolved by trusted HTTP/Telegram adapters, not
an ID or role accepted from the client's payload. Owner is configured by Telegram
user ID. Full identity and authorization implementation belongs to Task 03.
Shared expected failures use AppException(Code, Message, AppErrorKind) and ErrorCodes.
Map validation/unauthorized/forbidden/not-found/conflict to HTTP 400/401/403/404/409;
Telegram uses the same codes for user-facing messages. Do not expose raw SQL errors.

Persist instants as UTC DateTime (Kind=Utc) in timestamptz. HTTP instants use ISO 8601
with Z, local business dates YYYY-MM-DD, schedule times HH:mm. DayOfWeek follows
.NET Sunday=0 through Saturday=6. B's calculation DTOs use DateTimeOffset; adapt from
UTC entity values at the application boundary, never reinterpret UTC as local time.
Intervals are [start,end); adjacent appointments are valid. Use business timezone
for date boundaries and convert each boundary separately. Keep IClock.UtcNow
replaceable; never accept the current clock from an untrusted request.

Owner management, configurable timezone/currency, clocks and both Telegram modes
remain planned. Timezone/currency lock once bookings exist; display labels remain
editable. A/C must coordinate serialization of settings changes versus the first
booking before Task 02 acceptance. Booking/schedule transactions use C's employee
lock and fresh interval data; callbacks/messages are sent only after commit.
Global catalogue/offering eligibility lock ordering still needs A/B/C coordination
before feature acceptance; this handoff does not invent a new lock protocol.

## Model and verification

Eight shared entities and mappings include unique Telegram IDs, one staff profile
per user, singleton settings key, unique offerings/hours/days off, restrictive
foreign keys and booking snapshots/lifecycle checks. The schema migration also
provides the confirmed-overlap exclusion constraint. A owns future schema edits;
B/C review feature-specific requirements.

SharedFoundationTests checks scoped repository/context wiring, replaceable clock,
eight-entity registration and snapshot/lifecycle behavior without opening a DB.
SharedHostTests checks test-only configuration and clock replacement through the
real test host. Existing SchemaConstraintsTests and concurrency suites use an
isolated PostgreSQL container. On 2026-11-05, the solution test run with .NET SDK
8.0.425 passed 134 unit and 11 integration tests (145 total, no failures/skips).
These local results do not establish teammate approval.
