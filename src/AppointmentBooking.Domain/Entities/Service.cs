namespace AppointmentBooking.Domain.Entities;

public sealed class Service
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public bool IsActive { get; private set; }

    private Service()
    {
        // Used by EF Core.
    }

    public Service(string name)
    {
        Rename(name);

        Id = Guid.NewGuid();
        IsActive = true;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "Service name is required.",
                nameof(name));
        }

        string normalizedName = name.Trim();

        if (normalizedName.Length > 200)
        {
            throw new ArgumentException(
                "Service name cannot exceed 200 characters.",
                nameof(name));
        }

        Name = normalizedName;
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