# AppointmentBooking

A university team project for one business: employees offer services with individual
prices/durations; clients find available times, book and cancel through Telegram or
an HTTP API. Owner and employee operations use shared application permission checks.

## Current foundation

The shared foundation provides eight domain entities and EF mappings, shared
identity/error/clock contracts, repository wiring, migrations, a test host,
a scoped AppDbContext/IUnitOfWork, Swagger, health endpoints and CI for unit tests. Catalogue,
scheduling, authentication, booking rules and Telegram handlers are implemented in
the feature tasks. This README does not claim those features already work.

## Quick start (Bash)

Install .NET 8 SDK and Docker Compose v2, then from the repository root:

```bash
cp src/AppointmentBooking.Api/.env.example src/AppointmentBooking.Api/.env
# Edit .env: replace the password in POSTGRES_PASSWORD and the connection string.
docker compose --env-file src/AppointmentBooking.Api/.env up -d --wait postgres
dotnet tool restore
dotnet restore AppointmentBooking.sln
dotnet build AppointmentBooking.sln
dotnet test tests/AppointmentBooking.UnitTests/AppointmentBooking.UnitTests.csproj
cd src/AppointmentBooking.Api
dotnet run --launch-profile http
```

Open http://localhost:5080/swagger. Health: /health and /health/db.
For installed PostgreSQL, migrations, seed data, full Docker startup and later
Telegram deployment, follow [setup-and-deployment](docs/setup-and-deployment.md).

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
