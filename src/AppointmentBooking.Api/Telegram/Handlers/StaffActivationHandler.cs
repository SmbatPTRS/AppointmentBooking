using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StaffActivationHandler(
    StaffManagementService staff, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "staff.activation";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var profiles = await staff.ListAsync(context.Caller.Actor, includeInactive: true, ct);
        var draft = context.State.GetFeatureData<StaffManagementDraft>("management");
        var profile = profiles.FirstOrDefault(profile => profile.Id == draft?.StaffId);
        if (profile is null)
        {
            await sender.SendMenuAsync(context.ChatId, "Select an employee from Staff management first.",
                NavigationKeyboard.Create(context.State), ct);
            return;
        }

        // The details button opens a prompt. Only its server-bound item button confirms.
        if (!context.IsNavigation && context.Callback is not null && context.ItemId is null &&
            !(context.IsRetry && context.State.Step == Route))
        {
            context.State.NavigateTo(Route, clock.UtcNow);
            draft = draft! with { PendingActive = !profile.IsActive, ActivationSaved = false };
            context.State.SetFeatureData("management", draft);
        }

        if (context.State.Step != Route || draft?.PendingActive is not { } targetActive)
        {
            await sender.SendMenuAsync(context.ChatId, "Choose Deactivate or Reactivate from employee details first.",
                NavigationKeyboard.Create(context.State), ct);
            return;
        }

        if (!context.IsNavigation && context.Callback is not null && context.ItemId == profile.Id &&
            !(context.IsRetry && draft.ActivationSaved))
        {
            await staff.SetActiveAsync(context.Caller.Actor, profile.Id, targetActive, ct);
            draft = draft with { ActivationSaved = true };
            context.State.SetFeatureData("management", draft);
            // Invalidate the confirmation after commit, while retaining the reply for retry.
            context.State.BeginStep(Route, clock.UtcNow);
        }

        var buttons = NavigationKeyboard.Create(context.State).ToList();
        string text;
        if (draft.ActivationSaved)
            text = targetActive ? "Employee reactivated." : "Employee deactivated.";
        else
        {
            text = $"{(targetActive ? "Reactivate" : "Deactivate")} {profile.DisplayName}? Use Confirm, Back or Cancel.";
            buttons.Insert(0, context.State.ItemButton(
                targetActive ? "Confirm reactivation" : "Confirm deactivation", Route, profile.Id));
        }
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
