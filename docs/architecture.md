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
  authentication, Telegram handlers, polling, webhooks, conversation state.
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
AppDbContext/connection. No repository commits independently.

For booking creation: begin transaction, acquire employee lock, re-read eligibility,
settings/offering and availability, insert the booking, save, commit, then send a
success message. Acquire the same transaction-scoped lock for schedule mutations.
The SQL exclusion constraint is an additional database backstop.

The initial transaction helper deliberately has no automatic retry strategy. After
an error, dispose that operation's scope; use a fresh context for conflict recovery.
Do not continue querying an aborted PostgreSQL transaction. If retries are later
introduced, retry the WHOLE transaction, including lock acquisition and reads.
Do not send Telegram messages inside a retryable database transaction.

Feature owners add their own entity configurations, repositories, services,
controllers/handlers, tests and registrations. A coordinates migration generation.
D provides Telegram infrastructure; A/B/C provide their own feature screens.

The bootstrap does not yet implement authentication, business entities, migrations,
Telegram polling/webhooks, or feature services. The planned tree remains the target;
create each feature file when implementing it. AppDbContextFactory is optional:
EF tooling can obtain the context from the API host. If added later, it must follow
the same configuration rules and never hard-code credentials.
