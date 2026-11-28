# Member B handoff

Updated 2026-11-26. This branch includes catalogue, schedule and availability
services with HTTP/Telegram adapters and automated integration acceptance.
Team review and live Telegram acceptance remain open.

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
  Creation requires an active shared service, with a B service gate acquired
  before the employee lock so shared deactivation cannot miss a new link.
- Shared-service and individual-offering activation are implemented through HTTP
  and Telegram. Deactivation uses the existing independently committed staff
  versions before waiting for employee locks; shared changes use a stable membership
  gate and ordered locks. Reactivation does not increment versions or cascade flags.
  Explicit Telegram confirmations and saved replies cannot replay an old mutation
  after a newer HTTP change. See member-b-catalog-activation.md.

- Catalog DTOs and CatalogRules enforce trimmed service names up to 200 characters,
  numeric(12,2) price bounds and at most two meaningful decimal places.
- CatalogRepository reads service/employee offerings and stages catalogue writes.
- ScheduleRepository reads hours/days off and stages tracked schedule mutations.
- ScheduleRules validates hours, breaks and recurring/specific days off.
- ScheduleChangeValidator checks proposed schedules against future confirmed
  intervals, preserving accepted booking durations and ignoring a new start grid.
- AvailabilityCalculator shares interval rules with schedule validation.
  AvailabilityService loads PostgreSQL inputs and uses IClock. C's BookingService
  calls current availability inside its booking transaction/employee lock.
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
170 unit tests and 561 integration cases passed on 2026-11-26
on feature/erik-catalog-activation (731 total, zero failures or skipped tests).
The branch includes PR #33's offering-terms head 9325b5b, which was still open
at verification time, on development dc99ce1. That base includes schedule channel
acceptance, Task 06 setup checks and Task 23 delivery tests.

OfferingTermsServiceTests covers access, numeric limits, duplicate pairs, missing
resources, snapshots and stale tracked state. OfferingTermsEndpointsTests adds
HTTP required-field/role/route checks. OfferingTermsTelegramTests verifies forms,
paging, navigation and safe send retries after newer HTTP edits. Joint real-service
races cover price/duration edits versus booking in both lock orders, duplicate
creation, deactivation while waiting and cancellation. See member-b-catalog-api.md
and member-b-offering-overview.md.

Activation service/endpoint/Telegram tests cover access, strict request bodies,
independent flags, version scope, stale tracked state, rollback, confirmation and
safe reply retries. Six joint real-booking cases exercise publication before a
waiting employee lock, a booking already holding final coordination locks, and
an old confirmation spanning deactivation/reactivation. Both shared-service and
individual-offering paths are covered. See member-b-catalog-activation.md.

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
calendar and reactivation scenarios are covered. Those fixture mutations alone
do not prove management lock ordering; the activation and offering-terms concurrency
tests provide the corresponding real-service evidence.

ScheduleService tests use an IAccessService double; they do not prove A's authorization rules.
ScheduleService is registered and exposed through the five schedule HTTP routes;
see member-b-schedule-api.md. Telegram schedule editing now uses the same service
with real access checks; see member-b-telegram-schedule.md. No production allow-all
authorization implementation is supplied.

## Remaining work

- Team review and integration of this activation branch and its PR #33 dependency.
  A reviews catalogue/schedule invariants; C additionally reviews lock/version
  boundaries. Existing coordination contracts have been implemented and tested;
  no unanswered contract question blocks the activation code.
- Live Telegram smoke tests for catalogue creation/rename, offering terms/status,
  schedules and customer-visible availability. Fake-delivery tests do not verify
  real bot configuration or network delivery.
- The separate schedule-rollback test block is saved on
  feature/erik-schedule-rollback at c4ed4bf and is not integrated here. Its coverage
  is not included in the 731-test result; preserve it for a deliberate follow-up.

## Shared ownership

A owns entities, enums, mappings, migrations, shared access and settings behavior.
C owns booking lifecycle, booking queries and the employee transaction lock.
D owns Telegram transport, shared state, sender and routing. B consumes these
contracts and owns catalogue/schedule/availability services and feature adapters.
This activation branch consumes existing A/C coordination-version methods and
adds B's catalogue service gate. It changes no schema, entity, shared coordination
implementation, booking lifecycle, dispatcher or transport code.
