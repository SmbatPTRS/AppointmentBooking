using System.Globalization;
using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Scheduling;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>B's schedule screens; shared dispatch, navigation and authorization stay with D/A.</summary>
public sealed class ScheduleHandler(string route, ScheduleService schedules, IAccessService access,
    IStaffRepository staff, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public static readonly IReadOnlyList<string> Routes =
        ["schedule", "sch.view", "sch.hours", "sch.add", "sch.remove", "sch.staff.next", "sch.staff.prev", "sch.days.next", "sch.days.prev"];
    private const string Feature = "schedule";
    private const int PageSize = 10;
    private const string Delivery = "schedule.delivery";
    public sealed record DeliveryProgress(bool Transitioned = false, bool Committed = false);
    public string Route => route;
    public sealed record Draft(Guid StaffId = default, int Page = 0);

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (!context.IsRetry) state.ClearFeatureData(Delivery);
        var progress = state.GetFeatureData<DeliveryProgress>(Delivery) ?? new();
        // Screen identity alone is not proof of commit: removal starts on the overview.
        if (context.IsRetry && progress.Committed)
        {
            await RenderDetails(context, ct);
            return;
        }
        if (Route == "schedule" || Route.StartsWith("sch.staff."))
        {
            if (!context.IsNavigation && !context.IsRetry && context.Callback is null &&
                context.Text?.Split(' ', 2)[0].Split('@', 2)[0] != "/schedule") return;
            await Open(context, ct);
            return;
        }
        var draft = state.GetFeatureData<Draft>(Feature);
        if (draft is null) { await sender.SendTextAsync(context.ChatId, "Open /schedule first.", ct); return; }

        if (Route == "sch.view")
        {
            if (context.ItemId is { } selected && !context.IsNavigation && !progress.Transitioned)
            {
                await access.RequireStaffManagementAsync(context.Caller.Actor, selected, ct);
                state.NavigateTo(Route, clock.UtcNow);
                state.SetFeatureData(Feature, new Draft(selected));
                state.SetFeatureData(Delivery, new DeliveryProgress(Transitioned: true));
            }
            else if (!context.IsNavigation && !context.IsRetry) return; // Ignore ordinary overview text.
            await RenderDetails(context, ct);
            return;
        }
        if (Route.StartsWith("sch.days."))
        {
            if (context.Callback is null) return;
            if (!progress.Transitioned)
            {
                state.BeginStep("sch.view", clock.UtcNow);
                state.SetFeatureData(Feature, draft with { Page = Math.Max(0, draft.Page + (Route.EndsWith("next") ? 1 : -1)) });
                state.SetFeatureData(Delivery, new DeliveryProgress(Transitioned: true));
            }
            await RenderDetails(context, ct);
            return;
        }
        if (draft.StaffId == Guid.Empty) return;
        if (Route is "sch.hours" or "sch.add")
        {
            if (context.Callback is not null && !context.IsNavigation &&
                !progress.Transitioned)
            {
                state.NavigateTo(Route, clock.UtcNow);
                state.SetFeatureData(Delivery, new DeliveryProgress(Transitioned: true));
            }
            if (context.IsNavigation || context.Callback is not null || context.Text?.StartsWith('/') != false)
            {
                await access.RequireStaffManagementAsync(context.Caller.Actor, draft.StaffId, ct);
                await Prompt(context, null, ct);
                return;
            }
        }
        else if (Route != "sch.remove" || context.ItemId is null) return;

        try
        {
            // Do not preload staff here: ScheduleService checks fresh permissions after the lock.
            if (Route == "sch.hours")
                await schedules.SetHoursAsync(context.Caller.Actor, draft.StaffId, ParseHours(context.Text!), ct);
            else if (Route == "sch.add")
                await schedules.AddDayOffAsync(context.Caller.Actor, draft.StaffId, ParseDay(context.Text!), ct);
            else
                await schedules.RemoveDayOffAsync(context.Caller.Actor, draft.StaffId, context.ItemId!.Value, ct);
        }
        catch (AppException error) when (error.Kind is AppErrorKind.Validation or AppErrorKind.Conflict)
        {
            await Prompt(context, error.Message, ct);
            return;
        }
        // Mark completion before the first Telegram send. In-memory retries cannot repeat this write.
        state.BeginStep("sch.view", clock.UtcNow);
        state.SetFeatureData(Feature, draft with { Page = 0 });
        state.SetFeatureData(Delivery, new DeliveryProgress(Committed: true));
        await RenderDetails(context, ct);
    }

    private async Task Open(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        var transitioned = state.GetFeatureData<DeliveryProgress>(Delivery)?.Transitioned == true;
        var owner = true;
        try { await access.RequireOwnerAsync(context.Caller.Actor, ct); }
        catch (AppException error) when (error.Kind == AppErrorKind.Forbidden) { owner = false; }
        if (!owner)
        {
            var employee = await staff.GetByUserIdAsync(context.Caller.Actor.UserId, ct)
                ?? throw new AppException(ErrorCodes.Forbidden, "Staff access is required.", AppErrorKind.Forbidden);
            await access.RequireStaffManagementAsync(context.Caller.Actor, employee.Id, ct);
            if (!context.IsNavigation && !transitioned)
            {
                state.EnterFlow("sch.view", clock.UtcNow);
                state.SetFeatureData(Delivery, new DeliveryProgress(Transitioned: true));
            }
            state.SetFeatureData(Feature, new Draft(employee.Id));
            await RenderDetails(context, ct);
            return;
        }
        if (!context.IsNavigation && !transitioned)
        {
            if (Route == "schedule")
            {
                state.EnterFlow("schedule", clock.UtcNow);
                state.SetFeatureData(Feature, new Draft());
            }
            else
            {
                var previous = state.GetFeatureData<Draft>(Feature) ?? new Draft();
                state.BeginStep("schedule", clock.UtcNow);
                state.SetFeatureData(Feature, previous with { Page = Math.Max(0, previous.Page + (Route.EndsWith("next") ? 1 : -1)) });
            }
        }
        if (!context.IsNavigation) state.SetFeatureData(Delivery, new DeliveryProgress(Transitioned: true));
        var all = await staff.ListAsync(includeInactive: true, cancellationToken: ct);
        var page = Math.Min(state.GetFeatureData<Draft>(Feature)?.Page ?? 0, Math.Max(0, (all.Count - 1) / PageSize));
        state.SetFeatureData(Feature, new Draft(Page: page));
        List<TelegramMenuButton> buttons = all.Skip(page * PageSize).Take(PageSize)
            .Select(s => state.ItemButton(s.DisplayName, "sch.view", s.Id)).ToList();
        if (page > 0) buttons.Add(state.Button("Previous employees", "sch.staff.prev"));
        if ((page + 1) * PageSize < all.Count) buttons.Add(state.Button("Next employees", "sch.staff.next"));
        buttons.AddRange(NavigationKeyboard.Create(state));
        await sender.SendMenuAsync(context.ChatId, all.Count == 0 ? "No employees yet." : "Choose an employee's schedule:", buttons, ct);
    }

    private async Task RenderDetails(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        var draft = state.GetFeatureData<Draft>(Feature)!;
        var hours = await schedules.GetHoursAsync(context.Caller.Actor, draft.StaffId, ct);
        var days = (await schedules.ListDaysOffAsync(context.Caller.Actor, draft.StaffId, ct))
            .OrderBy(d => d.Date).ThenBy(d => d.Weekday).ThenBy(d => d.Id).ToArray();
        var page = Math.Min(draft.Page, Math.Max(0, (days.Length - 1) / PageSize));
        state.SetFeatureData(Feature, draft with { Page = page });
        var text = hours is null ? "Working hours are not configured." : $"Hours: {Time(hours.OpensAt)}–{Time(hours.ClosesAt)}";
        if (hours?.BreakStartsAt is { } start && hours.BreakEndsAt is { } end)
            text += $"\nBreak: {Time(start)}–{Time(end)}";
        text += "\nTimes use the business timezone.\nDays off: " + days.Length;
        List<TelegramMenuButton> buttons = [state.Button("Edit hours", "sch.hours"), state.Button("Add day off", "sch.add")];
        buttons.AddRange(days.Skip(page * PageSize).Take(PageSize)
            .Select(d => state.ItemButton("Remove " + (d.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? d.Weekday.ToString()), "sch.remove", d.Id)));
        if (page > 0) buttons.Add(state.Button("Previous days off", "sch.days.prev"));
        if ((page + 1) * PageSize < days.Length) buttons.Add(state.Button("Next days off", "sch.days.next"));
        buttons.AddRange(NavigationKeyboard.Create(state));
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }

    private Task Prompt(TelegramHandlerContext context, string? error, CancellationToken ct)
    {
        var instruction = Route == "sch.hours"
            ? "Send opening and closing times: 09:00 18:00\nWith a break: 09:00 18:00 13:00 14:00\nTwo times means no break. Times use the business timezone."
            : "Send a date (2027-02-28) or an English weekday name (Sunday).";
        return sender.SendMenuAsync(context.ChatId, error is null ? instruction : error + "\n" + instruction,
            NavigationKeyboard.Create(context.State), ct);
    }

    private static WorkingHoursModel ParseHours(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 4) || parts.Any(p => !CatalogScheduleInputParser.TryParseTime(p, out _)))
            throw new AppException(ErrorCodes.InvalidWorkingHours, "Use two or four times in HH:mm format.", AppErrorKind.Validation);
        var times = parts.Select(p => TimeOnly.ParseExact(p, "HH:mm", CultureInfo.InvariantCulture)).ToArray();
        return new(times[0], times[1], times.Length == 4 ? times[2] : null, times.Length == 4 ? times[3] : null);
    }

    private static DayOffModel ParseDay(string text)
    {
        if (CatalogScheduleInputParser.TryParseDate(text, out var date)) return new(Date: date);
        foreach (var day in Enum.GetValues<DayOfWeek>())
            if (string.Equals(day.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase)) return new(Weekday: day);
        throw new AppException(ErrorCodes.InvalidDayOff, "Use yyyy-MM-dd or an English weekday name.", AppErrorKind.Validation);
    }

    private static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
