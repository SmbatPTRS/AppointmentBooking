# Member B: catalogue HTTP API

These routes expose B's catalogue services through the API-key policy used
by the rest of the application. A configured key identifies the caller; request
body fields and arbitrary identity headers cannot grant owner privileges.

| Route | Result | Access |
| --- | --- | --- |
| GET /api/services | 200, active shared service types | Authenticated user |
| GET /api/staff/{staffId}/services | 200, active employee offerings | Authenticated user |
| POST /api/services | 201, new active service type | Owner |
| PATCH /api/services/{serviceId} | 200, renamed service type | Owner |
| POST /api/staff/{staffId}/services | 201, new employee offering | Owner or linked active employee |
| PUT /api/staff/{staffId}/services/{serviceId}/terms | 200, updated price/duration | Owner or linked active employee |

## Create a service

Send `{"name":"Haircut"}` with the existing API-key header. Name is required and
cannot be blank. CatalogService trims surrounding whitespace and enforces a maximum
of 200 characters after trimming. The response contains `id`, `name`, and `isActive`.
Its Location points to `/api/services`, the available catalogue collection resource.
There is no new GET-by-id route. Creating a service does not create a staff offering.

Invalid JSON, missing/null/blank names, wrong property types, and overlong trimmed
names return 400 without adding a row. Missing/invalid API keys return 401. Valid
non-owner identities receive 403 when submitting a valid creation request. HTTP
binding errors use the standard validation response; application errors retain the
existing middleware's code and message format.

## Rename a service

Send `{"name":"New name"}` to `PATCH /api/services/{serviceId}`. The same trimmed name
rules apply. An owner can rename either an active or inactive service. The response
returns the updated service with its existing activation status. Missing services
return 404. The request accepts only `name`, so sending `isActive` returns 400.
Existing offerings keep their service reference, price and duration.

## Browsing

Service types contain no prices. Offering results include the offering ID, employee
ID, service ID/name, employee-specific price and duration, and active flag. The
employee, shared service, and offering must all be active. Another employee's
prices never appear in the selected employee's response.

Unknown or inactive employees return an empty array, matching CatalogService.
An empty UUID returns 400; a malformed UUID does not match the route and returns 404.
Reads use current database state on every request. The lists are not a reservation
or a substitute for C's final booking checks under the employee lock.

## Employee offering terms

Creation accepts `{"serviceId":"<shared-service UUID>","price":0,"durationMinutes":30}`.
`serviceId` identifies the shared service type, not an offering. All three fields
must be present. Zero price is valid; an omitted price does not default to free.
A successful response contains StaffServiceDto, including the new offering ID;
Location points to the employee's protected management catalogue. Creation requires a currently active shared service and makes an active offering.
An inactive shared service without an existing pair returns 409 with code
`service.inactive` for both owner and employee; no offering is added. An existing pair (including a disabled offering) returns 409
with code `staff_service.already_exists`, leaving its original terms intact.

Updating accepts `{"price":25.50,"durationMinutes":45}`. Both fields are required;
PUT replaces them together and returns the existing offering with the updated
terms. It does not create a missing offering. The route uses the shared service
ID within the employee's catalogue. Offering and employee IDs cannot be changed
through the body. Unknown fields, including `isActive`, return 400.

Price must be between 0 and 9999999999.99 with at most two meaningful decimal
places; 5.2500 is valid, 1.001 is not. Duration must be a positive whole number of
minutes. Invalid values return the shared application validation error. Missing
services/offerings return 404; denied management access returns 403. An existing offering's terms remain editable when its shared service is inactive;
this does not reactivate the shared service or make the offering publicly visible.
The owner may edit terms for inactive employees. A linked inactive employee cannot do so.

OfferingTermsService acquires the existing employee transaction lock, reloads staff
before authorization and reloads an already tracked offering before editing it.
The same lock protects C's final price/duration check during booking. Existing
booking snapshots and offering activation status are preserved. Completion is
returned only after commit. Activation and shared-service eligibility changes
remain a separate coordination task.

## Verification and remaining work

CatalogEndpointsTests runs the real host, API-key authentication, AccessService,
CatalogService and PostgreSQL repositories. It covers route permissions,
owner impersonation attempts, creation/persistence, validation boundaries, active
filters, employee isolation, database state and Location reachability. Rename tests
also check that offering terms and activation status remain intact.

OfferingTermsEndpointsTests covers creation/update, required and unexpected fields,
price boundaries, duplicate handling, Location reachability, role checks and
employee isolation. OfferingTermsServiceTests additionally verifies accepted booking
snapshots and stale tracked-state handling.

Service/offering activation changes still depend on the agreed eligibility/version
rules. Telegram forms use the same service. Joint booking/edit acceptance covers both lock
orders for price/duration updates; see member-b-offering-overview.md.
No entity, migration, booking, authorization or Telegram infrastructure code changes.
