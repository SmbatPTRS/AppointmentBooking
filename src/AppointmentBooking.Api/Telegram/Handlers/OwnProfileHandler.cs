using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Staff;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class OwnProfileHandler(
    StaffManagementService staff, ITelegramMessageSender sender, IClock clock) : ITelegramHandler
{
    public string Route => "profile";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        StaffProfile profile;
        try
        {
            // Resolve the profile from trusted caller identity on every update.
            profile = await staff.GetOwnProfileAsync(context.Caller.Actor, ct);
        }
        catch (AppException error) when (error.Code is ErrorCodes.StaffNotFound or ErrorCodes.StaffInactive)
        {
            await sender.SendMenuAsync(context.ChatId, "An active employee profile is required to change your display name.",
                NavigationKeyboard.Create(context.State), ct);
            return;
        }

        var draft = context.State.GetFeatureData<OwnProfileDraft>(Route);
        if (!context.IsNavigation && !(context.IsRetry && context.State.Step == Route) &&
            (context.Callback is not null || context.Text?.StartsWith('/') == true))
        {
            context.State.EnterFlow(Route, clock.UtcNow);
            draft = new(profile.Id);
            context.State.SetFeatureData(Route, draft);
        }

        var text = $"Your display name is {profile.DisplayName}. Type a new name (1–200 characters), or use Back or Cancel.";
        if (!context.IsNavigation && context.Callback is null && context.Text is { } name && !name.StartsWith('/') &&
            draft?.StaffId == profile.Id)
        {
            if (!context.IsRetry)
            {
                draft = draft with { SavedName = null };
                context.State.SetFeatureData(Route, draft);
            }
            try
            {
                if (!(context.IsRetry && draft.SavedName is not null))
                {
                    var updated = await staff.RenameAsync(context.Caller.Actor, profile.Id, name, ct);
                    draft = draft with { SavedName = updated.DisplayName };
                    context.State.SetFeatureData(Route, draft);
                }
                text = $"Your display name was updated to {draft.SavedName}.";
            }
            catch (AppException error) when (error.Code == ErrorCodes.ValidationFailed)
            {
                text = "Display name must contain 1–200 characters. Type another name, or use Back or Cancel.";
            }
        }
        await sender.SendMenuAsync(context.ChatId, text, NavigationKeyboard.Create(context.State), ct);
    }
}

public sealed record OwnProfileDraft(Guid StaffId, string? SavedName = null);
