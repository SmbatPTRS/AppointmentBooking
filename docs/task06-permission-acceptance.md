# Task 06: identity and permission acceptance

Verified on 2026-11-15 on `feat/task06-schema-and-release-verification`,
based on development `d00bc5b`, with local Task 06 test additions.

Release verification passed **73 integration tests and 8 authorization unit tests**,
with zero failures/skips. Integration tests use isolated Testcontainers PostgreSQL;
Telegram scenarios use the real host/dispatcher/application services with fake delivery.
No developer database or Telegram network connection is used.

## Acceptance evidence

| Requirement | Evidence | Result |
| --- | --- | --- |
| Missing/invalid keys, multiple key values, or keys for an unregistered account cannot authenticate | PermissionAcceptanceTests, StaffAndAccessTests | Passed: HTTP 401 |
| Identity comes from a stored user linked to the API key; route/query/payload IDs and spoofed headers do not grant owner/other-staff access | PermissionAcceptanceTests, StaffAndAccessTests, ScheduleEndpointsTests | Passed |
| Owner manages staff/settings; employees and clients cannot use owner mutations | StaffEndpointsTests, BusinessSettingsEndpointsTests, StaffManagementTelegramTests, BusinessSettingsTelegramTests | Passed |
| Two active employees can rename themselves and cannot rename each other | PermissionAcceptanceTests | Passed through HTTP; Telegram self-service cannot be redirected with another profile in state |
| Deactivation blocks own-profile editing, including deactivation after opening a Telegram prompt | PermissionAcceptanceTests, OwnProfileTelegramTests | Passed |
| Deactivated employee can list/complete their existing appointments; owner retains access | PermissionAcceptanceTests, BookingLifecycleTests | Passed through HTTP and Telegram |
| Unrelated employees/clients cannot read or complete another employee's appointments, including after completion | PermissionAcceptanceTests, BookingLifecycleTests | Passed |
| Client booking lists are isolated by authenticated identity; cancellation uses the authenticated client | PermissionAcceptanceTests, BookingEndpointsTests, BookingLifecycleTests | Passed |
| Telegram uses the actual sender, including CallbackQuery.From, rather than the chat or callback message author | PermissionAcceptanceTests, TelegramUserRegistrationTests | Passed |
| Copied/stale callbacks, invalid inputs, Back/Cancel and failed-send retries preserve permission boundaries | StaffManagementTelegramTests, OwnProfileTelegramTests, BusinessSettingsTelegramTests, TelegramNavigationTests, ManagementRetryTests | Passed |
| First visits resolve one stored identity | UserIdentityTests, TelegramUserRegistrationTests | Passed |
| Application authorization distinguishes owner, own active staff, inactive appointment access, unrelated and stale identities | AccessServiceTests | Passed: 8 unit tests |

The appointment picker exposes public active staff names. This is not permission to
read their appointments: the application checks access before returning appointment
records. Inactive staff are additionally visible to their own user and the owner.

## Reproduce

From the repository root, use `/usr/lib/dotnet/dotnet` on the verified local machine
(SDK 8.0.425). Docker must be available for isolated PostgreSQL tests.

```bash
/usr/lib/dotnet/dotnet test tests/AppointmentBooking.IntegrationTests/AppointmentBooking.IntegrationTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~PermissionAcceptanceTests|FullyQualifiedName~StaffAndAccessTests|FullyQualifiedName~StaffEndpointsTests|FullyQualifiedName~BusinessSettingsEndpointsTests|FullyQualifiedName~StaffManagementTelegramTests|FullyQualifiedName~BusinessSettingsTelegramTests|FullyQualifiedName~OwnProfileTelegramTests|FullyQualifiedName~TelegramUserRegistrationTests|FullyQualifiedName~UserIdentityTests|FullyQualifiedName~ScheduleEndpointsTests|FullyQualifiedName~TelegramNavigationTests|FullyQualifiedName~ManagementRetryTests|FullyQualifiedName~BookingLifecycleTests|FullyQualifiedName~BookingEndpointsTests'
/usr/lib/dotnet/dotnet test tests/AppointmentBooking.UnitTests/AppointmentBooking.UnitTests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~AccessServiceTests'
```

Restore dependencies first on a fresh clone. This is focused acceptance, not a full
solution run. Live polling/Swagger demonstration and teammate review remain separate.
Webhook implementation/acceptance is deferred by project decision. Future catalogue
management operations must supply their own permission coverage when integrated.
