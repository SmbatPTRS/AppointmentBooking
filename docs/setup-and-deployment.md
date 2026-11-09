# Setup and deployment

## Scope and prerequisites

This is the corrected foundation. It provides the solution, EF context/transaction
boundary, health endpoints, Swagger and configuration. Feature code, entity mappings,
seed data and migrations are added by their owners; the complete booking bot is not
implemented yet. Install a stable .NET 8 SDK in the 8.0.4xx feature band or later .NET 8
band accepted by global.json. Use PostgreSQL 16 for matching development environments.
Docker Compose v2 is optional for normal local development.

Commands below use Bash and start from the repository root unless stated otherwise.
Use a teammate-approved supported patch baseline before deployment. The explicit
package versions in this proposal are compatibility pins, not a claim of latest releases.

## Configure

```bash
cp src/AppointmentBooking.Api/.env.example src/AppointmentBooking.Api/.env
```

Edit the local .env. Replace the example password in BOTH POSTGRES_PASSWORD and the
connection string. The latter is explicit; this setup does not expand POSTGRES_*
inside it. Use the owner's numeric Telegram user ID when identity is implemented.
Keep Telegram disabled until its services are implemented and configured.

Compose reads this file because commands explicitly pass --env-file. ASP.NET Core
does not load .env automatically; the supplied Program.cs loads it with DotNetEnv
in Development. Existing process environment variables take precedence.

## Option A: database in Docker, application in IDE/CLI

```bash
docker compose --env-file src/AppointmentBooking.Api/.env up -d --wait postgres
dotnet tool restore
dotnet restore AppointmentBooking.sln
dotnet build AppointmentBooking.sln
dotnet test tests/AppointmentBooking.UnitTests/AppointmentBooking.UnitTests.csproj
```

Run from the API directory so content root and .env discovery are explicit:

```bash
cd src/AppointmentBooking.Api
dotnet run --launch-profile http
```

Open http://localhost:5080/swagger, /health and /health/db. The first health endpoint
shows host liveness; the second checks database connectivity, not schema readiness.
The bootstrap can connect to an empty database. Tests are added with their features;
an empty test project is not evidence of verified business behavior.

## Option B: installed PostgreSQL

Skip the Compose startup. As a PostgreSQL administrator, create a development role
and database (replace the example password):

```sql
CREATE ROLE appointment_app WITH LOGIN PASSWORD 'replace_this_local_password';
CREATE DATABASE appointmentbooking OWNER appointment_app;
```

Use the same localhost connection string, build and run commands. Docker is not
required for this development path. Do not run two servers on the same host port.
If a container uses POSTGRES_PORT=5433, also change the local connection string to
Port=5433. Container-to-container traffic still uses Host=postgres;Port=5432.

## Migrations and seed data

Do not generate empty migrations just to populate the planned folder.
After the first mappings are merged, A generates the initial migration from the
API directory. Other members apply the committed migrations rather than regenerating:

```bash
# From src/AppointmentBooking.Api
ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add InitialCreate --project ../AppointmentBooking.Infrastructure --startup-project . --output-dir Persistence/Migrations
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update --project ../AppointmentBooking.Infrastructure --startup-project .
```

Run migration generation only when owning the coordinated model change. A fresh DB
must receive all migrations and then the explicit, idempotent DatabaseSeeder once it
exists, including BusinessSettings. Do not mix EnsureCreated with migrations. Do not
silently apply schema updates on every production application instance at startup.

Task 13's migration must enable btree_gist and add the overlap constraint after
creating the bookings table. With the explicit DBML names and string status mapping:

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE bookings
ADD CONSTRAINT bookings_no_confirmed_overlap
EXCLUDE USING gist (
    staff_member_id WITH =,
    tstzrange(starts_at_utc, ends_at_utc, '[)') WITH &&
) WHERE (status = 'Confirmed');
```

Place this SQL in migrationBuilder.Sql(...), not in a manual per-developer setup step.
The extension must be available to the migration role (ask the database administrator
if extension creation is restricted). Add its Down behavior deliberately; do not drop
an extension used by unrelated database objects. Add the other CHECK/unique/foreign-key
constraints described in database.dbml. DBML notes alone do not enforce any rule.

## Full development Compose setup

After applying migrations to this database, start the API too:

```bash
docker compose --env-file src/AppointmentBooking.Api/.env --profile full up -d --build --wait
```

Open http://localhost:8080/swagger. This is a LOCAL development configuration, with
loopback-bound ports and development Swagger. API .env is excluded from the image;
Compose explicitly supplies the required settings. Extend its environment mapping
with the per-user API-key settings below if the container needs protected HTTP endpoints.
Use deployment environment/secrets for those values; never copy secret configuration
into the Dockerfile or a tracked appsettings file.

The PostgreSQL volume preserves data across container recreation. Changing an
initialization password in .env does not change an already-created database role.
`docker compose ... down` preserves the volume; `down -v` deletes database data.

## Telegram deployment, after Tasks 19–24

- Polling: enable Telegram, provide token, select Polling. Remove the old webhook
  without dropping pending updates before starting polling. Do not start webhook mode.
- Webhook: select Webhook, provide token, HTTPS webhook URL and secret, expose the API
  through a configured HTTPS reverse proxy/tunnel, register webhook at startup, and
  validate its secret header. Do not start polling.
- The local Compose file does not provide public HTTPS by itself.
- Stop the old receiver before switching modes; do not start two receivers for one bot.
- Configure graceful shutdown, per-operation scopes and state expiration.

## CI progression

Initial CI restores/builds all projects and runs unit tests. Task 24 adds a separate
integration job using PostgreSQL 16, migrations and test-only data. The included
Testcontainers.PostgreSql package supports a Docker-backed fixture; installing the
package alone does not implement the fixture or run database tests. If local integration
tests must also work without Docker, PostgresFixture should accept an explicit test-only
connection string and use Testcontainers only when none is supplied. Never point tests
at a development database containing data you need.

A Linux GitHub-hosted runner can run the Testcontainers suite once that fixture exists:

```bash
dotnet test tests/AppointmentBooking.IntegrationTests/AppointmentBooking.IntegrationTests.csproj --configuration Release --no-build
```

## Moving an installed database into a container

For an empty development DB, simply apply migrations and seed. For existing data,
create a pg_dump custom-format backup and pg_restore it into a fresh destination
instead of pre-creating the same schema with migrations. Data belongs in the volume;
do not bake a live database into the application image.

### HTTP API keys

Configure each key in the deployment environment using
`ApiAccess__Keys__0__Key` and `ApiAccess__Keys__0__TelegramUserId`. Increment the
index for additional keys. Each Telegram ID must belong to an existing registered
user. Send the key in the `X-Api-Key` header. In development Swagger, select
**Authorize** and enter the key for **ApiKey**. Protected endpoints opt into the
`ApiKey` authorization policy. The Telegram webhook uses its own secret.
