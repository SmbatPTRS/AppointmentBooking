# Member B: Telegram catalogue browsing

Updated 2026-11-09 after integration with development.

The host registers CatalogService and the scoped CatalogHandler under route
`catalog`. Send `/catalog` to view active shared service names. The screen sends
at most ten names per message, explains that terms depend on the employee, and
provides a revision-checked Main menu callback. An empty catalogue has a dedicated
message. No staff/offering editing or booking creation is performed by this route.

The shared dispatcher resolves the trusted Telegram caller through UserService.
Production IAccessService comes from A's merged host registration; no permissive
access implementation is installed. The only shared-host changes for this feature
are the two B service registrations. D's dispatcher, menus and transport are unchanged.
The main menu does not yet advertise this command; users can type `/catalog`.

Verification: `dotnet test AppointmentBooking.sln -c Release --nologo` passed with
156 unit tests and 123 integration tests. Coverage includes real host resolution,
dispatch to the catalogue using PostgreSQL data, return to Start in a fresh scope,
active-only names, empty catalogues and long names split into messages. Telegram
sending is replaced by the test sender. A real-bot smoke test remains to be run
with the user's configured polling instance; no bot credentials were changed.

Remaining B work: employee offering selection/display, catalogue management,
schedule HTTP/Telegram adapters and joint acceptance of booking/schedule behavior.
The separate feature/erik-schedule-access branch still needs integration and review.
