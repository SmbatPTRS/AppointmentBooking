using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StaffRenameHandler(
    StaffManagementService staff, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "staff.rename";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        // Renaming another employee belongs to the owner flow, even though the service
        // also permits an active employee to rename their own profile.
        var profiles = await staff.ListAsync(context.Caller.Actor, includeInactive: true, ct);
        var draft = context.State.GetFeatureData<StaffManagementDraft>("management");
        if (draft?.StaffId is not { } staffId || !profiles.Any(profile => profile.Id == staffId))
        {
            await sender.SendMenuAsync(context.ChatId, "Select an employee from Staff management first.",
                NavigationKeyboard.Create(context.State), ct);
            return;
        }

        if (!context.IsNavigation && context.Callback is not null &&
            !(context.IsRetry && context.State.Step == Route))
        {
            context.State.NavigateTo(Route, clock.UtcNow);
            draft = draft with { SavedName = null };
            context.State.SetFeatureData("management", draft);
        }

        var text = "Type a new display name (1–200 characters).";
        if (context.State.Step != Route)
            text = "Choose Rename from the employee details screen first.";
        else if (!context.IsNavigation && context.Callback is null &&
                 context.Text is { } name && !name.StartsWith('/'))
        {
            if (!context.IsRetry)
            {
                draft = draft with { SavedName = null };
                context.State.SetFeatureData("management", draft);
            }
            try
            {
                // A failed reply must not repeat an already committed rename.
                if (!(context.IsRetry && draft.SavedName is not null))
                {
                    var updated = await staff.RenameAsync(context.Caller.Actor, staffId, name, ct);
                    draft = draft with { SavedName = updated.DisplayName };
                    context.State.SetFeatureData("management", draft);
                }
                text = $"Display name updated to {draft.SavedName}.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.ValidationFailed)
            {
                text = "Display name must contain 1–200 characters. Type another name, or use Back or Cancel.";
            }
        }

        await sender.SendMenuAsync(context.ChatId, text, NavigationKeyboard.Create(context.State), ct);
    }
}
