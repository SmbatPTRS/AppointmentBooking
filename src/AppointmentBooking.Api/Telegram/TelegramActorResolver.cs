using AppointmentBooking.Application.Common;
using AppointmentBooking.Application.Identity;
using TelegramUser = Telegram.Bot.Types.User;

namespace AppointmentBooking.Api.Telegram;

public sealed record TelegramActor(Actor Actor, long TelegramUserId, string DisplayName);

public sealed class TelegramActorResolver(UserService users)
{
    public async Task<TelegramActor> ResolveAsync(TelegramUser sender, CancellationToken ct = default)
    {
        if (sender.Id <= 0 || sender.IsBot)
            throw new AppException(ErrorCodes.Unauthorized, "A human Telegram sender is required.", AppErrorKind.Unauthorized);
        var displayName = string.Join(" ", new[] { sender.FirstName, sender.LastName }
            .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        if (displayName.Length > 200) displayName = displayName[..200];
        if (displayName.Length == 0)
            throw new AppException(ErrorCodes.ValidationFailed, "Telegram display name is missing.", AppErrorKind.Validation);
        var user = await users.GetOrCreateAsync(sender.Id, displayName, sender.Username, ct);
        return new TelegramActor(new Actor(user.Id), sender.Id, user.DisplayName);
    }
}
