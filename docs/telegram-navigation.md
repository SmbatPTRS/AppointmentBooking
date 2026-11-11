# Shared Telegram navigation — Task 22 handoff

## The agreement for A, B and C

Use the existing scoped `ITelegramHandler`. Give each screen a distinct route of at
most 20 characters. Keep `back`, `cancel` and `start` for shared navigation.
Register feature handlers as scoped as before. They receive the trusted
`context.Caller.Actor`; pass it to application services on every protected operation.
An item ID is a selection, never permission to manage that item.

The dispatcher holds the user/chat lease across callback validation, actor resolution,
handler execution and the awaited reply. Do not acquire a second lease inside a handler.
Another user or chat can proceed independently. One application instance only.

## Entering, moving forward, returning and cancelling

- `state.EnterFlow("staff", clock.UtcNow)` clears the previous flow and enters its
  first screen. Use this when the user explicitly opens/re-enters a feature.
- `state.NavigateTo("staff.details", clock.UtcNow)` saves the current screen before
  entering the next one. Call it **before** changing selections for the next screen.
- `/back` or a shared Back button restores the previous screen, selections, typed
  fields and confirmation identity. It creates a new revision and dispatches the
  restored screen's handler with `IsNavigation = true`, `Text = null`, `Callback = null`.
  Render that screen without pushing another history entry or treating Back as input.
- `/cancel`, `/start`, Cancel and Main menu buttons clear all feature fields,
  selections, confirmation identity, item mappings and history, then render `start`.
- With no history/state after expiry or restart, Back safely returns to `start`.
- Each forward/back/reset transition invalidates previous buttons. History stores
  values, not old buttons or their revisions. A maximum of 32 previous screens is kept.

`BeginStep` remains compatible with earlier handlers: it changes the screen/revision
without adding history. Use `NavigateTo` for screens that need step-by-step Back.
Never change screens solely to retry a failed Telegram send.

## Feature-owned typed fields

A owns management route names and draft record definitions in its feature handler
folder. B/C own their respective records. The shared state stores serialized JSON values,
so callers cannot accidentally modify a saved screen through a mutable object reference.
Keep drafts small and limited to IDs, enums, strings and other scalar value data.
Do not put database entities, contexts, scopes or services in a draft.

```csharp
public sealed record ManagementDraft(Guid StaffId, string? PendingName = null);

context.State.SetFeatureData("management", new ManagementDraft(selectedStaffId));
var draft = context.State.GetFeatureData<ManagementDraft>("management");
context.State.SetFeatureData("management", draft! with { PendingName = context.Text });
```

Use distinct feature keys such as `management`, `catalog`, `schedule`, `booking`.
Back restores the previous snapshot: pending rename input added on the rename screen
disappears when returning to details. Cancel/re-entry/expiry/restart discard every draft.
`ClearFeatureData(feature)` can clear a feature explicitly. Existing scalar `Selections`
remains available and is included in screen snapshots too.

## Item buttons without IDs supplied by the client

```csharp
var select = context.State.ItemButton("Ada", "staff.details", staff.Id);
var rename = context.State.Button("Rename", "staff.rename");
var navigation = NavigationKeyboard.Create(context.State); // Back and Cancel
```

These methods register allowed actions and return `TelegramMenuButton` values.
The item button carries `2:{revision:N}:{route}:{shortKey}`. Its route, key and Guid ID
are bound together in server-side state. The dispatcher verifies sender/chat, current
revision, allowed route and exact key/route mapping, then supplies `context.ItemId`.
Old version-1 route buttons remain supported. An item-only action cannot be forged by
removing its key and sending a version-1 route callback. Never enable the same item-only
route with a plain `Button` or `SetAllowedCallbacks` entry.

Callbacks are at most 64 UTF-8 bytes: a 20-character route and 8-character item key
fit exactly. There are at most 256 distinct route/item mappings per screen; paginate
larger lists. Rendering the same route/item pair again reuses its existing key, even
at the limit. Labels can change without allocating another mapping. A new screen
revision clears these mappings, so earlier-screen buttons still fail validation.
Raw UUIDs and arbitrary user fields are not added to callback payloads.

