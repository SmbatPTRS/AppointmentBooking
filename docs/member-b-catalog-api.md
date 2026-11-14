# Member B: catalogue HTTP API

These routes expose the existing CatalogService through the API-key policy used
by the rest of the application. A configured key identifies the caller; request
body fields and arbitrary identity headers cannot grant owner privileges.

| Route | Result | Access |
| --- | --- | --- |
| GET /api/services | 200, active shared service types | Authenticated user |
| GET /api/staff/{staffId}/services | 200, active employee offerings | Authenticated user |
| POST /api/services | 201, new active service type | Owner |

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

## Browsing

Service types contain no prices. Offering results include the offering ID, employee
ID, service ID/name, employee-specific price and duration, and active flag. The
employee, shared service, and offering must all be active. Another employee's
prices never appear in the selected employee's response.

Unknown or inactive employees return an empty array, matching CatalogService.
An empty UUID returns 400; a malformed UUID does not match the route and returns 404.
Reads use current database state on every request. The lists are not a reservation
or a substitute for C's final booking checks under the employee lock.

## Verification and remaining work

CatalogEndpointsTests runs the real host, API-key authentication, AccessService,
CatalogService and PostgreSQL repositories. It covers all three route permissions,
owner impersonation attempts, creation/persistence, validation boundaries, active
filters, employee isolation, current database state and Location reachability.

This block does not implement service editing/deactivation or staff-offering
configuration. Those remain B work after the shared lock/version rules are settled.
No entity, migration, booking, authorization or Telegram infrastructure code changes.
