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

## Remaining B stages

Shared-service activation needs serialization over its set of linked employees.
The planned B-owned service gate must be acquired before enumerating all linked
staff IDs and before any employee lock. Offering creation will acquire the same
gate before its employee lock, preventing a new link from escaping enumeration.
Affected staff versions must be published through an independent scope before
ordered employee locks are acquired. Include inactive staff/offerings in that set.
Different shared-service mutations must use the same staff ordering.

HTTP and Telegram controls will call these B services. Confirmations must store an
explicit target status and a committed result; failed sends render that result
instead of toggling or replaying a mutation. Full global coordination and live bot
acceptance remain pending implementation and verification of those stages.
