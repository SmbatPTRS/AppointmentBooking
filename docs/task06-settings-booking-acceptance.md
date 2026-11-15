# Task 06: settings and first-booking coordination acceptance

Verified on 2026-11-15 on `feat/task06-schema-and-release-verification`,
based on development `d00bc5b` with local Task 06 test additions.

Release build and **14 focused integration tests passed**, with zero failures/skips.
Tests use isolated PostgreSQL 16 containers, fixed clocks and separate operation
contexts. No developer database or live Telegram connection is used.

| Scenario | Expected and verified result |
| --- | --- |
| Confirmed, Cancelled or Completed booking history exists | Regional edit returns BusinessSettingsLocked; timezone/currency and booking snapshots remain unchanged. Labels remain editable. |
| Timezone edit finishes while a booking with an earlier version is paused | Booking returns ConfigurationChanged and persists no row. New timezone is preserved. |
| Currency edit finishes while a booking with an earlier version is paused | Booking returns ConfigurationChanged and persists no row. New currency is preserved. |
| Invalid currency submission occurs during an earlier booking attempt | Edit returns InvalidCurrency without changing settings; its independently committed version increment causes the older booking to return ConfigurationChanged. |
| Label-only edit occurs during booking | Labels are saved; business coordination version remains unchanged; booking succeeds. |
| Booking holds business/staff coordination locks before regional edit starts | PostgreSQL reports the edit connection blocked. After booking commits, edit returns BusinessSettingsLocked; original regional values and accepted booking snapshots remain intact. |
| Staff deactivation during an older booking attempt | Existing BookingCoordinationTests verify ConfigurationChanged and no persisted booking. |
| HTTP/Telegram settings adapters | Existing tests verify owner restrictions, label edits and regional rejection after cancelled booking history. |

New `SettingsBookingAcceptanceTests` supplies eight cases and uses the production
AccessService, BookingService, BusinessSettingsService, repositories, transactions
and locks. Test decorators pause execution only after real locks have been acquired.
The booking-first case observes `pg_blocking_pids` with a bounded cancellation token;
it does not infer blocking from an arbitrary sleep.

A regional submission advances the business version independently, even when the
subsequent edit is rejected. This intentionally invalidates an older confirmation;
label-only updates do not advance this version.

## Reproduce

From the repository root, after dependency restore and with Docker available:

```bash
/usr/lib/dotnet/dotnet test tests/AppointmentBooking.IntegrationTests/AppointmentBooking.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~SettingsBookingAcceptanceTests|FullyQualifiedName~BookingCoordinationTests|FullyQualifiedName~BusinessSettingsServiceTests|FullyQualifiedName~BusinessSettingsEndpointsTests|FullyQualifiedName~BusinessSettingsTelegramTests'
```

This records the focused settings/booking acceptance run, not a full solution run
or final release approval. Catalogue mutation coordination and live polling remain
separate acceptance areas. No production schema or service changes were required.
