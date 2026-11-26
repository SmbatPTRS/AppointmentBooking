using System.Globalization;
using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>B's management overview; terms forms delegate writes to OfferingTermsService.</summary>
public sealed class OfferingOverviewHandler(string route, OfferingManagementService offerings,
    ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public static readonly IReadOnlyList<string> Routes =
        ["offerings", "off.staff", "off.next", "off.prev", "off.refresh", "off.people.next", "off.people.prev"];
    internal const string Feature = "off.overview";
    private const string Transition = "off.overview.transition";
    private const int PageSize = 10;
    public string Route => route;
    public sealed record Draft(Guid StaffId = default, int Page = 0);

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var command = context.Text?.Split(' ', 2)[0].Split('@', 2)[0];
        if (!context.IsNavigation && !context.IsRetry && context.Callback is null && command != "/offerings")
        {
            if (command?.StartsWith('/') == true)
                await sender.SendTextAsync(context.ChatId, "Open /offerings to choose an employee.", ct);
            return;
        }
        var state = context.State;
        if (!context.IsRetry) state.ClearFeatureData(Transition);
        var transitioned = state.GetFeatureData<bool>(Transition);
        // Recheck access on every rendering, including retries and Back.
        var employees = await offerings.ListStaffAsync(context.Caller.Actor, ct);
        if (Route == "offerings" || Route.StartsWith("off.people.", StringComparison.Ordinal))
        {
            if (!context.IsNavigation && !transitioned && !context.IsRetryRender)
            {
                if (Route == "offerings")
                {
                    state.EnterFlow("offerings", clock.UtcNow);
                    state.SetFeatureData(Feature, new Draft());
                }
                else
                {
                    var previous = state.GetFeatureData<Draft>(Feature) ?? new();
                    state.BeginStep("offerings", clock.UtcNow);
                    state.SetFeatureData(Feature, previous with
                    {
                        Page = Math.Max(0, previous.Page + (Route.EndsWith("next") ? 1 : -1))
                    });
                }
                state.SetFeatureData(Transition, true);
            }
            var draft = state.GetFeatureData<Draft>(Feature) ?? new();
            var page = ClampPage(draft.Page, employees.Count);
            state.SetFeatureData(Feature, draft with { Page = page });
            var buttons = employees.Skip(page * PageSize).Take(PageSize)
                .Select(s => state.ItemButton(s.DisplayName + (s.IsActive ? "" : " (inactive)"), "off.staff", s.Id))
                .ToList();
            AddPages(buttons, page, employees.Count, "employees", "off.people", state);
            buttons.AddRange(NavigationKeyboard.Create(state));
            await sender.SendMenuAsync(context.ChatId, employees.Count == 0 ? "No employees yet."
                : $"Choose an employee · page {page + 1} of {PageCount(employees.Count)}.", buttons, ct);
            return;
        }

        var current = state.GetFeatureData<Draft>(Feature);
        if (Route == "off.refresh" && state.Step == "off.saved" && !context.IsNavigation && !context.IsRetryRender)
        {
            // View offerings finishes the form: restore the existing overview ancestry
            // instead of leaving a second overview/service picker behind Back.
            while (state.Step != "off.staff" && state.CanGoBack) state.GoBack(clock.UtcNow);
            current = state.GetFeatureData<Draft>(Feature) ?? current;
            if (state.Step != "off.staff") state.BeginStep("off.staff", clock.UtcNow);
            state.SetFeatureData(Transition, true);
            transitioned = true;
        }
        if (Route == "off.staff" && context.ItemId is { } selected &&
            !context.IsNavigation && !context.IsRetryRender && !transitioned)
        {
            // The service enforces ownership even if a caller manages to supply another ID.
            await offerings.ListAsync(context.Caller.Actor, selected, ct);
            state.NavigateTo("off.staff", clock.UtcNow);
            current = new Draft(selected);
            state.SetFeatureData(Feature, current);
            state.SetFeatureData(Transition, true);
        }
        if (current is null || current.StaffId == Guid.Empty)
        {
            await sender.SendTextAsync(context.ChatId, "Open /offerings to choose an employee.", ct);
            return;
        }
        var rows = await offerings.ListAsync(context.Caller.Actor, current.StaffId, ct);
        var employee = employees.SingleOrDefault(s => s.Id == current.StaffId)
            ?? throw new AppException(ErrorCodes.Forbidden, "Employee access is required.", AppErrorKind.Forbidden);
        if (Route is "off.next" or "off.prev" or "off.refresh" &&
            !context.IsNavigation && !context.IsRetryRender && !transitioned)
        {
            state.BeginStep("off.staff", clock.UtcNow);
            current = current with { Page = Math.Max(0, current.Page + (Route == "off.next" ? 1 : Route == "off.prev" ? -1 : 0)) };
            state.SetFeatureData(Transition, true);
        }
        var selectedPage = ClampPage(current.Page, rows.Count);
        state.SetFeatureData(Feature, current with { Page = selectedPage });
        var lines = rows.Skip(selectedPage * PageSize).Take(PageSize).Select(row =>
            $"{row.ServiceName}\nPrice: {row.Price.ToString("0.00", CultureInfo.InvariantCulture)} · Duration: {row.DurationMinutes} min\n" +
            $"Offering: {Status(row.IsActive)} · Service: {Status(row.ServiceIsActive)}");
        var text = $"Employee offerings: {employee.DisplayName}\nEmployee: {Status(employee.IsActive)}\n" +
            (rows.Count == 0 ? "No offerings configured." : $"Page {selectedPage + 1} of {PageCount(rows.Count)}\n\n" + string.Join("\n\n", lines));
        List<TelegramMenuButton> actions = [];
        AddPages(actions, selectedPage, rows.Count, "offerings", "off", state);
        actions.AddRange(rows.Skip(selectedPage * PageSize).Take(PageSize)
            .Select(row => state.ItemButton("Edit " + row.ServiceName, "off.edit", row.ServiceId)));
        actions.Add(state.Button("Create offering", "off.add"));
        actions.Add(state.Button("Refresh", "off.refresh"));
        actions.AddRange(NavigationKeyboard.Create(state));
        await sender.SendMenuAsync(context.ChatId, text, actions, ct);
    }

    private static string Status(bool active) => active ? "active" : "inactive";
    private static int PageCount(int count) => Math.Max(1, (count + PageSize - 1) / PageSize);
    private static int ClampPage(int page, int count) => Math.Clamp(page, 0, PageCount(count) - 1);

    private static void AddPages(List<TelegramMenuButton> buttons, int page, int count,
        string label, string prefix, AppointmentBooking.Api.Telegram.State.ConversationState state)
    {
        if (page > 0) buttons.Add(state.Button($"Previous {label}", prefix + ".prev"));
        if ((page + 1) * PageSize < count) buttons.Add(state.Button($"Next {label}", prefix + ".next"));
    }
}
