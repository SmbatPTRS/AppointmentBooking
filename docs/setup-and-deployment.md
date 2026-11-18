# Setup and deployment

## Prerequisites and current scope

Use a stable .NET 8 SDK accepted by `global.json`, PostgreSQL 16, and Bash for these
examples. Docker Compose v2 is optional for running the application database;
Docker is required by the current integration-test fixture. On Smb's machine,
`/usr/lib/dotnet/dotnet` selects the verified SDK 8.0.425; substitute that executable
for `dotnet` below if the default PATH selects another installation.

Identity, staff/settings management, schedule management, availability, booking
lifecycle and Telegram polling are implemented. Catalogue browsing, owner service
creation/rename, and protected offering inspection are available through HTTP and
Telegram. Service activation and offering mutations remain B work.
Webhook is deferred and is not a release requirement. Select Polling when enabling
Telegram; selecting Webhook currently provides no update receiver.

## Configure locally

From the repository root:

```bash
cp src/AppointmentBooking.Api/.env.example src/AppointmentBooking.Api/.env
```

Edit this ignored file. Replace the password in BOTH `POSTGRES_PASSWORD` and the
explicit `ConnectionStrings__DefaultConnection`; the connection string does not
expand other variables. Set `Owner__TelegramUserId` to the owner's numeric sender
ID. Keep `Telegram__Enabled=false` during database setup.

Development loads `.env` from the API content root; existing environment values
win. Production and containers receive environment variables/secrets explicitly.
Do not commit credentials or enable multiple polling processes for one bot.

## Prepare PostgreSQL

For Docker PostgreSQL:

```bash
docker compose --env-file src/AppointmentBooking.Api/.env up -d --wait postgres
```

For installed PostgreSQL, create a role/database as an administrator, replacing the
example password locally:

```sql
CREATE ROLE appointment_app WITH LOGIN PASSWORD 'replace_this_local_password';
CREATE DATABASE appointmentbooking OWNER appointment_app;
```

Use the matching local connection string. Do not start two servers on one port.
The default localhost port is 5432; changing `POSTGRES_PORT` also requires changing
the local connection string. Container-to-container connections use `postgres:5432`.
The migration role must be able to install `btree_gist`.

## Build, migrate and seed

From the repository root:

```bash
dotnet tool restore
dotnet restore AppointmentBooking.sln
dotnet build AppointmentBooking.sln --configuration Release
cd src/AppointmentBooking.Api
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update --project ../AppointmentBooking.Infrastructure --startup-project .
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --configuration Release --no-build -- --seed-business-settings --Seed:DisplayName="Appointment Studio" --Seed:StaffLabel="Employee" --Seed:TimeZoneId="Asia/Yerevan" --Seed:CurrencyCode="AMD"
```

Review the target connection before applying migrations to an existing database.
Back up valuable data first. Teammates apply committed migrations; only A generates
new migrations for coordinated model changes. Never mix `EnsureCreated` with migrations.
Normal server startup does not automatically migrate or seed.

The explicit seed command validates four supplied defaults, invokes the existing
transactional DatabaseSeeder, then exits without starting HTTP or Telegram. Values
may alternatively be supplied as `Seed__DisplayName`, `Seed__StaffLabel`,
`Seed__TimeZoneId`, `Seed__CurrencyCode` environment variables. Repeating it preserves
all existing settings and coordination versions. It creates settings/business
coordination only, not demo users, staff or offerings.

Current migrations are `20261102120326_InitialCreate` and
`20261107171452_AddBookingCoordination`. The first installs `btree_gist` and
`ex_bookings_staff_confirmed_overlap` for Confirmed `[start,end)` intervals. The
second backfills version-zero coordination rows for existing settings and staff.
Older comment-only placeholder files do not represent another migration. Do not
execute a second manual overlap-constraint script.

## Run and register identities

From `src/AppointmentBooking.Api`:

```bash
dotnet run --launch-profile http
```

In Development, open `http://localhost:5080/swagger`. `/health` checks liveness;
`/health/db` checks database connectivity, not schema completeness.

To register real users, configure a bot token in secret configuration, set
`Telegram__Enabled=true` and `Telegram__Mode=Polling`, and restart. The owner and
prospective employees/clients each send `/start`; the reply includes their Telegram
sender ID. The owner can then add staff through Staff management. Staff must already
have opened the bot. Remaining offering-management screens are pending B work;
a fully UI-driven catalogue demonstration is not yet accepted.

For Swagger/API access, configure separate secret keys for registered identities:
`ApiAccess__Keys__0__Key` and `ApiAccess__Keys__0__TelegramUserId`, then increment
indices for other users. Restart after changing configuration. In Swagger choose
Authorize / ApiKey; requests send `X-Api-Key`. A key for an unregistered user returns
401. A valid identity lacking permission returns 403. Use distinct owner, employee
and client identities, rather than a shared administrator key.

Polling removes a previously registered webhook without dropping pending updates.
Stop any other receiver before starting this process. Live polling and deployment
rehearsal remain D/team acceptance work; no HTTPS webhook setup is required.

## Tests and CI

From the repository root, with Docker available:

```bash
dotnet test AppointmentBooking.sln --configuration Release --no-restore
```

The PostgreSQL fixture creates disposable test containers and applies migrations;
never substitute a valuable development database. It currently does not accept an
installed-PostgreSQL test connection. Unit tests can run without Docker.

The current CI restores/builds the solution and runs unit tests only. D still needs
to add integration CI and record a successful run. See
[release acceptance](task06-release-checklist.md) for local results and pending work.

## Full development Compose setup

After migrations and seed have been applied to the selected database:

```bash
docker compose --env-file src/AppointmentBooking.Api/.env --profile full up -d --build --wait
```

Swagger is at `http://localhost:8080/swagger`. Compose explicitly maps database,
Telegram and owner settings. It does not currently map per-user API keys: D must
supply the appropriate environment/secrets mapping for protected HTTP demonstrations
in the container. The API's `.env` file is excluded from the image.

The PostgreSQL volume preserves data. Changing an initialization password in `.env`
does not change an existing role. `docker compose ... down` preserves the volume;
`down -v` deletes it. Never delete an existing volume to repair setup without consent.

For moving valuable data to a fresh container, use `pg_dump`/`pg_restore` instead of
pre-creating the same schema with migrations. Do not bake database data into images.
Installed-PostgreSQL and full Compose rehearsals are pending. A clean-source local
restore/build, EF migration/seed and host-health rehearsal passed using disposable
PostgreSQL, with polling disabled; see the release checklist.
