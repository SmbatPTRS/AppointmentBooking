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

    public void Add(User user)
    {
        context.Users.Add(user);
    }
}