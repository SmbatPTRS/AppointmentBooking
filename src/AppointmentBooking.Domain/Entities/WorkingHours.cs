namespace AppointmentBooking.Domain.Entities;

public sealed class WorkingHours
{
    public Guid Id { get; private set; }
    public Guid StaffMemberId { get; private set; }

    public TimeOnly OpensAt { get; private set; }
    public TimeOnly ClosesAt { get; private set; }

    public TimeOnly? BreakStartsAt { get; private set; }
    public TimeOnly? BreakEndsAt { get; private set; }

    private WorkingHours()
    {
        // Used by EF Core.
    }

    public WorkingHours(
        Guid staffMemberId,
        TimeOnly opensAt,
        TimeOnly closesAt,
        TimeOnly? breakStartsAt = null,
        TimeOnly? breakEndsAt = null)
    {
        if (staffMemberId == Guid.Empty)
        {
            throw new ArgumentException(
                "Staff member ID is required.",
                nameof(staffMemberId));
        }

        Update(opensAt, closesAt, breakStartsAt, breakEndsAt);

        Id = Guid.NewGuid();
        StaffMemberId = staffMemberId;
    }

    public void Update(
        TimeOnly opensAt,
        TimeOnly closesAt,
        TimeOnly? breakStartsAt,
        TimeOnly? breakEndsAt)
    {
        if (opensAt >= closesAt)
        {
            throw new ArgumentException(
                "Opening time must be before closing time.",
                nameof(closesAt));
        }

        if (breakStartsAt.HasValue != breakEndsAt.HasValue)
        {
            throw new ArgumentException(
                "Provide both break endpoints or neither.");
        }

        if (breakStartsAt.HasValue && breakEndsAt.HasValue)
        {
            TimeOnly breakStart = breakStartsAt.Value;
            TimeOnly breakEnd = breakEndsAt.Value;

            if (breakStart >= breakEnd)
            {
                throw new ArgumentException(
                    "Break start must be before break end.",
                    nameof(breakEndsAt));
            }

            if (breakStart < opensAt || breakEnd > closesAt)
            {
                throw new ArgumentException(
                    "The break must be within working hours.");
            }
        }

        OpensAt = opensAt;
        ClosesAt = closesAt;
        BreakStartsAt = breakStartsAt;
        BreakEndsAt = breakEndsAt;
    }
}