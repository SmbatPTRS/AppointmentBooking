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


## Telegram owner staff list

The owner's existing Staff management button and `/staff` open the staff list.
`StaffManagementHandler` calls `StaffManagementService.ListAsync` with the trusted
caller and includes inactive profiles. Each entry shows its display name and active
status. Lists use ten profiles per page, with Previous page and Next page buttons,
to respect Telegram text and shared item-mapping limits. An empty list displays
"No staff profiles yet."

Each employee button opens `staff.details` with a server-resolved staff ID. The
details handler rechecks owner access through the staff service and displays the
current name and activation status. The management draft stores the list page and
selected staff ID. Back restores the previous screen and list page. Cancel returns
to the main menu and clears the draft. Previous-screen buttons expire. Direct `/staff` commands from clients
or employees are rejected by the application service, regardless of menu visibility.

The owner can choose Rename from employee details, then submit a display name.
The handler calls StaffManagementService.RenameAsync with the trusted caller and
selected profile. Blank names and names longer than 200 characters leave the prompt
open with Back/Cancel available. Success is sent after the operation completes.
The draft remembers a successful rename so retrying a failed confirmation reply
preserves the original result without applying the rename again. Back reloads the
employee details with the current stored name.

Employee details also offers Deactivate for active profiles and Reactivate for inactive
profiles. The owner confirms on a separate screen, using a server-bound employee button.
Ordinary text does not confirm. Back and Cancel leave activation unchanged.
The handler calls StaffManagementService.SetActiveAsync with the selected employee
and the explicit target status, preserving the service's booking coordination behavior.
After saving, it invalidates the confirmation button and retains the completed result
for a failed-reply retry. Back reloads the employee's current status.

Add employee opens a registered-user lookup. The owner sends the user's numeric
Telegram ID, reviews the registered name/username/ID, and chooses Use this user.
Unregistered IDs and users who already have a staff profile receive a readable
rejection. A new lookup invalidates the previous selection button.

After selecting a user, the owner types the staff display name. The handler calls
StaffManagementService.CreateAsync with that user's internal ID and the trusted
caller. The service saves the active profile and its booking coordination row together.
Invalid names keep the prompt open. Duplicate creation and a user removed since
selection produce recovery messages. Back restores the selected-user preview;
Cancel abandons the flow. A successful creation records the result before replying,
so a failed reply can retry without creating another profile. View employee opens
the new profile's details.

The main menu's Business settings button opens the owner's current business name,
staff label, timezone and currency. Edit business name and staff label accepts a
two-line message, with business name first and staff label second. Edit timezone and
currency accepts timezone ID first and currency second, for example Asia/Yerevan and
AMD. Invalid input leaves the editor open with Back/Cancel available. These handlers
call the same settings operations as HTTP and retain successful results for reply retry.
Any booking, including a cancelled booking, blocks regional edits. Labels remain editable.
Back reloads current settings; Cancel clears the flow and returns to the main menu.

Active employees use Display name from the main menu or `/profile` to edit their own
staff name. The application service resolves the profile from the trusted actor and
rechecks permission on each update/save. Callback IDs and stored selections cannot
redirect this action to another employee. Blank/overlength names leave a usable prompt.
Inactive employees and clients cannot edit profiles. Owner users without their own
staff profile receive an explanatory message and navigation controls. Successful
self-renames retain their result for failed-reply retry.

`/start` displays the caller's Telegram user ID. A prospective employee can share that
ID with the owner, allowing staff creation without database access.

Task 05 automated acceptance covers owner management, employee self-renaming,
role restrictions, invalid input, Back/Cancel, stale/forged/copied buttons, booking
history restrictions and failed Telegram replies. Tests use fake delivery and isolated
PostgreSQL databases. Live Telegram verification and C's review are separate acceptance
steps.
