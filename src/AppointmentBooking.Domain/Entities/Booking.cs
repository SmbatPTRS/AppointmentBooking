using AppointmentBooking.Domain.Enums;

namespace AppointmentBooking.Domain.Entities;

public sealed class Booking
{
    public Guid Id { get; private set; }
    public Guid ClientUserId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public Guid StaffServiceId { get; private set; }
    public Guid RequestId { get; private set; }

    public DateTime StartsAtUtc { get; private set; }
    public DateTime EndsAtUtc { get; private set; }

    public decimal Price { get; private set; }
    public int DurationMinutes { get; private set; }

    public BookingStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private Booking()
    {
        // Used by EF Core.
    }

    public Booking(
        Guid clientUserId,
        Guid staffMemberId,
        Guid staffServiceId,
        Guid requestId,
        DateTime startsAtUtc,
        decimal price,
        int durationMinutes,
        DateTime createdAtUtc)
    {
        RequireId(clientUserId, nameof(clientUserId));
        RequireId(staffMemberId, nameof(staffMemberId));
        RequireId(staffServiceId, nameof(staffServiceId));
        RequireId(requestId, nameof(requestId));

        RequireUtc(startsAtUtc, nameof(startsAtUtc));
        RequireUtc(createdAtUtc, nameof(createdAtUtc));

        if (startsAtUtc <= createdAtUtc)
        {
            throw new ArgumentException(
                "The booking must start after its creation time.",
                nameof(startsAtUtc));
        }

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

        // Derive the end from the captured duration.
        DateTime endsAtUtc = startsAtUtc.AddMinutes(durationMinutes);

        Id = Guid.NewGuid();
        ClientUserId = clientUserId;
        StaffMemberId = staffMemberId;
        StaffServiceId = staffServiceId;
        RequestId = requestId;

        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Price = price;
        DurationMinutes = durationMinutes;

        Status = BookingStatus.Confirmed;
        CreatedAtUtc = createdAtUtc;
    }

    public void Cancel(DateTime cancelledAtUtc)
    {
        RequireUtc(cancelledAtUtc, nameof(cancelledAtUtc));

        if (Status == BookingStatus.Cancelled)
        {
            return;
        }

        if (Status != BookingStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only a confirmed booking can be cancelled.");
        }

        if (cancelledAtUtc < CreatedAtUtc)
        {
            throw new ArgumentException(
                "Cancellation cannot precede booking creation.",
                nameof(cancelledAtUtc));
        }

        if (cancelledAtUtc >= StartsAtUtc)
        {
            throw new InvalidOperationException(
                "A booking can only be cancelled before its start.");
        }

        Status = BookingStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
    }

    public void Complete(DateTime completedAtUtc)
    {
        RequireUtc(completedAtUtc, nameof(completedAtUtc));

        if (Status == BookingStatus.Completed)
        {
            return;
        }

        if (Status != BookingStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "Only a confirmed booking can be completed.");
        }

        if (completedAtUtc < EndsAtUtc)
        {
            throw new InvalidOperationException(
                "A booking can only be completed at or after its end.");
        }

        Status = BookingStatus.Completed;
        CompletedAtUtc = completedAtUtc;
    }

    private static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "An ID is required.",
                parameterName);
        }
    }

    private static void RequireUtc(
        DateTime value,
        string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "The timestamp must be UTC.",
                parameterName);
        }
    }
}