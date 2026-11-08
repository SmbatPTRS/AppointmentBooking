using AppointmentBooking.Application.Abstractions;
using AppointmentBooking.Domain.Entities;
using AppointmentBooking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppointmentBooking.Infrastructure.Repositories;

public sealed class UserRepository(AppDbContext context)
    : IUserRepository
{
    public Task<User?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return context.Users.SingleOrDefaultAsync(
            user => user.Id == userId,
            cancellationToken);
    }

    public Task<User?> GetByTelegramUserIdAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        return context.Users.SingleOrDefaultAsync(
            user => user.TelegramUserId == telegramUserId,
            cancellationToken);
    }

    public async Task<User> InsertOrGetAsync(
        User candidate,
        CancellationToken cancellationToken = default)
    {
        // Let PostgreSQL resolve competing inserts without aborting the transaction.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (id, telegram_user_id, display_name, telegram_username, created_at_utc)
            VALUES ({candidate.Id}, {candidate.TelegramUserId}, {candidate.DisplayName},
                    {candidate.TelegramUsername}, {candidate.CreatedAtUtc})
            ON CONFLICT (telegram_user_id) DO NOTHING
            """, cancellationToken);

        return await context.Users.SingleAsync(
            user => user.TelegramUserId == candidate.TelegramUserId,
            cancellationToken);
    }

    public void Add(User user)
    {
        context.Users.Add(user);
    }
}
