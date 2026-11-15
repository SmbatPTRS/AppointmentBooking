# AppointmentBooking architecture

One business, one configured owner, multiple employees, and multiple clients.
One ASP.NET Core host exposes HTTP controllers and hosts Telegram transports.

## Project dependencies

- Domain: entities and enums; no EF Core, ASP.NET Core, or Telegram dependency.
- Application -> Domain: use cases, permissions, DTOs, repository contracts, clock,
  transaction contract, and employee-lock contract.
- Infrastructure -> Application (and transitively Domain): EF Core, PostgreSQL,
  repository implementations, AppDbContext, system clock, employee lock.
- Api -> Application and Infrastructure: composition root, controllers,
  authentication, Telegram handlers, polling and conversation state. Webhook is deferred.
- Test projects may reference any project they test. Unit tests reference Api
  because CallbackDataParser is intentionally located there.

HTTP controllers and Telegram handlers BOTH call application services directly.
The bot does not call our own HTTP API to execute a booking.

Actor contains a trusted internal user ID, resolved from the authenticated API
identity or Telegram update sender. Never use a supplied request user ID as the actor.
Owner identity is configured using Telegram USER ID, not chat ID. Chat ID is a
message destination; it is not a general substitute for the sender's identity.
AccessService loads current staff membership/activation and applies shared checks.
AppException/ErrorCodes provide consistent expected failures for HTTP and Telegram.

AppDbContext implements IUnitOfWork. Do not add a forwarding UnitOfWork class.
All repositories and the PostgreSQL employee-operation lock use the SAME scoped
AppDbContext/connection. Normal staged writes commit through the operation
transaction. Coordination-version
increments deliberately commit independently before settings/deactivation transactions;
this lets waiting bookings detect configuration submissions, including rejected edits.

For booking creation: capture coordination versions, begin transaction, acquire the
employee lock, re-read eligibility,
settings/offering and availability, insert the booking, save, commit, then send a
success message. Acquire the same transaction-scoped lock for schedule mutations.
Before saving, lock/reread coordination versions in business-then-staff order and
reject a changed version. Regional settings writes use the business lock and check
for any booking under it. The SQL exclusion constraint is an additional backstop.

The initial transaction helper deliberately has no automatic retry strategy. After
an error, dispose that operation's scope; use a fresh context for conflict recovery.
Do not continue querying an aborted PostgreSQL transaction. If retries are later
introduced, retry the WHOLE transaction, including lock acquisition and reads.
Do not send Telegram messages inside a retryable database transaction.

A owns all entities, enums, EF configurations, DbSets/model registration and
migrations/snapshot. B/C supply requirements and review their mappings. C owns
AppDbContext transaction semantics and the shared lock. Feature owners implement
their repositories, services, adapters, tests and registrations. D provides shared
Telegram infrastructure; A/B/C provide their own feature screens.

Nine persisted entities include the original eight business entities and the later
BookingCoordination entity. Authentication, staff/settings, scheduling, availability,
booking lifecycle and Telegram polling/management are integrated. Catalogue management
is partially implemented; consult the release checklist for remaining work.
See [shared contracts](shared-contracts.md) for the Task 01
handoff, exact source interfaces, integration boundaries and review status.
AppDbContextFactory is optional; EF tooling can obtain the context from the API
host. Any future factory must follow the same configuration rules and must not
hard-code credentials.

The explicit `--seed-business-settings` setup command invokes the idempotent seeder
and exits before starting receivers. EF migrations remain a separate operation.
See [release checklist](task06-release-checklist.md) for verification and review limits.
