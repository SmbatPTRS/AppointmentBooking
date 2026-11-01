# Business rules

## Identity and access

- Create/retrieve Users by unique TelegramUserId (bigint / C# long).
- Simultaneous first visits must resolve to one user.
- One configured owner, identified by Telegram user ID.
- One staff profile per user; a future employee opens the bot before being added.
- Owner manages all staff, shared services and business settings.
- Active employees manage only their own offerings/schedule and view their appointments.
- Clients view/cancel their own eligible bookings.
- Per-user API keys resolve to real application users; one shared admin key must not
  impersonate all roles. Key-to-user configuration is secret configuration.
- Application services enforce permissions regardless of HTTP/Telegram entry point.

## Settings, catalogue and schedules

- Exactly one BusinessSettings record, seeded during setup. Enforce a fixed key
  with a primary key and CHECK id = 1 (this enforces at most one; setup creates it).
- Business settings include display name, staff label, timezone and currency.
- Proposed safe v1 policy: timezone/currency cannot change once bookings exist;
  owner can still change display labels. Confirm this policy before implementing.
- Services are shared types (name/activation). Price/duration belong to StaffService.
- Each staff/service pair is unique. Price >= 0; duration minutes > 0.
- Fixed daily working hours: ONE WorkingHours record per employee, not a separate
  timetable for each weekday. Weekly days off specify non-working weekdays.
- Opening < closing; no overnight shifts in v1.
- Optional break requires both endpoints: opening <= break start < break end <= closing.
- A day-off row specifies EITHER recurring weekday OR specific date, never both/neither.
- No duplicate recurring weekday or specific day off for one employee.
- Schedule edits/day-off changes must not invalidate future confirmed appointments.
  Recurring changes check ALL affected future dates, not just the next weekday.
- Existing booking history is preserved. Deactivate offerings/services/staff rather
  than deleting referenced rows. Existing appointments remain manageable.

## Availability and time

- Use the business timezone for calendar dates, fixed working hours and days off.
- Store booking instants as timestamptz, passing UTC DateTime values to Npgsql.
- Proposed defaults: SlotStepMinutes = 15, MaxAdvanceDays = 30; configurable.
- Define the v1 window as today through today + MaxAdvanceDays, inclusive, in the
  business timezone. Candidate starts must still be strictly later than now.
- Candidate starts are opening + n * SlotStepMinutes. Duration is independent of step.
- The whole appointment must fit working hours and avoid breaks and confirmed bookings.
- Intervals are [start, end); adjacent appointments are allowed.
- Overlap: existing.Start < proposed.End AND existing.End > proposed.Start.
- Skip invalid/ambiguous local starts and ends if daylight-saving transitions occur.
  V1 also rejects intervals crossing an offset change; test this documented policy.
- For a local-date appointment list, convert the day's boundaries to UTC before querying;
  do not assume every local day is 24 hours.

## Booking lifecycle and consistency

- Statuses: Confirmed, Cancelled, Completed. Keep old rows.
- Only active staff, service and staff offering may receive a NEW booking.
- Recheck full availability inside the transaction and employee lock.
- Store accepted price, duration and resulting end instant on Booking.
- Subsequent offering edits never modify existing booking snapshots.
- Confirmation includes the displayed expected price/duration. If current values
  differ, reject with an offering-changed error and obtain a new confirmation.
- Use a RequestId for each confirmed intent. Reuse it for network/transport retries.
- Uniqueness scope chosen here: (ClientUserId, RequestId), enforced in PostgreSQL.
- Same key + same original payload returns the existing booking, even after later
  cancellation or catalogue changes. Same key + different payload is rejected.
- Compare original staff/offering/start/expected price/expected duration, before
  validating current eligibility on a retry. Store all inputs needed for comparison.
- A fresh confirmation after an edited selection receives a new RequestId.
- RequestId uniqueness persists with booking history; it is not a 30-minute cache.
- Confirmed bookings must not overlap for one employee. Different employees may overlap.
- Cancellation releases availability. Check authorization before returning an
  idempotent success for an already-cancelled booking.
- Proposed v1 eligibility: client cancels a confirmed booking only before its start;
  staff/owner completes a confirmed booking only at/after its end. Owner/staff must
  retain access to existing appointments when a profile is deactivated. Confirm
  these boundary choices when agreeing service contracts.
- Do not add staff/owner cancellation of clients' bookings unless explicitly agreed.
- PostgreSQL overlap errors become a specific time-unavailable failure. Other
  database errors are not automatically booking conflicts.

## Telegram

- Polling OR webhook, never both simultaneously.
- Webhook validates X-Telegram-Bot-Api-Secret-Token.
- Polling advances offsets only after handling the corresponding update.
- A business rejection is a handled interaction, not an infinite retry trigger.
- Scope database work per operation; never share a DbContext across updates.
- Serialize a conversation; validate callback ownership, version/nonce and expiry.
- State is keyed by relevant user/chat, expires (suggested 30 minutes), and supports
  Back, Cancel and restart. Acknowledge callback queries.
- Sending failures after commit do not undo a booking. Retries must reuse its request ID.
- No exactly-once message delivery claim. Booking idempotency survives state expiry.
- In-memory state assumes a single application instance and resets on restart.
  Old callbacks after restart must recover safely; replicas need shared state/coordination.
