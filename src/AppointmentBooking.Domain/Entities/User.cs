namespace AppointmentBooking.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; }

    public long TelegramUserId { get; private set; }

    public string DisplayName { get; private set; } = null!;

    public string? TelegramUsername { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private User()
    {
        // Used by EF Core when loading a user from the database.
    }

    public User(
        long telegramUserId,
        string displayName,
        string? telegramUsername,
        DateTime createdAtUtc)
    {
        if (telegramUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telegramUserId),
                "Telegram user ID must be positive.");
        }

        if (createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "Creation time must be UTC.",
                nameof(createdAtUtc));
        }

        Id = Guid.NewGuid();
        TelegramUserId = telegramUserId;
        CreatedAtUtc = createdAtUtc;

        UpdateProfile(displayName, telegramUsername);
    }

    public void UpdateProfile(
        string displayName,
        string? telegramUsername)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException(
                "Display name is required.",
                nameof(displayName));
        }

        string normalizedName = displayName.Trim();

        if (normalizedName.Length > 200)
        {
            throw new ArgumentException(
                "Display name cannot exceed 200 characters.",
                nameof(displayName));
        }

        string? normalizedUsername =
            string.IsNullOrWhiteSpace(telegramUsername)
                ? null
                : telegramUsername.Trim();

        if (normalizedUsername is { Length: > 100 })
        {
            throw new ArgumentException(
                "Telegram username cannot exceed 100 characters.",
                nameof(telegramUsername));
        }

        DisplayName = normalizedName;
        TelegramUsername = normalizedUsername;
    }
}