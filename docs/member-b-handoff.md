# Member B handoff

Updated 2026-11-08. Tasks 07–09 have application/repository groundwork;
Tasks 10–12 and end-to-end acceptance are not complete.

## Implemented

- Catalog DTOs and CatalogRules enforce trimmed service names up to 200 characters,
  numeric(12,2) price bounds and at most two meaningful decimal places.
- CatalogRepository reads service/employee offerings and stages catalogue writes.
- ScheduleRepository reads hours/days off and stages tracked schedule mutations.
- ScheduleRules validates hours, breaks and recurring/specific days off.
- ScheduleChangeValidator checks proposed schedules against future confirmed
  intervals, preserving accepted booking durations and ignoring a new start grid.
- AvailabilityCalculator shares interval rules with schedule validation.
  AvailabilityService loads PostgreSQL inputs and uses IClock. C must call current
  availability inside the booking transaction/employee lock for final validation.
- ScheduleService exposes SetHoursAsync, ListDaysOffAsync, AddDayOffAsync and
  RemoveDayOffAsync with Actor, staff ID and CancellationToken. DayOffDto carries
  the ID needed for scoped deletion; repository entities are not returned.
- ScheduleService calls IAccessService.RequireStaffManagementAsync. Every mutation
  uses IUnitOfWork and IStaffOperationLock before reading decision data or writing.
  Hours changes and day-off additions inspect all future confirmed bookings,
  including dates beyond the booking window, using the business timezone.
  Duplicate days and schedule conflicts return existing AppException codes.
  Removing a day off expands availability and does not reject existing bookings.
- CatalogScheduleInputParser handles Telegram text formats only. Service-level
  validation, permissions and persistence remain necessary after parsing.

## Verification and limits

`dotnet test AppointmentBooking.sln -c Release --nologo`:
134 unit tests and 28 PostgreSQL integration cases passed on 2026-11-08.

Schedule tests cover persistence, duplicate constraints, scoped repository lookup,
input errors, denied hours/day-off additions, future booking conflicts and removal.
Two concurrency cases hold the real PostgreSQL employee lock, observe the waiting
schedule operation through pg_locks, commit a booking, then verify that the hours
edit/day-off addition rejects the conflict and leaves stored schedule data intact.
The booking writer in these tests uses repositories/entities directly: this is
not joint acceptance of C's BookingService or coordination-version checks.

Tests use an IAccessService double; they do not prove A's authorization rules.
The service is not yet registered/exposed through HTTP or Telegram. No production
allow-all authorization implementation is supplied.

## Remaining work

- Complete authorized schedule reads (GetHoursAsync), service wiring and transport
  adapters after A's production access implementation is available.
- Broaden service tests for first-time hours creation, cancellation/commit failures,
  foreign day-off deletion, denied listing/removal and boundary cases.
- Implement CatalogService against the agreed permission and locking contracts.
  Coordinate shared-service/offering lock ordering with A/C before mutations.
- Integrate Task 10 controllers and Task 11 feature handlers with trusted identity
  and D's conversation/sender/navigation contracts.
- Run Task 12 joint booking-versus-schedule acceptance with C's actual booking code,
  including coordination-version changes; these remain A/C-owned implementations.
- Reviewers: A for Task 08; C additionally reviews transaction/lock boundaries.

## Shared ownership

A owns entities, enums, mappings, migrations, shared access and settings behavior.
C owns booking lifecycle, booking queries and the employee transaction lock.
D owns Telegram transport, shared state, sender and routing. B consumes these
contracts and owns catalogue/schedule/availability services and feature adapters.
No schema or coordination-version changes are introduced by this schedule branch.