The dispatcher owns all callback acknowledgements. Feature handlers must not acknowledge
them a second time. Invalid, copied, stale and expired buttons get readable rejections.
The specific terminal Telegram expired-query error still ends that click without
blocking polling; other delivery failures retain polling's retry behavior.

## An executable example for A

See `tests/AppointmentBooking.IntegrationTests/Fixtures/ManagementNavigationExample.cs`.
It is test-only and is not registered in the live bot. It includes three small handlers:

1. `/demo.staff` enters the flow and renders an employee item button.
2. `demo.details` reads the server-resolved `context.ItemId`, pushes the list screen,
   saves `ManagementDraft`, and renders Rename, Back and Cancel.
3. `demo.rename` pushes details and asks for text. Text updates the draft's pending
   name. Back restores details with the same employee and no pending name. Cancel
   returns to the main menu and clears the draft.

The example previews input; it does not rename a real employee. A's real handler calls
`StaffManagementService` with `context.Caller.Actor` and the selected staff ID. Validate
input and recheck current application permissions when saving.

## Retrying replies and confirmations

If a handler fails, the dispatcher retains the exact update's original route and
resolved item selection in that conversation. Retrying that same update gets
`IsRetry = true`, even if the original handler changed the screen revision before
the send failed. A different update cannot use this exception to bypass old-button
validation. The next different delivery replaces pending retry metadata.

Handlers must avoid pushing history, resetting drafts or generating a new confirmation
on retry when that transition was already applied. The example checks both `IsRetry`
and `State.Step`: a failure before a transition still needs to apply it. It then
rebuilds/sends the current screen. Back has its own pending-render marker so a failed
Back reply cannot pop history twice on a retry.

`ConfirmationRequestId` is retained on normal delivery retry and in screen snapshots.
Keep it for the same original booking payload. C must explicitly create a new intent
when the client changes that payload, and pass the original RequestId for a retry
after commit. Clearing/expiring in-memory state never authorizes reconstruction of an
old unsafe confirmation; old buttons fail. Durable booking idempotency is C's database
guarantee. This layer does not promise exactly-once Telegram replies or rollback a
committed business action if sending its result fails.

State expires 30 minutes after entering a screen. On acquisition, expired state is
replaced. Expired idle state/locks are removed opportunistically on later acquisitions.
Active/queued operations retain their entry until safely released. There is no distributed
state; restart loses drafts/history and safely rejects the old buttons.

## Verification and adoption

`ConversationStateStoreTests`, `CallbackDataParserTests` and `TelegramNavigationTests`
cover restoration/clearing, typed-value copying, key format/limits, forged/cross-user
buttons, expiry/restart/re-entry, queued operation cleanup, concurrent clicks,
independent conversations, confirmation retry and failed screen/Back sends.
All Telegram delivery in these tests uses fake adapters; no real messages are sent.

The shared implementation is not the complete acceptance claim for Task 22. A/B/C must
adopt it in their own feature screens, and B must review the handoff. Actual booking
commit/send-failure acceptance remains part of the integrated Task 23 work.

November 11 verification: 167 unit tests and 83 integration tests passed in the initial
assistant run, including all 17 navigation tests. After Docker became available, all 55
previously setup-failing database tests passed on rerun, with no failures or skipped tests.
Together the runs cover all 305 tests (167 unit and 138 integration) on the same code.
Database retry results: task22-database-retry.trx in the ignored integration TestResults folder.

Review follow-up: item-button redraw regression verified with a 256-item screen, repeated
renders, changed labels, route isolation and failed-send retry retaining keys/confirmation.
The regression failed with the original capacity exception before the fix. After reusing
existing route/item bindings, the full suite passed: 169 unit + 139 integration = 308 tests,
zero failures/skips. New screens still clear mappings and reject previous revisions.
