# Member B availability HTTP API

GET `/api/availability?staffId={guid}&staffServiceId={guid}&date=2026-11-10`
requires a registered user's `X-Api-Key`, using A's shared authentication policy.
A customer can view any employee's active offering; management privileges are not
required to browse. This read operation performs no reservation or mutation.

## Request and response

Both identifiers must be present and nonempty. `staffServiceId` identifies the
employee-specific offering, not the shared service type. `date` must be exactly
`YYYY-MM-DD` and is interpreted in the configured business timezone. Timestamp
strings, locale-specific dates and impossible dates are rejected.

Successful responses are ordered arrays of AvailableSlot objects:

```json
[
  {
    "startsAtUtc": "2026-11-10T05:00:00+00:00",
    "endsAtUtc": "2026-11-10T05:40:00+00:00"
  }
]
```

The example represents 09:00–09:40 in Asia/Yerevan. Actual times depend on the
selected employee's hours, offering duration, breaks, days off and bookings.
Starts follow opening + n × SlotStepMinutes. The whole interval must fit. An end
exactly at closing and intervals touching existing bookings are allowed.

## Errors and empty results

- 401: missing or invalid API key.
- 400: missing/invalid query input, mismatched employee/offering, or date outside
  the configured booking window. Application errors include the shared `code`.
  Invalid GUID syntax uses standard ApiController model-binding errors.
- 404: employee or offering not found.
- 200 with `[]`: valid date with no available intervals, including inactive
  employee/service/offering, missing hours, days off or insufficient time.

A previously displayed interval can become unavailable. C's booking service must
still perform its final validation under the existing transaction and staff lock.
No new locking scheme, booking mutation or domain model is introduced here.

## Verification

AvailabilityEndpointsTests exercises the real HTTP host, authentication,
PostgreSQL repositories and AvailabilityService with a fixed clock. It covers
query validation, UTC output, opening grid, breaks, closing, booking-window
boundaries, inactive resources, missing schedules and both day-off types.
Booking creation uses C's real HTTP endpoint; cancellation uses C's real service
(the cancellation HTTP adapter is not present in this branch's development base).
Subsequent GET requests verify occupied intervals disappear and return after
cancellation, without restarting the host. Another employee's booking does not
hide the selected employee's slots.

DST policy remains in the shared B calculator and its existing unit tests; this
HTTP block does not introduce independent timezone conversion rules.
