# Catalogue activation implementation (Member B)

## Existing integrated contracts

A supplies Service/StaffService.Activate/Deactivate, persisted flags, access checks
and staff mutation behavior. C supplies the employee transaction lock and booking
coordination checks. D supplies trusted actors, serialized conversations, bound
callbacks and retry routing. Activation uses these existing implementations.
No entity, mapping, migration, booking or shared transport changes are required.

BookingService reads business/staff versions before acquiring the employee lock.
Inside the transaction it rereads all eligibility flags and terms, then locks the
business coordination row followed by the staff coordination row and checks versions
before saving. StaffManagementService already independently increments the affected
staff version before waiting for the employee lock on deactivation. Reactivation
does not increment. Version publication can survive a later failed mutation.

## Individual offering service

OfferingActivationService follows that existing staff-deactivation contract:

1. Refresh staff and check owner/linked-active-employee access.
2. Read a fresh offering snapshot. An already inactive offering is a no-op.
3. For active-to-inactive changes, independently publish the staff version increment.
4. Begin the mutation transaction and acquire the employee lock.
5. Reload staff/offering, recheck permissions and apply the explicit target status.
6. Save and commit before returning the existing offering's DTO.

Reactivation requires an active shared service; an owner may still prepare an
inactive employee's offering without activating that employee. IDs, prices,
durations and accepted booking snapshots are preserved. No status is cascaded.
The singleton business version and unrelated employees' versions are unchanged.

OfferingActivationServiceTests verifies permissions, version scope, no-op behavior,
missing resources, inactive parent rejection, stale tracked state, snapshots and
rollback with an already published version. These service tests do not claim
coverage of joint booking races; the remaining acceptance work is listed below.

## Shared-service implementation

SharedServiceActivationService requires the owner, opens a transaction and acquires
B's ICatalogServiceLock for the shared service before reading linked employees.
PostgresCatalogServiceLock uses a transaction-scoped advisory key in a separate
catalogue namespace. Offering creation takes that gate before the employee lock;
price/duration updates of existing offerings retain their employee lock only.

The service reloads the shared entity and treats an unchanged status as a no-op.
For a change it collects all linked staff IDs, including inactive employees and
inactive offerings, and sorts them with one deterministic ordering. Deactivation
publishes all affected staff versions in a separate DI scope/context before taking
any employee lock. Reactivation publishes no invalidation. Ordered employee locks,
a fresh owner check and a service reload precede the final status write and commit.
No offering flags or booking snapshots are cascaded or rewritten.

The singleton business version is unchanged. Staff-version granularity can also
invalidate in-flight bookings for other services of an affected employee. Published
versions intentionally survive a failed mutation, following A's existing contract.
A deactivation waiting behind an earlier catalogue gate holder publishes only after
obtaining that gate; this serializes mutations, rather than promising that every
API invocation wins against a booking already holding final coordination locks.

SharedServiceActivationTests covers owner access, version scope, inactive links,
no-op/reactivation, cached entities, rollback and booking preservation. Its membership
race observes the exact blocked PostgreSQL backend before releasing the service
gate, then verifies that a new offering cannot be created for the now inactive
shared service. The duplicate-offering concurrency test likewise observes the new
gate where the second creator now waits.

## HTTP and Telegram controls

| Route | Required body | Access |
| --- | --- | --- |
| PUT /api/services/{serviceId}/activation | `{"isActive":true}` or `{"isActive":false}` | Owner |
| PUT /api/staff/{staffId}/services/{serviceId}/activation | Same explicit target | Owner or linked active employee |

Both routes return the existing DTO with HTTP 200 after commit. Missing/null/non-boolean
status and extra fields return 400. Credentials resolve the actor; body fields cannot
supply another identity, change prices or redirect the employee/service pair.
Missing resources and access/conflict errors use the existing shared mapping.

In `/services`, the owner has separate Activate/Deactivate buttons beside service
rename buttons. In `/offerings`, owner/employee activation actions sit beside the
existing term editor. A confirmation stores the explicit target and selected IDs.
Ordinary text never submits a confirmation. Back/Cancel use D's navigation.

A committed result is stored before sending and gets its own saved route. Failed
send retries restore that result without changing status again, even if an HTTP
operation changed it afterward. View services/offerings reads the current state;
Back does not leave completed forms in the overview ancestry. Confirmation buttons
remain bound to D's sender/chat/revision checks.

CatalogActivationEndpointsTests verifies roles, strict bodies, missing resources,
independent flags and unchanged terms. CatalogActivationTelegramTests covers failed
prompts, explicit confirmation, target preservation after concurrent changes,
foreign/cancelled buttons and reply retries after an opposite HTTP activation.

## Joint booking acceptance

CatalogActivationConcurrencyTests uses C's real BookingService, PostgreSQL locks
and coordination repository in separate scopes. Test decorators only pause progress;
they forward the real reads, version increments and lock acquisitions.

Six cases cover shared-service and individual-offering deactivation:

- With a booking paused after acquiring the staff lock, deactivation publishes the
  version before waiting for that same lock. The test observes the exact PostgreSQL
  waiter/holder relationship. The booking rejects the stale version, stores no row,
  and the waiting deactivation commits afterward.
- With a booking already holding its final coordination locks, the version increment
  waits for the booking's PostgreSQL backend. The booking commits its original
  price/duration/time snapshot before deactivation completes. Its idempotent replay
  returns the same ID; a new request is rejected as inactive.
- With a booking paused after its first version read, deactivation and reactivation
  both finish before it resumes. Although all eligibility flags are active again,
  the old confirmation is rejected. A fresh confirmation succeeds.

The unrelated employee and business versions remain unchanged. All paused tasks
are released and settled before scopes are disposed, including failure paths.
As a sensitivity check, temporarily omitting B's two version-publication calls
made both deactivate/reactivate tests fail because stale bookings were accepted.
The source files were restored before the final successful suite.

Validation on 2026-11-26, feature/erik-catalog-activation:

```sh
dotnet test AppointmentBooking.sln -c Release --nologo
```

731 tests passed: 170 unit and 561 integration, zero failures or skipped tests.
PostgreSQL tests used containers; Telegram tests used fake delivery.

## Remaining acceptance

Live Telegram smoke testing and team review remain outstanding. This branch includes
PR #33's offering-terms work; that PR was still open at verification time. The
separate schedule-rollback block is not included in this branch's test count.
No A/C/D implementation or schema changes are part of these services or adapters.
