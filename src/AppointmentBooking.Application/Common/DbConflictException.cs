namespace AppointmentBooking.Application.Common;

public enum ConflictKind
{
    UniqueRequestId,
    Overlap
}

public sealed class DbConflictException(ConflictKind kind, Exception innerException)
    : Exception("A booking database constraint was violated.", innerException)
{
    public ConflictKind Kind { get; } = kind;
}
