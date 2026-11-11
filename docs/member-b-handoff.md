# Member B handoff

Updated 2026-11-09. Tasks 07–09 have application/repository groundwork;
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
- ScheduleService exposes GetHoursAsync, SetHoursAsync, ListDaysOffAsync, AddDayOffAsync and
  RemoveDayOffAsync with Actor, staff ID and CancellationToken. DayOffDto carries
  the ID needed for scoped deletion; repository entities are not returned.
- ScheduleService calls IAccessService.RequireStaffManagementAsync after acquiring
  the employee lock for writes, avoiding pre-lock tracked staff snapshots. Every mutation
  uses IUnitOfWork and IStaffOperationLock before reading decision data or writing.
  Hours changes and day-off additions inspect all future confirmed bookings,
  including dates beyond the booking window, using the business timezone.
  Duplicate days and schedule conflicts return existing AppException codes.
  Removing a day off expands availability and does not reject existing bookings.
- CatalogScheduleInputParser handles Telegram text formats only. Service-level
  validation, permissions and persistence remain necessary after parsing.

## Verification and limits

`dotnet test AppointmentBooking.sln -c Release --nologo`:
156 unit tests and 139 integration cases passed on 2026-11-09
after integrating development into feature/erik-schedule-access.

Schedule tests cover persistence, duplicate constraints, scoped repository lookup,
input errors, denied hours/day-off additions, future booking conflicts and removal.
Two concurrency cases hold the real PostgreSQL employee lock, observe the waiting
schedule operation through pg_locks, commit a booking, then verify that the hours
edit/day-off addition rejects the conflict and leaves stored schedule data intact.
The booking writer in these tests uses repositories/entities directly: this is
not joint acceptance of C's BookingService or coordination-version checks.

ScheduleService tests use an IAccessService double; they do not prove A's authorization rules.
ScheduleService is registered and exposed through the five schedule HTTP routes;
see member-b-schedule-api.md. Telegram schedule editing remains pending. No
production allow-all authorization implementation is supplied.

## Remaining work

- Complete Telegram schedule adapters; HTTP wiring now uses A's merged
  production access implementation. GetHoursAsync returns an authorized read-only
  model, or null when hours have not been configured.
- Broaden service tests for commit failures and boundary cases. Cancellation while
  waiting for the PostgreSQL employee lock is covered for hours edits and day-off
  additions, including transaction disposal and unchanged stored schedules.
  First-time hours creation, updating the same row, foreign day-off deletion,
  denied reads/removal and isolated hours reads are covered with an access-service
  double. These tests do not verify the production authorization implementation.
- Complete remaining CatalogService mutations; creation and active browsing are merged.
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
