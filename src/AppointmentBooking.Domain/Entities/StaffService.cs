namespace AppointmentBooking.Domain.Entities;

public sealed class StaffService
{
    public Guid Id { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public Guid ServiceId { get; private set; }
    public decimal Price { get; private set; }
    public int DurationMinutes { get; private set; }
    public bool IsActive { get; private set; }

    private StaffService()
    {
        // Used by EF Core.
    }

    public StaffService(
        Guid staffMemberId,
        Guid serviceId,
        decimal price,
        int durationMinutes)
    {
        if (staffMemberId == Guid.Empty)
        {
            throw new ArgumentException(
                "Staff member ID is required.",
                nameof(staffMemberId));
        }

        if (serviceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Service ID is required.",
                nameof(serviceId));
        }

        UpdateTerms(price, durationMinutes);

        Id = Guid.NewGuid();
        StaffMemberId = staffMemberId;
        ServiceId = serviceId;
        IsActive = true;
    }

    public void UpdateTerms(decimal price, int durationMinutes)
    {
        if (price < 0 || price > 9_999_999_999.99m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(price),
                "Price must be between 0 and 9,999,999,999.99.");
        }

        if (decimal.Round(price, 2) != price)
        {
            throw new ArgumentException(
                "Price cannot have more than two decimal places.",
                nameof(price));
        }

        if (durationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMinutes),
                "Duration must be a positive number of minutes.");
        }

        Price = price;
        DurationMinutes = durationMinutes;
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