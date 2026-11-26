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
rollback with an already published version. Joint booking races and adapters are
subsequent stages; these service tests do not claim that coverage yet.

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

## Remaining B stages

HTTP and Telegram controls will call these B services. Confirmations must store an
explicit target status and a committed result; failed sends render that result
instead of toggling or replaying a mutation. Joint real-booking activation races,
adapter acceptance and live bot testing remain later stages. No A/C/D implementation
or schema changes are part of these services.
