# Member B: Telegram schedule management

The `/schedule` command and existing Schedule menu button use the shared dispatcher.
An active employee opens their own schedule; the owner selects an employee, including
inactive profiles. Customers cannot open management screens. Staff and day-off lists
use ten items per page, with server-bound item buttons rather than client-supplied IDs.

## Using the screens

- **Edit hours:** send `09:00 18:00`, or `09:00 18:00 13:00 14:00` with a break.
  Both opening and closing time are explicit. `00:00` is accepted. Two times remove
  an existing break. All times are business-local; overnight hours are unsupported.
- **Add day off:** send an ISO date such as `2027-02-28` or an English weekday such
  as `Sunday` (case-insensitive). Numeric weekdays and invalid dates are rejected.
- **Remove <day>:** removes only the selected employee's day off.
- Back restores the previous screen; Cancel returns to the shared main menu.
  Ordinary text on the overview leaves its buttons valid. Invalid form input stays
  on the form, allowing correction or navigation away.

Every mutation calls the existing ScheduleService with the trusted Telegram actor.
The handler does not preload the staff entity in the write update: authorization
runs inside the service after acquiring the staff lock. Existing future-booking
conflicts and duplicate-day rules apply. No booking, access, state, schema or shared
navigation implementation is changed. Program.cs only adds B's handler registrations.

After a successful write, the screen changes to the overview before sending a reply.
An explicit per-delivery completion marker distinguishes a committed write from a
pre-commit failure. A retry of a committed delivery renders current data instead of
repeating the mutation. Failed operations before commit can run again. Entry and page
transitions are similarly marked only after they happen, so retries neither skip an
unapplied transition nor push the same screen twice.
This uses D's in-memory delivery metadata, not durable exactly-once processing.
A process restart loses navigation state; users must reopen `/schedule`.

## Verification

TelegramScheduleTests exercises the registered handlers through the real dispatcher,
real AccessService, PostgreSQL repositories and ScheduleService. Only Telegram delivery
is faked. Cases cover hours/break changes, invalid input correction, date/weekly days
off, duplicates, owner selection, customer denial, deactivation between opening and
saving, Back/Cancel, ordinary text, booking conflicts, pagination, stale buttons and
reply failures after each of the three mutation types commits. Regression cases also
cover failed removal before commit and failed re-entry from an active form.

This branch starts from development independently of availability API PR #23. It adds
schedule screens only; catalogue management and other participants' handlers remain
separate work. A real Telegram smoke test remains to be done with the local bot.
