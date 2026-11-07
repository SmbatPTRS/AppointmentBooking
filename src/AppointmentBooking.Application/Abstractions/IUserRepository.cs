using AppointmentBooking.Domain.Entities;

namespace AppointmentBooking.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<User?> GetByTelegramUserIdAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default);

    // Persists the candidate or returns the user already stored for its Telegram ID.
    Task<User> InsertOrGetAsync(User candidate, CancellationToken cancellationToken = default);

    void Add(User user);
}
