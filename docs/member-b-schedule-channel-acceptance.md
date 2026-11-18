# Schedule acceptance across Telegram and HTTP (Member B)

This test block verifies that B's two schedule adapters operate on the same
persisted schedule and produce consistent availability. It adds no production
features and changes no booking, authorization, schema or Telegram transport code.

## Test setup

ScheduleChannelScenario starts one application host with a fake Telegram sender,
the real dispatcher, real access service, real repositories and PostgreSQL. HTTP
clients use separate configured employee/customer API keys. Each dispatched update
and HTTP request gets its normal service scope. Booking requests use C's registered
BookingService through its HTTP endpoint.

The clock is fixed at 2026-11-09 00:00 UTC, the selection window is 30 days and the
slot step is 15 minutes. Each test gets unique users, staff and offerings. Business
settings are arranged directly as fixture data and restored during disposal. These
tests must stay in the serialized PostgresCollection because settings are shared.
Fixture setup does not exercise A's settings-edit workflow or coordination policy.

## Coverage

| Test class | Acceptance evidence |
| --- | --- |
| ScheduleChannelTests | Exact slots for business-local hours; Yerevan/Los Angeles date conversion; HTTP changes reflected in Telegram; break removal; invalid input preserves data |
| DayOffChannelTests | Date/weekly days off created in one adapter and removed in the other; duplicate rejection; overlapping rules; invalid input correction; old removal buttons after HTTP deletion |
| ScheduleBookingChannelTests | Conflicting opening/closing/break/day-off edits reject in both adapters; cancellation permits the edit; snapshots remain intact; changed start grid and exact closing boundary; bookings outside the current window |
| ScheduleDeliveryChannelTests | Failed replies after commit; HTTP edits between retries; repeated send failures; no replayed writes; recovered buttons bind current IDs; validation and Back recovery; access checked again after deactivation |

Failures after commit are verified by reading through HTTP before retrying Telegram.
Later HTTP changes deliberately differ from the original Telegram input: a replayed
write would overwrite them or recreate/delete the wrong row. Tests inspect IDs,
booking snapshots and available slots as appropriate, not only success messages.

## Reproducible checks

Run from the repository root with the documented SDK and Docker available:

```bash
dotnet test tests/AppointmentBooking.IntegrationTests -c Release --filter 'FullyQualifiedName~ScheduleChannelTests|FullyQualifiedName~DayOffChannelTests|FullyQualifiedName~ScheduleBookingChannelTests|FullyQualifiedName~ScheduleDeliveryChannelTests'
dotnet test AppointmentBooking.sln -c Release --nologo
```

## Manual demonstration after merge

Use a test employee and dates inside the configured booking window.

1. Open `/schedule` as that employee and save hours and a break. Read the HTTP
   schedule and availability for the same business-local date.
2. Change the hours through HTTP and reopen `/schedule`; confirm the same times.
3. Add a specific day off in Telegram; availability becomes empty. Delete that
   day through HTTP and confirm slots return.
4. Add both a weekly and specific day off. Remove one; the covered date must stay
   closed until the other is removed.
5. Create a customer booking. Attempt to move closing into its interval or add a
   day off; both adapters must preserve the accepted booking and original schedule.
6. Cancel through the customer's booking API and repeat the schedule edit.
7. Verify dates displayed to the user are business-local while API booking/slot
   timestamps identify the same instants in UTC.

Use automated fault injection for failed sends; do not simulate production failures
by changing shared transport code or exposing real bot credentials.

## Limits and remaining work

These tests do not contact Telegram, verify polling/webhook delivery, or prove
recovery after process restart. Retry metadata is the existing in-memory state;
there is no new durable exactly-once guarantee. A live bot smoke test remains.

Offering mutation lock ordering, shared-service activation and price/duration/status
editing still require the A/C agreement. This block supplies no alternative protocol.
The outstanding catalogue management work is not marked complete by these tests.
