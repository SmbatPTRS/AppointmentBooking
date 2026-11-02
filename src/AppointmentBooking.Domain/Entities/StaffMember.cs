namespace AppointmentBooking.Domain.Entities;

public sealed class StaffMember
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public bool IsActive { get; private set; }

    private StaffMember()
    {
        // Used by EF Core.
    }

    public StaffMember(Guid userId, string displayName)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        Rename(displayName);

        Id = Guid.NewGuid();
        UserId = userId;
        IsActive = true;
    }

    public void Rename(string displayName)
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

        DisplayName = normalizedName;
    }

    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}