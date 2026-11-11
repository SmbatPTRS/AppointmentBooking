using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class BusinessRegionHandler(
    BusinessSettingsService settings, IAccessService access, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "settings.region";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        await access.RequireOwnerAsync(context.Caller.Actor, ct);
        var draft = context.State.GetFeatureData<BusinessSettingsEditDraft>(Route) ?? new();
        if (!context.IsNavigation && !(context.IsRetry && context.State.Step == Route) &&
            (context.Callback is not null || context.Text?.StartsWith('/') == true))
        {
            context.State.NavigateTo(Route, clock.UtcNow);
            draft = new();
            context.State.SetFeatureData(Route, draft);
        }

        var text = "Send two lines. Put the timezone ID on the first and the three-letter currency on the second.\n\nExample\nAsia/Yerevan\nAMD\n\nThese fields are locked after any booking exists, including cancelled bookings.";
        if (!context.IsNavigation && context.Callback is null && context.Text is { } input && !input.StartsWith('/') &&
            !(context.IsRetry && draft.Saved is not null))
        {
            draft = new();
            context.State.SetFeatureData(Route, draft);
            var lines = input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            try
            {
                if (lines.Length != 2)
                    text = "Please send exactly two lines.\n\n" + text;
                else
                {
                    var updated = await settings.UpdateTimeZoneAndCurrencyAsync(context.Caller.Actor, lines[0], lines[1], ct);
                    draft = new(updated);
                    context.State.SetFeatureData(Route, draft);
                    context.State.BeginStep(Route, clock.UtcNow);
                }
            }
            catch (AppException error) when (error.Code == ErrorCodes.BusinessSettingsLocked)
            {
                text = "Timezone and currency are locked because a booking exists, including cancelled bookings. " +
                    "You can still change the business name and staff label. Use Back to return to settings.";
            }
            catch (AppException error) when (error.Code is ErrorCodes.InvalidTimeZone or ErrorCodes.InvalidCurrency or ErrorCodes.ValidationFailed)
            {
                text = "Use a supported timezone ID, such as Asia/Yerevan, and a currency of three English letters. " +
                    "Send both lines again, or use Back or Cancel.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.BusinessSettingsNotFound)
            {
                text = "Business settings have not been initialized. Ask the administrator to initialize them.";
            }
        }
        if (draft.Saved is { } saved)
        {
            text = $"Timezone and currency updated.\nTimezone — {saved.TimeZoneId}\nCurrency — {saved.CurrencyCode}";
        }
        await sender.SendMenuAsync(context.ChatId, text, NavigationKeyboard.Create(context.State), ct);
    }
}
