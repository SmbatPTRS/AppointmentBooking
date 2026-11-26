using System.Globalization;
using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>Creation and price/duration forms owned by B; writes use OfferingTermsService.</summary>
public sealed class OfferingTermsHandler(string route, OfferingManagementService management,
    OfferingTermsService terms, ICatalogRepository catalog, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public static readonly IReadOnlyList<string> Routes =
        ["off.add", "off.add.next", "off.add.prev", "off.new", "off.edit", "off.terms", "off.saved"];
    private const string Feature = "off.terms.draft";
    private const string Transition = "off.terms.transition";
    private const int PageSize = 10;
    public string Route => route;
    public sealed record FormDraft(Guid StaffId, Guid ServiceId = default, bool Create = true,
        int Page = 0, StaffServiceDto? Saved = null);

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (context.Callback is null && !context.IsNavigation && !context.IsRetry && context.Text?.StartsWith('/') == true)
        {
            await sender.SendTextAsync(context.ChatId, "Open /offerings and choose an employee first.", ct);
            return;
        }
        if (!context.IsRetry) state.ClearFeatureData(Transition);
        var transitioned = state.GetFeatureData<bool>(Transition);
        if (Route == "off.saved")
        {
            if (context.IsRetry || context.IsNavigation) await RenderSaved(context, ct);
            return; // Ordinary text on a completed form must not repeat the write.
        }
        if (Route is "off.add" or "off.add.next" or "off.add.prev")
        {
            if (context.Callback is null && !context.IsNavigation && !context.IsRetry) return;
            if (!context.IsNavigation && !context.IsRetryRender && !transitioned)
            {
                if (Route == "off.add")
                {
                    var selected = state.GetFeatureData<OfferingOverviewHandler.Draft>(OfferingOverviewHandler.Feature);
                    if (selected is null || selected.StaffId == Guid.Empty) return;
                    await management.ListAsync(context.Caller.Actor, selected.StaffId, ct);
                    state.NavigateTo("off.add", clock.UtcNow);
                    state.SetFeatureData(Feature, new FormDraft(selected.StaffId));
                }
                else
                {
                    var previous = state.GetFeatureData<FormDraft>(Feature);
                    if (previous is null) return;
                    state.BeginStep("off.add", clock.UtcNow);
                    state.SetFeatureData(Feature, previous with
                    {
                        Page = Math.Max(0, previous.Page + (Route.EndsWith("next") ? 1 : -1))
                    });
                }
                state.SetFeatureData(Transition, true);
            }
            await RenderServices(context, ct);
            return;
        }
        if (Route is "off.new" or "off.edit" && context.ItemId is { } serviceId &&
            !context.IsNavigation && !context.IsRetryRender && !transitioned)
        {
            var selected = state.GetFeatureData<OfferingOverviewHandler.Draft>(OfferingOverviewHandler.Feature);
            if (selected is null || selected.StaffId == Guid.Empty) return;
            var rows = await management.ListAsync(context.Caller.Actor, selected.StaffId, ct);
            if (Route == "off.edit" && !rows.Any(o => o.ServiceId == serviceId))
                throw new AppException(ErrorCodes.StaffServiceNotFound, "Offering not found.", AppErrorKind.NotFound);
            state.NavigateTo("off.terms", clock.UtcNow);
            state.SetFeatureData(Feature, new FormDraft(selected.StaffId, serviceId, Create: Route == "off.new"));
            state.SetFeatureData(Transition, true);
        }
        var draft = state.GetFeatureData<FormDraft>(Feature);
        if (draft is null || draft.ServiceId == Guid.Empty || state.Step != "off.terms") return;
        if (context.IsNavigation || context.IsRetryRender || context.Callback is not null)
        {
            await Prompt(context, null, ct);
            return;
        }
        var parts = context.Text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length != 2 || !CatalogScheduleInputParser.TryParsePrice(parts[0], out var price) ||
            !CatalogScheduleInputParser.TryParseDuration(parts[1], out var duration))
        {
            await Prompt(context, "Use a nonnegative price with a decimal point and a positive whole number of minutes.", ct);
            return;
        }
        StaffServiceDto saved;
        try
        {
            var request = new OfferingTermsRequest(price, duration);
            saved = draft.Create
                ? await terms.CreateAsync(context.Caller.Actor, draft.StaffId, draft.ServiceId, request, ct)
                : await terms.UpdateAsync(context.Caller.Actor, draft.StaffId, draft.ServiceId, request, ct);
        }
        catch (AppException error) when (error.Kind is AppErrorKind.Validation or AppErrorKind.Conflict or AppErrorKind.NotFound)
        {
            await Prompt(context, error.Message, ct);
            return;
        }
        // Store the committed result before sending. D retries this destination screen
        // if delivery fails; it must never run the original text as another mutation.
        state.SetFeatureData(Feature, draft with { Saved = saved });
        state.BeginStep("off.saved", clock.UtcNow);
        await RenderSaved(context, ct);
    }

    private async Task RenderServices(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        var draft = state.GetFeatureData<FormDraft>(Feature);
        if (draft is null) return;
        var configured = (await management.ListAsync(context.Caller.Actor, draft.StaffId, ct))
            .Select(o => o.ServiceId).ToHashSet();
        var services = (await catalog.ListActiveServicesAsync(ct)).Where(s => !configured.Contains(s.Id)).ToArray();
        var page = Math.Clamp(draft.Page, 0, Math.Max(0, (services.Length - 1) / PageSize));
        state.SetFeatureData(Feature, draft with { Page = page });
        var buttons = services.Skip(page * PageSize).Take(PageSize)
            .Select(s => state.ItemButton(s.Name, "off.new", s.Id)).ToList();
        if (page > 0) buttons.Add(state.Button("Previous services", "off.add.prev"));
        if ((page + 1) * PageSize < services.Length) buttons.Add(state.Button("Next services", "off.add.next"));
        buttons.AddRange(NavigationKeyboard.Create(state));
        await sender.SendMenuAsync(context.ChatId, services.Length == 0
            ? "No additional active services are available. Use Back to view existing offerings."
            : $"Choose a shared service · page {page + 1} of {Math.Max(1, (services.Length + PageSize - 1) / PageSize)}.", buttons, ct);
    }

    private async Task Prompt(TelegramHandlerContext context, string? error, CancellationToken ct)
    {
        var draft = context.State.GetFeatureData<FormDraft>(Feature)!;
        var rows = await management.ListAsync(context.Caller.Actor, draft.StaffId, ct);
        var service = await catalog.GetServiceAsync(draft.ServiceId, ct);
        var current = rows.SingleOrDefault(o => o.ServiceId == draft.ServiceId);
        var text = $"{service?.Name ?? "Service no longer available"}\nSend price and duration in minutes, for example: 25.50 30.\n" +
            "Use a decimal point. Price: 0–9999999999.99, at most two decimal places. Duration: positive whole minutes.";
        if (!draft.Create && current is not null)
            text += $"\nCurrent: {current.Price.ToString("0.00", CultureInfo.InvariantCulture)} · {current.DurationMinutes} min.";
        if (error is not null) text = error + "\n" + text;
        await sender.SendMenuAsync(context.ChatId, text, NavigationKeyboard.Create(context.State), ct);
    }

    private async Task RenderSaved(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = context.State.GetFeatureData<FormDraft>(Feature);
        if (draft?.Saved is not { } saved) return;
        await management.ListAsync(context.Caller.Actor, draft.StaffId, ct);
        var text = $"Offering {(draft.Create ? "created" : "updated")}: {saved.ServiceName}.\n" +
            $"Price: {saved.Price.ToString("0.00", CultureInfo.InvariantCulture)} · Duration: {saved.DurationMinutes} min.";
        List<TelegramMenuButton> buttons = [context.State.Button("View offerings", "off.refresh")];
        buttons.AddRange(NavigationKeyboard.Create(context.State));
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
