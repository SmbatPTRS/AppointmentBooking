# Member B schedule HTTP API

All routes use A's ApiKey policy and X-Api-Key header. The authenticated internal
Actor comes from HttpActorResolver; JSON/query values cannot select a caller.
The owner may manage any existing employee, including inactive employees. Other
users may manage only their own active staff profile. Reads use the same policy.

| Method | Route | Success |
| --- | --- | --- |
| GET | /api/staff/{staffId}/schedule | 200 hours object, or JSON null if not configured |
| PUT | /api/staff/{staffId}/schedule | 204 |
| GET | /api/staff/{staffId}/days-off | 200 array of DayOffDto |
| POST | /api/staff/{staffId}/days-off | 201 DayOffDto; Location points to the employee's day-off collection |
| DELETE | /api/staff/{staffId}/days-off/{id} | 204; missing or foreign ID gives 404 |

A schedule is a fixed daily interval in business-local time, not a UTC instant.
Use explicit TimeOnly JSON strings (HH:mm:ss), for example:

```json
{
  "opensAt": "09:00:00",
  "closesAt": "18:00:00",
  "breakStartsAt": "12:00:00",
  "breakEndsAt": "13:00:00"
}
```

Set both break values to null to remove a break. PUT creates or replaces the
employee's working hours; it does not replace days off.

A day off supplies exactly one of weekday (Sunday=0 through Saturday=6) or date:

```json
{"weekday": 0, "date": null}
```

```json
{"weekday": null, "date": "2026-12-25"}
```

Invalid schedule rules return 400 with the shared application `code`.
Missing/invalid credentials return 401; denied access 403; missing employee/day
404; duplicate day or future-booking conflict 409. Malformed JSON and invalid
JSON types use standard ApiController validation responses. Unexpected errors
are handled by the existing exception middleware.

Mutations use ScheduleService's employee lock and authorization after acquisition.
They do not cancel or alter bookings. A booking ending exactly at the proposed
closing time is permitted; shortened hours, overlapping breaks and days off that
invalidate a future confirmed booking are rejected atomically.

Verification (2026-11-09): full Release suite passed: 156 unit tests and 139
integration tests. ScheduleEndpointsTests uses real API keys, AccessService,
PostgreSQL repositories and HTTP requests. It verifies all five routes, owner and
employee flows, inactive/foreign employee rejection, scoped deletion, duplicate
rules, malformed input and identity spoofing. It creates a booking through C's
actual POST /api/bookings and verifies subsequent conflicting schedule requests
return 409 and preserve the stored schedule. No production A/C/D implementation
was modified; Program.cs only adds B's ScheduleService/validator registrations.
