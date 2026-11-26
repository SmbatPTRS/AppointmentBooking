# Member B handoff

Updated 2026-11-24. Tasks 07–09 have application/repository groundwork;
Tasks 10–12 and end-to-end acceptance are not complete.

## Implemented

- Schedule HTTP API and Telegram schedule management are merged (PRs #17/#24).
  See member-b-schedule-api.md and member-b-telegram-schedule.md.
- Availability HTTP API is merged (PR #23); see member-b-availability-api.md.
- Catalogue HTTP browsing and owner service creation are merged.
  Owner Telegram creation/rename and HTTP rename are merged (PR #26).
- Protected read-only offering inspection via HTTP and /offerings is merged (PR #29),
  including inactive rows, scoped employee selection, pagination and retry recovery.
  See member-b-offering-overview.md.
- This branch adds offering creation and atomic price/duration editing through HTTP
  and Telegram. OfferingTermsService uses the existing employee transaction lock,
  refreshes tracked staff before authorization and reloads tracked offerings before
  mutation. Creation rejects existing pairs, including disabled offerings; edits
  preserve offering status, IDs and all accepted booking snapshots.
  Shared-service/offering activation remains pending eligibility coordination.

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
170 unit tests and 507 integration cases passed on 2026-11-24
on feature/erik-offering-terms (677 total, zero failures or skipped tests).
The feature starts from development dc99ce1, including schedule channel acceptance,
Task 06 setup checks and Task 23 delivery tests.

OfferingTermsServiceTests covers access, numeric limits, duplicate pairs, missing
resources, snapshots and stale tracked state. OfferingTermsEndpointsTests adds
HTTP required-field/role/route checks. OfferingTermsTelegramTests verifies forms,
paging, navigation and safe send retries after newer HTTP edits. Joint real-service
races cover price/duration edits versus booking in both lock orders, duplicate
creation, deactivation while waiting and cancellation. See member-b-catalog-api.md
and member-b-offering-overview.md.

The merged cross-channel schedule block adds 41 cases; see
member-b-schedule-channel-acceptance.md for its matrix, commands and limits.

Schedule tests cover persistence, duplicate constraints, scoped repository lookup,
input errors, denied hours/day-off additions, future booking conflicts and removal.
Two concurrency cases hold the real PostgreSQL employee lock, observe the waiting
schedule operation through pg_locks, commit a booking, then verify that the hours
edit/day-off addition rejects the conflict and leaves stored schedule data intact.
Those ScheduleService tests use repositories/entities directly. The newer
AvailabilityEndpointsTests also exercise C's real BookingService in both lock orders
against hours/date-off/weekly-off changes. Catalogue snapshot, cross-offering
calendar and reactivation scenarios are covered. Catalogue fixture mutations do
not prove management mutation lock ordering or coordination-version updates.

ScheduleService tests use an IAccessService double; they do not prove A's authorization rules.
ScheduleService is registered and exposed through the five schedule HTTP routes;
see member-b-schedule-api.md. Telegram schedule editing now uses the same service
with real access checks; see member-b-telegram-schedule.md. No production allow-all
authorization implementation is supplied.

## Remaining work

- Broaden service tests for commit failures and boundary cases. Cancellation while
  waiting for the PostgreSQL employee lock is covered for hours edits and day-off
  additions, including transaction disposal and unchanged stored schedules.
  First-time hours creation, updating the same row, foreign day-off deletion,
  denied reads/removal and isolated hours reads are covered with an access-service
  double. These tests do not verify the production authorization implementation.
- Complete shared-service/offering activation after agreeing eligibility-change
  coordination with A/C. Employee-scoped price/duration serialization uses the
  existing shared employee lock and is covered by joint booking acceptance.
- Remaining catalogue HTTP/Telegram operations for Tasks 10/11: activation controls.
  Creation, name editing, offering terms, inspection and schedule adapters are implemented.
- Task 12 booking-versus-schedule tests now exercise C's actual booking code.
  Complete activation coordination acceptance and live Telegram smoke tests;
  coordination-version implementations remain A/C-owned.
- Reviewers: A for Task 08; C additionally reviews transaction/lock boundaries.

## Shared ownership

A owns entities, enums, mappings, migrations, shared access and settings behavior.
C owns booking lifecycle, booking queries and the employee transaction lock.
D owns Telegram transport, shared state, sender and routing. B consumes these
contracts and owns catalogue/schedule/availability services and feature adapters.
No schema or coordination-version changes are introduced by this offering-terms branch.
