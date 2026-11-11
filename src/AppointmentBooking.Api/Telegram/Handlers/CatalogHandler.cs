using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>Customer catalogue screen registered for /catalog through the shared dispatcher.</summary>
public sealed class CatalogHandler(CatalogService catalog, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "catalog";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        // Ordinary text is routed to the current step by D's dispatcher.
        // Ignore it without replacing the screen or invalidating its buttons.
        var command = context.Text?.Split(' ', 2)[0].Split('@', 2)[0];
        if (context.Callback?.Route != Route && command != "/catalog")
            return;

        var services = await catalog.ListActiveServicesAsync(ct);
        // Ten names of at most 200 characters fit comfortably within Telegram's limit.
        foreach (var page in services.Chunk(10))
            await sender.SendTextAsync(context.ChatId,
                "Available services:\n" + string.Join("\n", page.Select(s => "• " + s.Name)), ct);

        context.State.BeginStep(Route, clock.UtcNow);
        context.State.SetAllowedCallbacks(["start"]);
        await sender.SendMenuAsync(context.ChatId,
            services.Count == 0 ? "No services are available yet." : "Prices and durations depend on the employee.",
            [new TelegramMenuButton("Main menu", CallbackDataParser.Format("start", context.State.Revision))], ct);
    }
}
