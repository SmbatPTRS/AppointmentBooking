using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class RenameSharedServiceHandler(CatalogService catalog, IAccessService access,
    ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "services.rename";
    private const string Selection = "services.rename.selection";
    private const string Delivery = "services.rename.delivery";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (!context.IsRetry) state.ClearFeatureData(Delivery);
        await access.RequireOwnerAsync(context.Caller.Actor, ct);

        var progress = state.GetFeatureData<RenameDelivery>(Delivery);
        if (context.ItemId is { } selected && progress is null)
        {
            state.NavigateTo(Route, clock.UtcNow);
            state.SetFeatureData(Selection, selected);
            progress = new();
            state.SetFeatureData(Delivery, progress);
        }

        if (context.IsRetry && progress?.SavedName is { } saved)
        {
            await Reply($"Service renamed to {saved}.");
            return;
        }
        var serviceId = state.GetFeatureData<Guid>(Selection);
        if (state.Step != Route || serviceId == Guid.Empty || context.Text?.StartsWith('/') == true)
            return;

        var reply = "Send a new name (1–200 characters), or use Back or Cancel.";
        if (!context.IsNavigation && context.Callback is null && context.Text is { } name)
        {
            try
            {
                var result = await catalog.RenameServiceAsync(context.Caller.Actor, serviceId, new(name), ct);
                state.SetFeatureData(Delivery, new RenameDelivery(result.Name));
                state.BeginStep("services", clock.UtcNow);
                reply = $"Service renamed to {result.Name}.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.ValidationFailed)
            {
                reply = "The name must contain 1–200 characters. Send another name, or use Back or Cancel.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.ServiceNotFound)
            {
                reply = "This service is no longer available. Use Back to refresh the list.";
            }
        }
        await Reply(reply);

        Task Reply(string text) => sender.SendMenuAsync(context.ChatId, text, NavigationKeyboard.Create(state), ct);
    }

    public sealed record RenameDelivery(string? SavedName = null);
}
