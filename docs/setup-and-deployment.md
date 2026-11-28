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
Telegram. Offering creation and price/duration editing are integrated; service and
offering activation controls remain B work.
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
have opened the bot. Offering creation and price/duration editing are available;
activation controls remain pending B work;
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

The Task 24 workflow restores/builds the solution in separate unit and integration
jobs. The integration job uses the existing PostgreSQL 16 Testcontainers fixtures
on the runner's Docker engine; it does not need a shared PostgreSQL service or a
developer connection string. Both jobs upload TRX, console output and test diagnostics
for seven days, including when tests fail. Download them from the Actions run's
Artifacts section. A successful GitHub run still needs to be recorded after publication.
See [release acceptance](task06-release-checklist.md) for the remaining team gates.

## Full development Compose setup

After migrations and seed have been applied to the selected database:

```bash
docker compose --env-file src/AppointmentBooking.Api/.env --profile full up -d --build --wait
```

Swagger is at `http://localhost:8080/swagger`. Compose loads the API `.env` at
runtime, including uncommented `ApiAccess__Keys__N__Key` and matching
`ApiAccess__Keys__N__TelegramUserId` entries. Configure complete entries only; omit
unused entries rather than supplying blank keys or zero IDs. The explicit Compose
connection string uses `Host=postgres`; local host tools use `localhost`.

For a configuration file outside the checkout, set `API_ENV_FILE` to its absolute
path and also pass that same file with Compose `--env-file` for interpolation.
The default runtime path is `src/AppointmentBooking.Api/.env`. Runtime environment
variables remain visible to people with Docker access; the file is not copied into
the image. Keep the application on one instance with one polling receiver.

The PostgreSQL volume preserves data. Changing an initialization password in `.env`
does not change an existing role. `docker compose ... down` preserves the volume;
`down -v` deletes it. Never delete an existing volume to repair setup without consent.

For moving valuable data to a fresh container, use `pg_dump`/`pg_restore` instead of
pre-creating the same schema with migrations. Do not bake database data into images.
Installed-PostgreSQL rehearsal is pending. A local full Compose smoke check passed
on November 25 (details below). A clean-source local
restore/build, EF migration/seed and host-health rehearsal passed using disposable
PostgreSQL, with polling disabled; see the release checklist.

## Windows PowerShell setup

Use an installed .NET 8 SDK and Docker Desktop in Linux-container mode. From the
repository root, copy the example only when the local file does not already exist:

```powershell
if (-not (Test-Path src/AppointmentBooking.Api/.env)) {
    Copy-Item src/AppointmentBooking.Api/.env.example src/AppointmentBooking.Api/.env
}
# Edit the new file locally: passwords, owner ID, and optional complete API-key pairs.
docker compose --env-file src/AppointmentBooking.Api/.env config --quiet
docker compose --env-file src/AppointmentBooking.Api/.env up -d --wait postgres
dotnet tool restore
dotnet restore AppointmentBooking.sln
dotnet build AppointmentBooking.sln -c Release
Set-Location src/AppointmentBooking.Api
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet ef database update --project ../AppointmentBooking.Infrastructure --startup-project .
dotnet run -c Release --no-build --no-launch-profile -- --seed-business-settings --Seed:DisplayName="Appointment Studio" --Seed:StaffLabel="Employee" --Seed:TimeZoneId="Asia/Yerevan" --Seed:CurrencyCode="AMD"
dotnet run --launch-profile http
```

Press Ctrl+C to stop the host. Return to the repository root before running Compose
or solution-wide tests. `config --quiet` checks YAML/interpolation without printing
resolved passwords or API keys.

## Startup, shutdown and preserving data

Check `/health`, `/health/db` and (in Development) `/swagger/index.html`. The database
health endpoint proves connectivity only; check migration status and seed separately.
Keep Telegram disabled during setup. Enable Polling only after setup and stop any
other process using that bot token. Ctrl+C cancels polling and the current wait;
conversation memory is lost on process restart, while committed bookings remain.

From the repository root, stop the Compose app gracefully with:

```bash
docker compose --env-file src/AppointmentBooking.Api/.env --profile full stop -t 30 api
```

`down` removes containers and their network while preserving the named database
volume. Do not use `down -v` for valuable data. Keep the same Compose project name
and volume when recreating containers. A volume is persistence, not a backup.

For installed PostgreSQL (or the host-published Docker port), use PostgreSQL client
tools to back up, allowing a password prompt or a local pgpass file:

```bash
pg_dump -h localhost -p 5432 -U appointment_app -d appointmentbooking -Fc -f appointmentbooking.backup
# Restore into a separately created empty database, not a populated working database.
pg_restore -h localhost -p 5432 -U appointment_app -d appointmentbooking_restored --no-owner appointmentbooking.backup
```

Protect backup files like database credentials. When restoring a complete dump, do
not first create the same application schema with EF migrations. Verify restored
rows and migration history before changing the app's connection string.

## Remaining Task 24 acceptance

The CI changes need a real GitHub run after publication. Installed-PostgreSQL setup,
independent teammate reproduction and the complete live polling/Swagger demonstration
still need evidence. Catalogue activation and the feature work not yet merged remain
owned by their feature authors. Webhook/HTTPS webhook deployment stays deferred.
## Task 24 local verification — November 25, 2026

Branch `feature/member-d-task-24-deployment-ci` started from development `dc99ce1`.
The full Release suite passed **598 tests: 170 unit and 428 integration**, with
zero failures or skips. Integration tests used their disposable PostgreSQL 16
fixtures and fake Telegram. Workflow validation with actionlint 1.7.12 passed;
GitHub execution is still pending publication.

The existing Dockerfile built successfully. An isolated Compose project, fake
credentials, separate host ports and its own volume were used to verify:

- Compose YAML/interpolation and forwarding of a complete API-key pair.
- Real EF migration application to an empty PostgreSQL database.
- The container seed command run twice without duplicate settings.
- HTTP 200 from `/health`, `/health/db` and `/swagger/index.html`.
- A saved settings edit surviving recreation of both API and database containers.
- Absence of `.env`, `AGENTS.md` and `local-context` in the final application image.

The rehearsal containers/network were removed; the isolated test volume was retained.
No developer database, live bot credentials or real Telegram delivery were used.
This is a local smoke check, not the independent teammate or complete feature-flow
acceptance. Those checks and an actual integration CI run remain outstanding.

### Reverification with merged PR #33 — November 27, 2026

Task 24 was fast-forwarded to development `32a0fcc`, preserving its local CI and
configuration changes. This includes offering creation/terms editing and the
inactive-service creation fix. No merge conflicts occurred.

The final full Release rerun passed **684 tests: 170 unit and 514 integration**,
with zero failures or skips, using `dotnet test -c Release --no-build --no-restore`
and TRX logging. The initial run had one Docker API timeout while starting an
upgrade-test container, before migrations; the full rerun without concurrent
image/deployment work passed. No assertion was removed or relaxed.

The updated image built successfully and workflow validation passed. A fresh,
separate Compose database was migrated and seeded twice. Health, database health,
Swagger and an authenticated API request with a forwarded fake key returned 200.
A saved settings edit survived recreation of both containers. Final-image private
file checks passed; rehearsal containers/network were removed and the isolated
volume retained. No live Telegram credentials or developer database were used.

These are local results. Actual GitHub CI execution, independent teammate setup,
installed-PostgreSQL rehearsal and the complete live demonstration remain separate
acceptance steps. Service/offering activation controls remain pending integration.
