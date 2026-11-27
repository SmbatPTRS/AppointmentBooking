# Task 06 release acceptance checklist

Updated: 2026-11-18, `feat/task06-schema-and-release-verification`, including fresh
development `d41b0e7` through merge `c2facb5`. The original Task 06 work started from
`d00bc5b` on 2026-11-15. A owns consolidation;
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
| Booking lifecycle and creation concurrency | Passed: BookingLifecycleTests, BookingConcurrencyTests, BookingIdempotencyTests and BookingEndpointsTests cover permissions, transitions, snapshots, overlapping creates, simultaneous identical requests, different employees, adjacent intervals, retry after cancellation and deterministic real unique-conflict recovery through a fresh scope. Cross-employee RequestId reuse is also tested sequentially | C |
| Joint booking/schedule races | Passed: AvailabilityEndpointsTests exercises actual BookingService/ScheduleService in both lock orders for shortened hours, dated days off and weekly days off | B/C |
| Shared-service management and offering inspection | Passed in the combined full suite: HTTP/Telegram service creation/rename and protected read-only offering inspection, including shared-service failed-reply recovery with the current dispatcher | B |
| Polling/navigation infrastructure | Full-suite transport/navigation tests use fake Telegram HTTP/delivery; cover retry, offset ordering, restart, cancellation, stale/copied callbacks and send failures | D; feature owners for their handlers |

Latest combined Release result (2026-11-18): **170 unit + 376 integration = 546
passed**, zero failures/skips. The actual Desktop merge result was tested with
`--logger 'trx;LogFileName=task06-development-sync.trx'`; builds succeeded. It includes
the original Task 06 work and merged PRs #26, #27, #28 and #29.

Historical original Task 06 result (2026-11-15): **170 unit + 309 integration = 479
passed**, zero failures/skips. The original clean-source Release build reported zero
warnings/errors, and EF migration script generation succeeded with test-only
configuration without a database connection.

On 2026-11-15, a separate clean export of the original Task 06 source (without .git,
.env, bin or obj) restored
tools/dependencies and built from scratch. Against a new disposable PostgreSQL 16
container, the documented EF database update and explicit seed command (twice)
succeeded. The running Development host returned HTTP 200 for /health, /health/db
and /swagger/index.html with polling disabled. Local rehearsal log:
`/tmp/task06-clean-1b1zn2y0/rehearsal.log` (temporary, not a committed artifact).
That clean-source rehearsal has not been repeated for the newer teammate features.
The detailed permission/settings documents record their dated original focused runs;
their tests also passed in the latest combined full run. Focused counts overlap, so
do not add them together to estimate the full suite.

## Remaining gates and responsible owners

| Gate | Status / next action | Owner |
| --- | --- | --- |
| Service activation/deactivation and offering creation/editing/activation through HTTP and Telegram | PR #33 offering creation/terms editing is merged. Service/offering activation controls and their acceptance remain pending | B |
| Catalogue mutation lock/version integration | PR #33 includes employee-lock coordination and both booking/terms-edit lock orders. Activation coordination remains pending | B/C; A for requested model changes |
| Simultaneous same-client/RequestId submissions across different employees | Still pending: the new suite covers simultaneous identical requests and sequential cross-employee key reuse, plus deterministic unique-conflict recovery; it does not explicitly exercise simultaneous cross-employee submissions under the same key | C |
| Real booking Telegram confirmation/send-retry acceptance | Task 23 is merged with real booking-handler, dispatcher and database retry evidence; live feature demonstration remains pending | C/D |
| Integration CI | Task 24 adds separate unit/PostgreSQL jobs and retained diagnostics; first GitHub run pending publication | D |
| Installed PostgreSQL setup rehearsal | Pending documented fresh setup against installed PostgreSQL | A/D |
| Full Compose/deployment rehearsal | Local image/migrations/seed/health/persistence smoke passed November 25; runtime API-key forwarding added. Independent target deployment and live flow remain pending; see setup instructions | D |
| Clean-source setup rehearsal | Passed locally: clean export, restore/build, real EF database update, seed twice, API/DB health and Swagger. Independent clean-machine/installed-PostgreSQL reproduction remains pending | A verified; D/team reproduce |
| Live polling and Swagger demonstration | Pending owner, two employees and clients: /start, management, booking, cancellation, completion, wrong-role denial, restart/retry and separate Swagger keys | A/B/C/D |
| Team model and acceptance review | Pending C review of A work/booking constraints; B review of schedule/catalogue mappings; D review of setup/deployment; each owner signs off their evidence | A coordinates; B/C/D review |
| Final merge/release approval | Pending user approval and resolution of applicable gates; no automatic commit/push/merge | A/team |
| Webhook | Deferred; excluded from acceptance | D, only if later scheduled |

These are missing evidence/implementation in the inspected integration snapshot;
work on other branches may supersede them once reviewed and merged. A passing suite
is not evidence that unimplemented scenarios are complete.

## Demonstration checklist (live feature demonstration pending)

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
/usr/lib/dotnet/dotnet test AppointmentBooking.sln --no-restore --configuration Release --logger 'trx;LogFileName=task06-development-sync.trx'
```

On other machines use the .NET 8 executable appropriate to the environment.
TRX files are written under each test project's ignored TestResults directory;
they contain local run evidence and are not committed artifacts.
