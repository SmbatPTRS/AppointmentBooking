using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Business;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class BusinessLabelsHandler(
    BusinessSettingsService settings, IAccessService access, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "settings.labels";

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

        var text = "Send two lines. Put the business name on the first line (1–200 characters) and the staff label on the second (1–100).\n\nExample\nStudio\nSpecialist";
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
                    var updated = await settings.UpdateLabelsAsync(context.Caller.Actor, lines[0], lines[1], ct);
                    draft = new(updated);
                    context.State.SetFeatureData(Route, draft);
                    context.State.BeginStep(Route, clock.UtcNow);
                }
            }
            catch (AppException error) when (error.Code == ErrorCodes.ValidationFailed)
            {
                text = "Business name must contain 1–200 characters and staff label 1–100. Send both lines again, or use Back or Cancel.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.BusinessSettingsNotFound)
            {
                text = "Business settings have not been initialized. Ask the administrator to initialize them.";
            }
        }
        if (draft.Saved is { } saved)
        {
            text = $"Business labels updated.\nName — {saved.DisplayName}\nStaff label — {saved.StaffLabel}";
        }
        await sender.SendMenuAsync(context.ChatId, text, NavigationKeyboard.Create(context.State), ct);
    }
}
