using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StaffCreateHandler(
    StaffManagementService staff, IAccessService access, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "staff.create";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        await access.RequireOwnerAsync(context.Caller.Actor, ct);
        var draft = context.State.GetFeatureData<StaffManagementDraft>("management");
        if (draft?.Candidate is not { } candidate)
        {
            await sender.SendMenuAsync(context.ChatId, "Choose a registered user through Add employee first.",
                NavigationKeyboard.Create(context.State), ct);
            return;
        }

        if (!context.IsNavigation && context.ItemId == candidate.Id &&
            !(context.IsRetry && context.State.Step == Route))
        {
            context.State.NavigateTo(Route, clock.UtcNow);
            draft = draft with { CreatedStaffId = null, SavedName = null };
            context.State.SetFeatureData("management", draft);
        }

        var text = $"Type the staff display name for {candidate.DisplayName} (1–200 characters).";
        if (context.State.Step != Route)
            text = "Choose Use this user before entering a staff display name.";
        else if (!context.IsNavigation && context.Callback is null && draft.CreatedStaffId is null &&
                 context.Text is { } name && !name.StartsWith('/'))
        {
            try
            {
                var created = await staff.CreateAsync(context.Caller.Actor, candidate.Id, name, ct);
                draft = draft with { CreatedStaffId = created.Id, SavedName = created.DisplayName };
                context.State.SetFeatureData("management", draft);
                context.State.BeginStep(Route, clock.UtcNow);
            }
            catch (AppException error) when (error.Code == ErrorCodes.ValidationFailed)
            {
                text = "Display name must contain 1–200 characters. Type another name, or use Back or Cancel.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.StaffAlreadyExists)
            {
                text = "This user already has a staff profile. Use Back to choose another user, or Cancel.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.UserNotFound)
            {
                text = "This user is no longer registered. Use Back to choose another user, or Cancel.";
            }
        }

        var buttons = NavigationKeyboard.Create(context.State).ToList();
        if (draft.CreatedStaffId is { } staffId)
        {
            text = $"Staff profile created for {draft.SavedName}.";
            buttons.Insert(0, context.State.ItemButton("View employee", "staff.details", staffId));
        }
        await sender.SendMenuAsync(context.ChatId, text, buttons, ct);
    }
}
