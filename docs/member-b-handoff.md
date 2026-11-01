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
by the pure unit tests. Duplicate day-off and offering checks remain service/DB work.
