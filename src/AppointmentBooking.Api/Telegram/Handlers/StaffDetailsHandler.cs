using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StaffDetailsHandler(
    StaffManagementService staff, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "staff.details";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        var profiles = await staff.ListAsync(context.Caller.Actor, includeInactive: true, ct);
        var draft = context.State.GetFeatureData<StaffManagementDraft>("management");
        if (!context.IsNavigation && !(context.IsRetry && context.State.Step == Route) &&
            context.ItemId is { } selected)
        {
            context.State.NavigateTo(Route, clock.UtcNow);
            draft = (draft ?? new StaffManagementDraft()) with { StaffId = selected };
            context.State.SetFeatureData("management", draft);
        }

        var profile = profiles.FirstOrDefault(profile => profile.Id == draft?.StaffId);
        var text = profile is null ? "Select an employee from Staff management to view their details." :
            $"Employee details\n\nName — {profile.DisplayName}\nStatus — {(profile.IsActive ? "Active" : "Inactive")}";
        var buttons = NavigationKeyboard.Create(context.State).ToList();
        if (profile is not null)
        {
            buttons.Insert(0, context.State.Button("Rename", "staff.rename"));
            buttons.Insert(1, context.State.Button(profile.IsActive ? "Deactivate" : "Reactivate", "staff.activation"));
        }
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
