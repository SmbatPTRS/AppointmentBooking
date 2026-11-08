using AppointmentBooking.Application.Catalog;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

/// <summary>Customer catalogue screen. Registration awaits production access-service wiring.</summary>
public sealed class CatalogHandler(CatalogService catalog, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "catalog";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
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
