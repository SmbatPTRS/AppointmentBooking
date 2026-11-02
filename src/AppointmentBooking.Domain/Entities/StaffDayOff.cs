namespace AppointmentBooking.Domain.Entities;

public sealed class StaffDayOff
{
    public Guid Id { get; private set; }
    public Guid StaffMemberId { get; private set; }

    public DayOfWeek? Weekday { get; private set; }
    public DateOnly? Date { get; private set; }

    private StaffDayOff()
    {
        // Used by EF Core.
    }

    public StaffDayOff(
        Guid staffMemberId,
        DayOfWeek? weekday,
        DateOnly? date)
    {
        if (staffMemberId == Guid.Empty)
        {
            throw new ArgumentException(
                "Staff member ID is required.",
                nameof(staffMemberId));
        }

        if (weekday.HasValue == date.HasValue)
        {
            throw new ArgumentException(
                "Provide either a weekday or a date, but not both.");
        }

        if (weekday.HasValue &&
            !Enum.IsDefined(typeof(DayOfWeek), weekday.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(weekday),
                "Weekday must be between Sunday and Saturday.");
        }

        Id = Guid.NewGuid();
        StaffMemberId = staffMemberId;
        Weekday = weekday;
        Date = date;
    }
}