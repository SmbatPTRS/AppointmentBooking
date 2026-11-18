# Employee offering management overview (Member B)

This block supplies authorized, read-only catalogue inspection for future editing
screens. It does not change offering prices/durations/status, shared-service status,
booking coordination versions, migrations or shared Telegram infrastructure.

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

## Validation and remaining integration

OfferingManagementTests covers authorization through HTTP and direct service calls,
inactive rows, employee isolation, missing/empty resources, fresh terms and public
catalogue filtering. OfferingOverviewTelegramTests covers the UI, page boundaries,
long names, send failures, stale/foreign buttons and conversation restart.

The carried-over availability tests additionally cover catalogue changes and
schedule/booking consistency; see acceptance-tests.md. Test fixtures change entity
state only to establish scenarios; they do not implement management mutations.

Price/duration/status mutation flows and shared-service activation remain pending
A/C agreement on concurrency. Live Telegram transport smoke testing remains separate.
