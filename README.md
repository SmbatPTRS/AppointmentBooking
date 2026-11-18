# AppointmentBooking

A university team project for one business: employees offer services with individual
prices/durations; clients find available times, book and cancel through Telegram or
an HTTP API. Owner and employee operations use shared application permission checks.

## Current status

Implemented: shared entities/mappings, API-key identity, staff/business management
through HTTP and Telegram, scheduling, availability, booking lifecycle and polling.
Catalogue browsing, owner service creation/rename, and protected offering inspection
are available through HTTP and Telegram. Service activation and offering mutations
remain B work. Webhook is deferred. CI currently runs unit tests only.

## Quick start

Follow [setup and deployment](docs/setup-and-deployment.md) to configure PostgreSQL,
restore/build, apply committed migrations, run the explicit business-settings seed,
and start the API/bot. Normal startup does not create the schema or seed settings.
.NET 8 is required; integration tests use disposable Docker PostgreSQL containers.

For the verified Task 06 results and remaining release dependencies, use the
[release checklist](docs/task06-release-checklist.md). A passing local suite does not
establish live Telegram, deployment or teammate approval.

## Structure and ownership

- Domain: entities/enums.
- Application: business services, permission checks, DTOs and persistence contracts.
- Infrastructure: EF Core/PostgreSQL, repository implementations, locks and clock.
- Api: HTTP endpoints, identity adapters, Telegram transports/handlers and composition.
- UnitTests/IntegrationTests: logic tests and real PostgreSQL/API/transport tests.

A: foundation, identity, staff/settings, integration coordination.
B: catalogue, schedules, availability.
C: booking lifecycle, transactions, PostgreSQL consistency.
D: Telegram infrastructure, transport, navigation and deployment.
Each owner implements their own feature screens and tests. A coordinates migrations.
AppDbContext implements IUnitOfWork; there is no separate forwarding UnitOfWork class.

## Documentation

- [Architecture](docs/architecture.md)
- [Shared contracts and Task 01 handoff](docs/shared-contracts.md)
- [Business rules](docs/business-rules.md)
- [Database model](docs/database.dbml)
- [API and bot flows](docs/api-and-bot-flows.md)
- [Setup and deployment](docs/setup-and-deployment.md)
- [Acceptance criteria](docs/acceptance-tests.md)
- [Shared Telegram navigation and feature handoff](docs/telegram-navigation.md)

- [Task 06 permission evidence](docs/task06-permission-acceptance.md)
- [Task 06 settings/booking evidence](docs/task06-settings-booking-acceptance.md)
