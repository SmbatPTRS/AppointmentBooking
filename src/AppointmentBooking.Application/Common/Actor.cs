namespace AppointmentBooking.Application.Common;

public sealed record Actor
{
    public Guid UserId { get; }

    public Actor(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        UserId = userId;
    }
}