using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Application.Common;
using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Identity;

public sealed class UserService(IUserRepository users, IClock clock)
{
    public async Task<User> GetOrCreateAsync(
        long telegramUserId,
        string displayName,
        string? telegramUsername,
        CancellationToken cancellationToken = default)
    {
        var existing = await users.GetByTelegramUserIdAsync(telegramUserId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var user = new User(telegramUserId, displayName, telegramUsername, clock.UtcNow);
        return await users.InsertOrGetAsync(user, cancellationToken);
    }
}
