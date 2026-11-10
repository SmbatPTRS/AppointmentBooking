# API and Telegram flows

This document lists HTTP contracts for feature tasks, including routes planned for later work.
The shared identity/error/time conventions are recorded in [shared contracts](shared-contracts.md).
Planned HTTP uses per-user API-key authentication. Telegram uses the verified update sender.
Both invoke the same application services and permission checks.

| Operation | Suggested endpoint | Access |
|---|---|---|
| Read business settings | GET /api/business-settings | Authenticated user |
| Edit business display name and staff label | PUT /api/business-settings | Owner |
| Edit timezone and currency | PUT /api/business-settings/time-zone-and-currency | Owner, before any booking exists |
| Browse staff | GET /api/staff | Authenticated user; active profiles only |
| Browse all staff | GET /api/staff?includeInactive=true | Owner |
| Add staff | POST /api/staff | Owner; existing registered user |
| Rename staff profile | PATCH /api/staff/{staffId} | Owner or employee editing their own active profile |
| Activate or deactivate staff | PUT /api/staff/{staffId}/activation | Owner |
| Browse service types | GET /api/services | Authenticated user |
| Create/edit/deactivate service type | POST /api/services; PATCH /api/services/{serviceId} | Owner |
| Browse staff offerings | GET /api/staff/{staffId}/services | Authenticated user |
| Configure offering | PUT /api/staff/{staffId}/services/{serviceId} | Own active staff profile or owner |
| Read/edit working hours | GET/PUT /api/staff/{staffId}/schedule | Own active staff profile or owner |
| List/add/remove days off | GET/POST /api/staff/{staffId}/days-off; DELETE /api/staff/{staffId}/days-off/{id} | Own active staff profile or owner |
| Find times | GET /api/availability?staffId=...&staffServiceId=...&date=YYYY-MM-DD | Authenticated user |
| Create booking | POST /api/bookings | Client acting as themselves |
| My bookings | GET /api/bookings/mine | Current user |
| Staff appointment list | GET /api/staff/{staffId}/bookings?date=YYYY-MM-DD | Own staff profile or owner |
| Cancel booking | POST /api/bookings/{id}/cancel | Its client, eligible booking |
| Complete booking | POST /api/bookings/{id}/complete | Relevant staff member or owner |
| Telegram update | POST /api/telegram/webhook | Webhook secret, separate from API keys |

Business settings GET returns `displayName`, `staffLabel`, `timeZoneId` and
`currencyCode`. PUT accepts `displayName` and `staffLabel` and returns the updated
settings. The timezone and currency route accepts `timeZoneId` and `currencyCode`
together and returns the updated settings. Once any booking exists, even a
cancelled one, this route returns 409 with code `BusinessSettingsLocked`. The
display name and staff label can still be changed.
Use Swagger's Authorize button
with the API key for an existing registered user. A missing or invalid key returns
401. An authenticated non-owner submitting a valid PUT receives 403.

Staff browsing returns `id`, `displayName` and `isActive`. An authenticated
non-owner requesting `includeInactive=true` receives 403. To create an active
profile, the owner sends `userId` (the registered user's internal UUID) and
`displayName` to `POST /api/staff`. The response is 201 with the created
profile, including its staff profile ID. An unknown user returns 404
(`UserNotFound`), and an existing profile returns 409 (`StaffAlreadyExists`).
The rename route accepts `displayName` and returns the updated profile. An
employee can rename their own active profile. The owner can rename any profile,
including inactive profiles. The activation route requires an explicit boolean
`isActive` and returns the updated profile. Deactivated profiles are omitted
from the active list and remain visible in the owner's management list.

### Try Task 04 in Swagger

Use a local Development instance with the migrated database and a seeded
business settings row. Use three distinct registered users. Configure the owner
ID and one API key per user in the API process environment or the API project's
ignored Development `.env`. Replace the sample Telegram user IDs and key
placeholders with values for your test environment.

```dotenv
Owner__TelegramUserId=111111111
ApiAccess__Keys__0__TelegramUserId=111111111
ApiAccess__Keys__0__Key=replace-with-owner-key
ApiAccess__Keys__1__TelegramUserId=222222222
ApiAccess__Keys__1__Key=replace-with-staff-key
ApiAccess__Keys__2__TelegramUserId=333333333
ApiAccess__Keys__2__Key=replace-with-client-key
```

Use a different random key for each user (for example, generate each with
`openssl rand -hex 32`). Use the Telegram sender's user ID for key
configuration. For staff creation, use the internal UUID from the same user's
row instead. A read-only lookup in the local database is:

```sql
SELECT id, telegram_user_id
FROM users
WHERE telegram_user_id IN (111111111, 222222222, 333333333);
```

Restart the API and open `http://localhost:5080/swagger` with the default
Development launch profile. Under **Authorize**, enter the owner key for
**ApiKey**. Swagger sends it as `X-Api-Key`.

1. As owner, read and edit the business labels. Try the timezone and currency
   update on a test database with no bookings. On a database with booking
   history, expect 409. Create a staff profile for the staff identity if it
   has none, then deactivate and reactivate it. Use
   `GET /api/staff?includeInactive=true` to check its status.
2. Log out of the Swagger authorization dialog and authorize with the staff
   key. With the profile active, rename it using the staff profile ID from
   the list. Try renaming a different existing profile or changing activation
   and expect 403.
3. Switch to the client key. Confirm settings and active staff are readable.
   Request the inactive-inclusive list or submit a valid write and expect 403.
   Clear authorization and confirm a protected request returns 401.

Creation includes StaffServiceId, StartAtUtc, RequestId, ExpectedPrice and
ExpectedDurationMinutes. Resolve the client from Actor, not a client-ID payload.
Return UTC instants in ISO 8601 with Z; include the business timezone for display.
Use YYYY-MM-DD for business-local dates and HH:mm for fixed local schedule times.
Prices are decimal and use the business currency.

HTTP failures: 401 unauthenticated, 403 forbidden, 404 resource not found,
400 malformed/invalid input, 409 overlap/stale offering/request-ID conflict.
Return consistent structured codes and map them to understandable bot messages.
Do not expose stack traces or raw SQL errors.

Client flow: /start -> employee -> service -> date -> available time -> review
price/duration -> confirm -> committed booking details.
Generate RequestId when preparing a confirmation; retries reuse it. New selections
create a new confirmation. Old/expired/other-user buttons cannot bypass validation.

My bookings supports viewing and eligible cancellation. Staff views appointments
by business-local date. Owner management adds/deactivates employees and edits settings.
Staff management handles offerings, fixed hours and weekly/specific days off.
All multi-step flows support Back/Cancel and understandable rejection recovery.

Transport routing:
- Polling service -> shared dispatcher -> feature handler -> application service.
- Webhook controller -> shared dispatcher -> same feature handler -> same service.
- Neither path performs a loopback HTTP call to the application's controllers.
