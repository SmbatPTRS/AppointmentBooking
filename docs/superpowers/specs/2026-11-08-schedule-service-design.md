# Member B: ScheduleService design

## Intent and scope
Implement Task 08 using the merged domain, permission, transaction and repository
contracts. Allow authorized staff/owner management of one daily schedule and
recurring/specific days off without invalidating future confirmed bookings.

## Approach
Use the existing ScheduleRules and ScheduleChangeValidator inside an application
service. Reuse IAccessService, IUnitOfWork and IStaffOperationLock. Controller-led
validation would duplicate rules in Telegram; a replacement permission/locking
implementation would cross A/C ownership. Neither is appropriate here.

## Operations
- Get working hours (nullable when not configured).
- Set working hours, creating the row when absent and updating it otherwise.
- List days off as DTOs containing ID, weekday and date.
- Add a recurring weekday or specific date off; return its DTO for later removal.
- Remove a day off scoped by both staff ID and day-off ID.
All operations take Actor, staff ID and CancellationToken and authorize through
RequireStaffManagementAsync. No actor or permissions are inferred from request DTOs.

## Protected writes
Authorize first, validate input, begin a transaction, acquire the existing employee
lock, then load the schedule and bookings used in the decision. Read the clock
after lock acquisition. Check every future confirmed booking without an advance
window cutoff. Translate input failures into existing scheduling AppException
codes; reject duplicate days and conflicting changes before modifying tracked
entities. Save and commit only after checks pass. Disposal without commit rolls
back; cancellation and unexpected failures propagate.

Hours edits use the complete proposed hours and existing days off with the shared
validator and business timezone. Day-off addition checks affected future booking
local dates, including recurring rules beyond the booking window. This also allows
day-off configuration before working hours exist. Removing a day off only expands
availability, so it needs authorization and the transaction/lock but no conflict
rejection. Missing/foreign day-off IDs return DayOffNotFound. Return DTOs rather
than tracked domain objects.

## Verification
Unit tests cover denied access with no writes, invalid input, duplicate rules,
missing resources, future conflicts beyond 30 days, touching interval boundaries,
transaction/lock ordering, cancellation and failed commit propagation.
PostgreSQL tests cover committed persistence, rejected changes leaving stored data
unchanged, staff isolation and a deterministic lock race where a newly committed
booking is observed before a schedule change proceeds.

## Boundaries
No changes to A's entities, migrations or permission implementation or C's booking
and coordination-version implementation. No protected endpoints or permissive
production access fake. Update B's handoff documentation with remaining wiring.
Full booking-flow concurrency acceptance awaits C's integrated booking code.
User reviews and performs commits/pushes after verification.
