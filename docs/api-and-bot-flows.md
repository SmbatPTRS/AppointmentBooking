# API and Telegram flows

These are proposed contracts for the feature tasks, not endpoints already implemented
by the bootstrap. Agree DTO names and error codes before coding independently.
The shared identity/error/time conventions are recorded in [shared contracts](shared-contracts.md).
Planned HTTP uses per-user API-key authentication. Telegram uses the verified update sender.
Both invoke the same application services and permission checks.

| Operation | Suggested endpoint | Access |
|---|---|---|
| Read business settings | GET /api/business-settings | Authenticated user |
| Edit business settings | PUT /api/business-settings | Owner |
| Browse staff | GET /api/staff | Authenticated user; client view filters active |
| Add staff | POST /api/staff | Owner; existing registered user |
| Edit staff name/activation | PATCH /api/staff/{staffId} | Owner; own name change may be allowed explicitly |
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
