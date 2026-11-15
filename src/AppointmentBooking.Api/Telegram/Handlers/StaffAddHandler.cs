using System.Globalization;
using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StaffAddHandler(
    StaffManagementService staff, IAccessService access, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "staff.add";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        await access.RequireOwnerAsync(context.Caller.Actor, ct);
        var draft = context.State.GetFeatureData<StaffManagementDraft>("management") ?? new();
        if (!context.IsNavigation && !(context.IsRetry && context.State.Step == Route) &&
            (context.Callback is not null || context.Text?.StartsWith('/') == true))
        {
            context.State.NavigateTo(Route, clock.UtcNow);
            draft = draft with { Candidate = null, CreatedStaffId = null, SavedName = null };
            context.State.SetFeatureData("management", draft);
        }

        var text = "Send the employee's Telegram user ID. They must have opened this bot with /start first.";
        if (!context.IsNavigation && context.Callback is null &&
            context.Text is { } input && !input.StartsWith('/'))
        {
            // A new search invalidates any selection button from the previous result.
            if (!context.IsRetry) context.State.BeginStep(Route, clock.UtcNow);
            draft = draft with { Candidate = null };
            context.State.SetFeatureData("management", draft);
            if (!long.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var telegramId) || telegramId <= 0)
                text = "Enter a positive numeric Telegram user ID, or use Back or Cancel.";
            else
            {
                try
                {
                    var candidate = await staff.FindRegisteredUserAsync(context.Caller.Actor, telegramId, ct);
                    draft = draft with { Candidate = candidate };
                    context.State.SetFeatureData("management", draft);
                }
                catch (AppException error) when (error.Code == ErrorCodes.UserNotFound)
                {
                    text = "This user is not registered. Ask them to use /start, then send their Telegram user ID again.";
                }
                catch (AppException error) when (error.Code == ErrorCodes.StaffAlreadyExists)
                {
                    text = "This user already has a staff profile. Send another Telegram user ID, or use Back or Cancel.";
                }
            }
        }

        var buttons = NavigationKeyboard.Create(context.State).ToList();
        if (draft.Candidate is { } selected)
        {
            text = $"Registered user\n\nName — {selected.DisplayName}\nTelegram ID — {selected.TelegramUserId}\n" +
                $"Username — {(selected.TelegramUsername is { } username ? "@" + username : "None")}\n\n" +
                "Choose this user, or send another Telegram user ID.";
            buttons.Insert(0, context.State.ItemButton("Use this user", "staff.create", selected.Id));
        }
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
