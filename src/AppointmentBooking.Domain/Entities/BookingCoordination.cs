namespace AppointmentBooking.Domain.Entities;

public sealed class BookingCoordination
{
    public long Id { get; private set; }

    public int? BusinessSettingsId { get; private set; }

    public Guid? StaffMemberId { get; private set; }

    public long ChangeVersion { get; private set; }

    private BookingCoordination()
    {
        // Used by EF Core when loading a coordination row.
    }

    public BookingCoordination(BusinessSettings settings)
    {
        BusinessSettingsId = settings.Id;
    }

    public BookingCoordination(StaffMember staffMember)
    {
        StaffMemberId = staffMember.Id;
    }
}
