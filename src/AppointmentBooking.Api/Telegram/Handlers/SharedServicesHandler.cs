using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class SharedServicesHandler(CatalogService catalog, ITelegramMessageSender sender,
    IClock clock) : ITelegramHandler
{
    public string Route => "services";
    private const string PageKey = "services.page";
    private const int PageSize = 10;

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var state = context.State;
        if (!context.IsRetry) state.ClearFeatureData(Route);
        if (!context.IsNavigation && context.Callback is null && context.Text?.StartsWith('/') != true)
            return;

        var services = await catalog.ListServicesForOwnerAsync(context.Caller.Actor, ct);
        if (!context.IsNavigation && !state.GetFeatureData<bool>(Route))
        {
            if (context.ItemId is { } anchor)
            {
                var index = services.ToList().FindIndex(service => service.Id == anchor);
                state.BeginStep(Route, clock.UtcNow);
                state.SetFeatureData(PageKey, Math.Max(0, index) / PageSize);
            }
            else
            {
                state.EnterFlow(Route, clock.UtcNow);
            }
            state.SetFeatureData(Route, true);
        }

        var lastPage = Math.Max(0, (services.Count - 1) / PageSize);
        var page = Math.Min(state.GetFeatureData<int>(PageKey), lastPage);
        var buttons = services.Skip(page * PageSize).Take(PageSize)
            .Select(service => state.ItemButton(
                $"{service.Name} ({(service.IsActive ? "active" : "inactive")})", "services.rename", service.Id))
            .ToList();
        if (page > 0)
            buttons.Add(state.ItemButton("Previous page", Route, services[(page - 1) * PageSize].Id));
        if (page < lastPage)
            buttons.Add(state.ItemButton("Next page", Route, services[(page + 1) * PageSize].Id));
        buttons.Add(state.Button("Create service", "services.create"));
        buttons.AddRange(NavigationKeyboard.Create(state));

        var title = services.Count == 0 ? "Shared services: no services yet."
            : $"Shared services · page {page + 1} of {lastPage + 1}. Choose a service to rename.";
        await sender.SendMenuAsync(context.ChatId, title, buttons, ct);
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
