using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class BusinessSettingsHandler(
    BusinessSettingsService settings, IAccessService access, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "settings";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        await access.RequireOwnerAsync(context.Caller.Actor, ct);
        if (!context.IsNavigation && !(context.IsRetry && context.State.Step == Route))
            context.State.EnterFlow(Route, clock.UtcNow);

        var buttons = NavigationKeyboard.Create(context.State).ToList();
        string text;
        try
        {
            var current = await settings.GetAsync(ct);
            text = $"Business settings\n\nName — {current.DisplayName}\nStaff label — {current.StaffLabel}\n" +
                $"Timezone — {current.TimeZoneId}\nCurrency — {current.CurrencyCode}\n\n" +
                "Timezone and currency can change only before the first booking, including cancelled bookings. " +
                "Business name and staff label remain editable.";
            buttons.Insert(0, context.State.Button("Edit business name and staff label", "settings.labels"));
            buttons.Insert(1, context.State.Button("Edit timezone and currency", "settings.region"));
        }
        catch (AppException error) when (error.Code == ErrorCodes.BusinessSettingsNotFound)
        {
            text = "Business settings have not been initialized. Ask the administrator to initialize them.";
        }
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
