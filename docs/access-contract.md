# Access contract for feature owners

Person A confirms these names and rules for Task 01. The first two names match
Person B's request. This records A's contract decision; it does not claim B/C have
reviewed the final version. Task 03 implements these checks in AccessService, which the API registers as a
scoped service. Set `Owner__TelegramUserId` to the owner's positive Telegram user
ID. An unset value or zero disables owner access. Negative values fail host
startup validation.

## Interface

Namespace: AppointmentBooking.Application.Abstractions.
Source: src/AppointmentBooking.Application/Abstractions/IAccessService.cs.
Actor, AppException, AppErrorKind and ErrorCodes use AppointmentBooking.Application.Common.

```csharp
Task RequireOwnerAsync(Actor actor, CancellationToken ct);
Task RequireStaffManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct);
Task RequireAppointmentManagementAsync(Actor actor, Guid staffMemberId, CancellationToken ct);
```

Inject IAccessService, not a concrete AccessService. Success completes the Task
without a return value; expected rejections throw AppException with Code and Kind
from the table below. Do not branch on human-readable exception messages.
All calls propagate CancellationToken; cancellation is not an access rejection.

## Rules and errors

Actor.UserId identifies a stored application User resolved by a trusted adapter.
Resolve the owner by matching that user's TelegramUserId to configured owner ID;
never trust a role, staff ID or owner flag supplied in a request.

| Condition | AppException.Code | AppException.Kind |
| --- | --- | --- |
| Actor's internal user no longer exists | ErrorCodes.Unauthorized | AppErrorKind.Unauthorized |
| Caller is not owner in RequireOwnerAsync | ErrorCodes.Forbidden | AppErrorKind.Forbidden |
| Requested staff profile does not exist (including Guid.Empty) | ErrorCodes.StaffNotFound | AppErrorKind.NotFound |
| Non-owner targets another user's staff profile | ErrorCodes.Forbidden | AppErrorKind.Forbidden |
| Non-owner targets their own inactive profile in RequireStaffManagementAsync | ErrorCodes.StaffInactive | AppErrorKind.Forbidden |

Order for staff checks: validate caller identity, load the requested staff profile,
reject missing staff, allow the owner, reject a different linked user, then check
activation when using RequireStaffManagementAsync. Consequently an owner also gets
StaffNotFound for a missing employee; an unrelated user gets Forbidden rather than
StaffInactive when targeting someone else's inactive profile. This contract returns
NotFound for absent staff; it does not deliberately disguise every rejection as 404.
A null Actor is a caller programming error (ArgumentNullException), not an anonymous
identity representation. Missing/invalid HTTP credentials are rejected by the
identity adapter before a service call; a stale user reference follows the table.

- RequireOwnerAsync protects creation, renaming and activation/deactivation of shared
  service types. An owner does not need a staff profile.
- RequireStaffManagementAsync allows the owner for any existing employee, including
  an inactive employee. A non-owner must be the linked user AND the profile must be
  active. Clients without that link cannot manage the employee's offerings/schedule.
- RequireAppointmentManagementAsync allows owner or linked user regardless of staff
  activation, preserving existing-appointment access. It is for C's appointment flows,
  not B's offering/schedule mutations and not client booking cancellation.

These are permission checks only. Permission to manage an inactive employee does
not make that employee eligible for a new booking. B/C still enforce their business
rules, transactions, locks, complete interval validation and protected mutation.

## B's service calls

```csharp
// CatalogService: before changing shared service types.
await access.RequireOwnerAsync(actor, ct);

// CatalogService / ScheduleService: before managing a staff offering or schedule.
await access.RequireStaffManagementAsync(actor, staffMemberId, ct);
```

Let AppException propagate to the shared HTTP/Telegram error mapping. Do not catch
all exceptions and convert them into Forbidden. If a later operation requires its
own staff lookup, preserve the same NotFound behavior and revalidation/locking rules.
Temporary unit-test fakes may implement IAccessService. Do not register an allow-all
fake in production or expose protected operations before Task 03 authorization exists.

## Team handoff

The access interface and these rules are supplied on feat/entities-and-configurations
for review into development. B can depend on the interface after integrating the
published branch/PR through the team's normal workflow. HTTP identity wiring remains part of Task 03.
B's repository interfaces and implementations are kept in B's PR; this change does
not replace them. Teammate review and approval must be recorded separately.
