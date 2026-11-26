using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>Explicit activation confirmations; delivery retries never invert or replay a saved status.</summary>
public sealed class CatalogActivationHandler(string route, SharedServiceActivationService services,
    OfferingActivationService offerings, IAccessService access, ICatalogRepository catalog,
    ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public static readonly IReadOnlyList<string> Routes =
        ["svc.on", "svc.off", "svc.confirm", "svc.saved", "off.on", "off.off", "off.confirm", "off.done"];
    private const string Feature = "catalog.activation";
    private const string Transition = "catalog.activation.transition";
    public string Route => route;
    private bool IsOffering => Route.StartsWith("off.", StringComparison.Ordinal);
    public sealed record Draft(Guid ServiceId, bool IsActive, Guid? StaffId, string Name, bool Committed = false);

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (context.Callback is null && !context.IsNavigation && !context.IsRetry && context.Text?.StartsWith('/') == true)
        {
            await sender.SendTextAsync(context.ChatId, "Open /services or /offerings and choose an activation action first.", ct);
            return;
        }
        if (!context.IsRetry) state.ClearFeatureData(Transition);
        if (Route.EndsWith(".on") || Route.EndsWith(".off"))
        {
            if (!context.IsNavigation && !context.IsRetryRender && !state.GetFeatureData<bool>(Transition))
            {
                if (context.Callback is null || context.ItemId is not { } selected) return;
                Guid? staffId = IsOffering
                    ? state.GetFeatureData<OfferingOverviewHandler.Draft>(OfferingOverviewHandler.Feature)?.StaffId : null;
                if (IsOffering && (staffId is null || staffId == Guid.Empty)) return;
                // The action button binds the target, rather than negating a status that
                // another operation may have changed since the list was displayed.
                var selection = new Draft(selected, Route.EndsWith(".on"), staffId, "");
                selection = selection with { Name = await ReadName(context.Caller.Actor, selection, ct) };
                state.NavigateTo(IsOffering ? "off.confirm" : "svc.confirm", clock.UtcNow);
                state.SetFeatureData(Feature, selection);
                state.SetFeatureData(Transition, true);
            }
            await Confirm(context, ct);
            return;
        }
        var draft = state.GetFeatureData<Draft>(Feature);
        if (draft is null || (draft.StaffId is not null) != IsOffering) return;
        if (Route is "svc.saved" or "off.done")
        {
            if (context.IsRetry || context.IsNavigation) await Saved(context, ct);
            return;
        }
        if (context.IsNavigation || context.IsRetryRender)
        {
            await Confirm(context, ct);
            return;
        }
        if (draft.Committed || state.Step != Route || context.Callback?.Route != Route || context.ItemId != draft.ServiceId) return;
        try
        {
            if (draft.StaffId is { } staffId)
            {
                var result = await offerings.SetActiveAsync(context.Caller.Actor, staffId, draft.ServiceId, draft.IsActive, ct);
                draft = draft with { Name = result.ServiceName, IsActive = result.IsActive };
            }
            else
            {
                var result = await services.SetActiveAsync(context.Caller.Actor, draft.ServiceId, draft.IsActive, ct);
                draft = draft with { Name = result.Name, IsActive = result.IsActive };
            }
        }
        catch (AppException error) when (error.Kind is AppErrorKind.Validation or AppErrorKind.Conflict or AppErrorKind.NotFound)
        {
            await sender.SendMenuAsync(context.ChatId, error.Message, NavigationKeyboard.Create(state), ct);
            return;
        }
        state.SetFeatureData(Feature, draft with { Committed = true });
        state.BeginStep(IsOffering ? "off.done" : "svc.saved", clock.UtcNow);
        await Saved(context, ct);
    }

    private async Task Confirm(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = context.State.GetFeatureData<Draft>(Feature);
        if (draft is null) return;
        var name = await ReadName(context.Caller.Actor, draft, ct);
        var action = draft.IsActive ? "activation" : "deactivation";
        var buttons = new List<TelegramMenuButton>
        {
            context.State.ItemButton("Confirm " + action, draft.StaffId is null ? "svc.confirm" : "off.confirm", draft.ServiceId)
        };
        buttons.AddRange(NavigationKeyboard.Create(context.State));
        await sender.SendMenuAsync(context.ChatId,
            $"Confirm {action} of {(draft.StaffId is null ? "shared service" : "employee offering")}: {name}?", buttons, ct);
    }

    private async Task Saved(TelegramHandlerContext context, CancellationToken ct)
    {
        var draft = context.State.GetFeatureData<Draft>(Feature);
        if (draft?.Committed != true) return;
        await ReadName(context.Caller.Actor, draft, ct); // Recheck access on failed-send retries.
        var buttons = new List<TelegramMenuButton>
        {
            context.State.Button(draft.StaffId is null ? "View services" : "View offerings", draft.StaffId is null ? "services" : "off.refresh")
        };
        buttons.AddRange(NavigationKeyboard.Create(context.State));
        await sender.SendMenuAsync(context.ChatId,
            $"{(draft.StaffId is null ? "Service" : "Offering")} {(draft.IsActive ? "activated" : "deactivated")}: {draft.Name}.", buttons, ct);
    }

    private async Task<string> ReadName(Actor actor, Draft draft, CancellationToken ct)
    {
        if (draft.StaffId is { } staffId)
        {
            await access.RequireStaffManagementAsync(actor, staffId, ct);
            if (await catalog.GetOfferingSnapshotAsync(staffId, draft.ServiceId, ct) is null)
                throw new AppException(ErrorCodes.StaffServiceNotFound, "Offering not found.", AppErrorKind.NotFound);
        }
        else await access.RequireOwnerAsync(actor, ct);
        return (await catalog.GetServiceSnapshotAsync(draft.ServiceId, ct))?.Name
            ?? throw new AppException(ErrorCodes.ServiceNotFound, "Service not found.", AppErrorKind.NotFound);
    }
}
