# Task 06 release acceptance checklist

Snapshot: 2026-11-15, `feat/task06-schema-and-release-verification` from development
`d00bc5b`, including the Task 06 changes. A owns consolidation;
feature owners retain their implementation, review and defect-fix responsibilities.

**Release is pending.** Automated checks below do not substitute for teammate
review, missing catalogue operations, CI or a live demonstration. Webhook is
explicitly deferred by Smb and is not a release gate.

## Recorded automated evidence

| Area | Result/evidence | Owner |
| --- | --- | --- |
| Schema/model consistency | Passed: nine expected tables; no pending EF model changes/migrations; btree_gist; restrictive foreign keys; direct SQL constraint cases in FreshDatabaseSchemaTests and SchemaConstraintsTests | A; B/C review their rules |
| Fresh migrations | Passed on isolated PostgreSQL 16; committed initial and coordination migrations apply and can be reapplied | A |
| Upgrade and data preservation | Passed: 3 MigrationUpgradeTests cases; every original table/column unchanged, active/inactive staff backfilled, owner settings and booking lifecycle/snapshots preserved, new identity values remain usable | A; C review |
| Repeatable seed | Passed: 5 DatabaseSeederTests; changed defaults/owner edits/versions preserved, missing business scope repaired, concurrent calls and rollback/retry | A |
| Explicit seed command and health | SetupCommandTests exercises the actual executable, repeat seed, invalid defaults and /health plus /health/db; command exits before starting a server; Development .env handling preserves command-line precedence | A |
| Trusted identity/permissions | Passed: 73 focused integration + 8 unit authorization cases; [detailed matrix](task06-permission-acceptance.md) | A; C review |
| Regional settings versus first booking | Passed: 14 focused integration cases; both race orders, invalid submissions, labels, history statuses and deactivation; [detailed evidence](task06-settings-booking-acceptance.md) | A/C |
| Schedule/catalogue/availability implementation currently merged | Covered by full-suite service/repository/HTTP/Telegram tests, including schedule conflicts, access checks, active filters, availability boundaries and timezone logic; this does not complete unimplemented catalogue mutations | B |
| Booking lifecycle and idempotency currently covered | Full-suite BookingLifecycleTests, BookingIdempotencyTests and BookingEndpointsTests cover access, transitions, snapshots, changed terms and identity-scoped retries | C |
| Polling/navigation infrastructure | Full-suite transport/navigation tests use fake Telegram HTTP/delivery; cover retry, offset ordering, restart, cancellation, stale/copied callbacks and send failures | D; feature owners for their handlers |

Final Release result: **170 unit + 309 integration = 479 passed**, zero failures
and zero skips. Builds succeeded. The clean-source Release build also reported zero
warnings/errors. EF migration script generation succeeded using test-only configuration
without a database connection.

A separate clean export of the current source (without .git, .env, bin or obj) restored
tools/dependencies and built from scratch. Against a new disposable PostgreSQL 16
container, the documented EF database update and explicit seed command (twice)
succeeded. The running Development host returned HTTP 200 for /health, /health/db
and /swagger/index.html with polling disabled. Local rehearsal log:
`/tmp/task06-clean-1b1zn2y0/rehearsal.log` (temporary, not a committed artifact).
Focused counts overlap; do not add them together to estimate the full suite.

## Remaining gates and responsible owners

| Gate | Status / next action | Owner |
| --- | --- | --- |
| Shared-service editing/activation and employee offering creation/editing/activation through HTTP and Telegram | Pending implementation in current development; include price/duration validation, access checks and tests | B |
| Catalogue mutation lock/version integration | Pending agreed lock order and joint acceptance against actual booking creation | B/C; A for requested model changes |
| Actual create-booking races | Evidence pending for simultaneous overlapping creates and simultaneous duplicate requests, including same client/request across different employees. Current lock/constraint tests alone do not prove the whole BookingService recovery path | C |
| Joint booking/schedule races | Evidence pending with the actual booking service, beyond direct repository/entity writers in existing schedule race tests | B/C |
| Real booking Telegram confirmation/send-retry acceptance | Evidence pending for the full feature through the real dispatcher; infrastructure/sample-state retry tests alone do not establish committed-booking behavior | C/D |
| Integration CI | Pending: current workflow only runs unit tests. Add PostgreSQL integration job and record successful CI | D |
| Installed PostgreSQL setup rehearsal | Pending documented fresh setup against installed PostgreSQL | A/D |
| Full Compose/deployment rehearsal | Pending image/startup/health, secret/environment mapping and persistence checks; Compose currently lacks per-user API-key mapping | D |
| Clean-source setup rehearsal | Passed locally: clean export, restore/build, real EF database update, seed twice, API/DB health and Swagger. Independent clean-machine/installed-PostgreSQL reproduction remains pending | A verified; D/team reproduce |
| Live polling and Swagger demonstration | Pending owner, two employees and clients: /start, management, booking, cancellation, completion, wrong-role denial, restart/retry and separate Swagger keys | A/B/C/D |
| Team model and acceptance review | Pending C review of A work/booking constraints; B review of schedule/catalogue mappings; D review of setup/deployment; each owner signs off their evidence | A coordinates; B/C/D review |
| Final merge/release approval | Pending user approval and resolution of applicable gates; no automatic commit/push/merge | A/team |
| Webhook | Deferred; excluded from acceptance | D, only if later scheduled |

These are missing evidence/implementation in the inspected integration snapshot;
work on other branches may supersede them once reviewed and merged. A passing suite
is not evidence that unimplemented scenarios are complete.

## Demonstration checklist (not yet executed)

- [x] Rehearse documented build/migrations/seed twice from a clean source export against disposable PostgreSQL (A, 2026-11-15).
- [ ] Team independently reproduces setup on its target environment.
- [x] Start the host and verify both health endpoints and Swagger against the disposable database (A, 2026-11-15).
- [ ] Owner and two employees/clients register via /start; configure separate API keys.
- [ ] Owner adds/deactivates/reactivates staff and edits labels; wrong-role attempts fail.
- [ ] Configure two offerings of one shared service with different prices/durations (B dependency).
- [ ] Set hours, break, weekly/date-specific day off; reject changes invalidating bookings.
- [ ] Book for A, reject overlap for A, accept same time for B; repeat confirmation safely.
- [ ] Cancel as the owning client and show released availability; deny another client.
- [ ] Preserve booking snapshots after offering edits; complete only at/after end.
- [ ] Deactivate a staff profile; retain own/owner appointment access and block profile/schedule edits.
- [ ] Regional edit after any booking is rejected; label edit succeeds.
- [ ] Demonstrate polling restart/retry and present local/CI evidence.

## Reproduce final regression

With dependencies restored and Docker running, from the repository root:

```bash
/usr/lib/dotnet/dotnet test AppointmentBooking.sln --no-restore --configuration Release --logger 'trx;LogFileName=task06-final.trx'
```

On other machines use the .NET 8 executable appropriate to the environment.
TRX files are written under each test project's ignored TestResults directory;
they contain local run evidence and are not committed artifacts.
