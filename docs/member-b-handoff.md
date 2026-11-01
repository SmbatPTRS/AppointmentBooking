# Member B: independent calculation groundwork

Status: partial Tasks 07–09, not completed application services.

- Catalog request/response DTOs and input annotations are proposed contracts.
- ScheduleRules validates hours, optional break pairs and weekday/date days off.
- AvailabilityCalculator consumes a snapshot and explicit current instant. Both
  listing and exact-start checks share the same interval implementation.
- Start grid is anchored at opening. Dates use the business timezone; output is UTC.
- Current time is explicit until A supplies IClock. The application service must
  obtain it through IClock, never from a request supplied by an end user.
- Inactive staff/services/offerings and valid nonworking dates return no slots.
- Invalid input/out-of-window dates throw standard argument exceptions for now;
  the future service must map these to A's agreed application error contract.

## Required integration with A/C/D

A owns all entities/enums/EF mappings/DbSets/migrations and shared access/errors/clock.
Required model fields are those in the team brief: Service, StaffService,
WorkingHours and StaffDayOff, with employee-specific price/duration, active flags,
one schedule per employee, valid breaks and unique offerings/days off.

C supplies confirmed intervals for the selected employee and transaction/lock
contracts. Input must exclude cancelled/completed bookings and other employees.
Final booking validation must use freshly read input inside C's transaction/lock;
the calculator alone does not prove concurrency safety. Schedule mutation services
must inspect all affected future confirmed bookings under that same lock.

B will add repository/service orchestration, permissions, duplicate checks, DI,
HTTP adapters and feature Telegram handlers against the agreed shared contracts.
D supplies shared Telegram state/sender/navigation. No transport or shared identity
implementation is included in this groundwork.

No database uniqueness, permission, persistence or transport guarantee is proved
by the pure unit tests. Atomic duplicate checks and database constraints remain service/DB integration work.

## Proposed schedule changes

ScheduleChangeValidator reports supplied future confirmed intervals invalidated by
proposed hours/breaks/days off. Supply the complete proposed schedule and every
potentially affected future booking, including dates beyond the advance window.
Starts at the evaluation instant are protected; already-started/history intervals
are excluded. Current offering duration and opening-based start grid are not
reapplied to historical accepted snapshots. No database writes or cancellation
occur. B's future ScheduleService must reject the mutation on any conflict.

The caller must enforce permissions and use C's transaction/lock before reading
bookings and invoking this calculation. Passing an incomplete list cannot prove a
schedule change safe. The unit tests do not replace real PostgreSQL race tests.

## Input validation before integration

CatalogRules exposes entry-point-independent validation using the same DTO
annotations as HTTP. Future CatalogService must call it and translate
ValidationException into the agreed shared application errors. Telegram must not
be the only validation boundary. No arbitrary currency precision/maximum-price
policy or global catalogue price has been introduced.

ScheduleRules validates unique weekday/date sets for one employee and can check a
proposed addition. A recurring day off and a specific date on that weekday may
coexist: they are distinct rules. Database uniqueness and race protection still
require A's mappings and C's lock boundary. Availability and schedule-change
validation reject malformed duplicate input rather than hiding data errors.

## Shared interval rule

ScheduleRules.FitsWorkingInterval is used by both availability and proposed schedule
validation. It checks the whole same-day local interval against opening, closing
and break boundaries. Cross-check tests exercise both consumers on the same start
grid for 1/30/40/90-minute durations. Existing bookings need not match a newly
anchored start grid; only new requests must match that grid.

## Handoff checklist for A and C (proposed; not agreed signatures)

| Owner | Required information/behavior | B consumer |
| --- | --- | --- |
| A | Service: Guid ID, name, active flag; no global price/duration | Catalog DTOs/repository |
| A | StaffService: Guid ID, staff/service IDs, decimal price, integer duration, active flag; unique staff/service pair | Catalogue and availability |
| A | WorkingHours: staff ID, TimeOnly opens/closes, nullable paired break endpoints; one row per staff | ScheduleRules |
| A | StaffDayOff: ID, staff ID, nullable DayOfWeek or DateOnly; exactly one; unique staff/weekday and staff/date | Days-off operations |
| A | Staff active state and business timezone/currency, shared access operations, IClock and error mappings | Future B services |
| C | Confirmed intervals for one employee overlapping a requested UTC range: start < rangeEnd AND end > rangeStart | Availability input |
| C | All future confirmed intervals affected by schedule changes, without a 30-day cutoff | ScheduleChangeValidator |
| C | Employee-scoped transaction lock on the same context/connection as repositories | B schedule/offering mutations |
| A/C | One consistent lock ordering for shared service activation and offering changes against booking creation | Future CatalogService |

CancellationToken and scoped lifetime must be retained by future async repository
and service methods. This document requests capabilities; it does not change A/C
interfaces or assert that the team has accepted a method signature.

B-owned service/repository files remain placeholders until the shared model and
permission/clock/transaction/query contracts are coordinated. HTTP (Task 10),
Telegram feature handlers (Task 11) and real-database acceptance (Task 12) remain
pending. Tasks 07–09 are groundwork, not complete end-to-end features.
