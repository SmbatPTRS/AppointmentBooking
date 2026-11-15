using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class SharedServicesHandler(IAccessService access, ITelegramMessageSender sender,
    IClock clock) : ITelegramHandler
{
    public string Route => "services";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (!context.IsRetry) state.ClearFeatureData(Route);
        if (!context.IsNavigation && context.Callback is null && context.Text?.StartsWith('/') != true)
            return;

        await access.RequireOwnerAsync(context.Caller.Actor, ct);
        if (!context.IsNavigation && !state.GetFeatureData<bool>(Route))
        {
            state.EnterFlow(Route, clock.UtcNow);
            state.SetFeatureData(Route, true);
        }

        await sender.SendMenuAsync(context.ChatId, "Shared services",
            [state.Button("Create service", "services.create"), .. NavigationKeyboard.Create(state)], ct);
    }
}

public sealed class CreateSharedServiceHandler(CatalogService catalog, IAccessService access,
    ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "services.create";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (!context.IsRetry) state.ClearFeatureData(Route);
        await access.RequireOwnerAsync(context.Caller.Actor, ct);

        var progress = state.GetFeatureData<CreateProgress>(Route);
        if (context.Callback is not null && progress is null)
        {
            state.NavigateTo(Route, clock.UtcNow);
            progress = new();
            state.SetFeatureData(Route, progress);
        }
        if ((state.Step != Route && !(context.IsRetry && progress?.SavedName is not null)) ||
            context.Text?.StartsWith('/') == true)
            return;

        const string prompt = "Type a service name (1–200 characters), or use Back or Cancel.";
        var reply = prompt;
        if (progress?.SavedName is { } saved)
            reply = $"Service created: {saved}.";
        else if (!context.IsNavigation && context.Callback is null && context.Text is { } name)
        {
            try
            {
                var result = await catalog.CreateServiceAsync(context.Caller.Actor, new(name), ct);
                state.SetFeatureData(Route, new CreateProgress(result.Name));
                // Leave the input route before sending, so ordinary text cannot create another service.
                state.BeginStep("services", clock.UtcNow);
                reply = $"Service created: {result.Name}.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.ValidationFailed)
            {
                reply = $"Service name must contain 1–200 characters. {prompt}";
            }
        }

        await sender.SendMenuAsync(context.ChatId, reply, NavigationKeyboard.Create(state), ct);
    }

    public sealed record CreateProgress(string? SavedName = null);
}
