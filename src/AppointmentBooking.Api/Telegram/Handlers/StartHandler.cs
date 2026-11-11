using AppointmentBooking.Api.Telegram.Keyboards;
using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;

namespace AppointmentBooking.Api.Telegram.Handlers;

public sealed class StartHandler(ITelegramMessageSender sender, IStaffRepository staff,
    IConfiguration configuration, IClock clock) : ITelegramHandler
{
    public string Route => "start";

    public async Task HandleAsync(TelegramHandlerContext context, CancellationToken ct)
    {
        // This controls menu visibility only. Feature application services still enforce permissions.
        var profile = await staff.GetByUserIdAsync(context.Caller.Actor.UserId, ct);
        var ownerId = configuration.GetValue<long>("Owner:TelegramUserId");
        var owner = ownerId > 0 && context.Caller.TelegramUserId == ownerId;
        context.State.Reset(clock.UtcNow);
        var buttons = MainMenuKeyboard.Create(context.State, owner, profile?.IsActive == true, profile is not null);
        await sender.SendMenuAsync(context.ChatId, $"Welcome, {context.Caller.DisplayName}! Your Telegram user ID is {context.Caller.TelegramUserId}. Choose an option.", buttons, ct);
    }
}
