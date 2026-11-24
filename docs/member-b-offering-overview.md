# Employee offering management overview (Member B)

The original overview supplies authorized catalogue inspection. The offering-terms
feature adds creation and price/duration editing from that screen using B's
OfferingTermsService. Activation, booking coordination versions, migrations and
shared Telegram infrastructure remain separate work.

## HTTP

| Route | Result | Permission |
| --- | --- | --- |
| GET /api/offerings/staff | Employee IDs, names and active flags | Owner: all; active employee: own profile |
| GET /api/staff/{staffId}/services/management | All configured offerings with their terms and independent status flags | Owner or linked active employee |

Both routes require the existing per-user API key. Actor identity comes from the
trusted HTTP resolver. Payload/query/header role hints do not grant access.
A customer without a staff profile is forbidden. An inactive employee cannot use
these management reads; the owner can inspect inactive employees. Missing employees
return the shared staff-not-found error. An existing employee with no offerings
returns an empty JSON array. Application services enforce these rules themselves.

The management response includes offering ID, employee ID, shared-service ID/name,
price, duration and separate offering/service/employee active flags. Prices are
amounts in the business's configured currency. Flags describe current catalogue
state, not slot availability or a reservation guarantee. The existing public GET
/api/staff/{staffId}/services still exposes only fully active offerings.

## Telegram

Type `/offerings`. The owner chooses any employee (including inactive profiles);
an active employee sees only their own profile. The overview shows price, duration,
offering status, shared-service status and employee status. Prices use two decimal
places. Lists contain at most ten employees/offerings per page.

Next/Previous change pages, Refresh reloads persisted data, Back restores the
previous selection page and Cancel uses the shared main-menu navigation. Ordinary
text does not redraw the screen or invalidate its buttons. Direct internal route
commands prompt the user to open /offerings. No changes to A's main menu are needed.

Access is rechecked on every render. Failed sends retry the destination screen
without advancing pages again or adding duplicate history entries. Callback IDs,
revisions and conversation ownership remain enforced by D's dispatcher. After a
restart, old buttons are rejected and the user can reopen /offerings.

## Create and edit offering terms

From an employee overview, choose **Create offering**, select a shared service and
send price plus duration, for example `25.50 30`. Selection includes active shared
services without an existing offering for that employee; disabled offerings also
count as existing. Use **Edit <service name>** to change a configured offering's
price/duration, including a disabled offering. Both values must be supplied together.

Use a decimal point for price and whole positive minutes for duration. Free offerings
are valid (`0 30`). Prices with more than two meaningful decimal places or above
9999999999.99 are rejected. Invalid input leaves the form open for correction.
Back returns to the selection/overview and Cancel leaves the flow.

After saving, the confirmation shows the committed terms. **View offerings** refreshes
the current catalogue, so later edits are visible. If the confirmation send fails,
retry renders the stored result and navigation; it does not repeat creation or
replace a later HTTP edit. These are in-memory delivery guarantees within the
existing dispatcher, not durable recovery after restart.

## Validation and remaining integration

OfferingManagementTests covers authorization through HTTP and direct service calls,
inactive rows, employee isolation, missing/empty resources, fresh terms and public
catalogue filtering. OfferingOverviewTelegramTests covers the UI, page boundaries,
long names, send failures, stale/foreign buttons and conversation restart.

The carried-over availability tests additionally cover catalogue changes and
schedule/booking consistency; see acceptance-tests.md. Test fixtures change entity
state only to establish scenarios; they do not implement management mutations.

OfferingTermsTelegramTests additionally covers create/edit forms, price parsing,
validation, ownership, inactive staff, duplicate creation, selection pagination and
failed sends before/after writes. All writes use the same service as HTTP; see
member-b-catalog-api.md for routes and numeric limits.

Offering/shared-service activation remains pending A/C agreement on eligibility
coordination. OfferingTermsConcurrencyTests exercises real BookingService/OfferingTermsService
operations in separate scopes under the existing employee lock: both lock orders
for price, duration and simultaneous term changes; duplicate offering creation;
fresh authorization after a waiting employee is deactivated; cancellation while
waiting. A pausing wrapper retains the real PostgreSQL lock. The test observes
that the second backend is blocked by the specific holder PID before release.
All coordinated operations have bounded cancellation and settle before disposal.
Stored rows prove snapshot preservation or rejection of stale confirmation.
This is price/duration serialization, not acceptance of global eligibility edits.
Live Telegram transport smoke testing remains separate.
