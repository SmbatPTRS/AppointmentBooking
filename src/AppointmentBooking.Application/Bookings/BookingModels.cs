using AppointmentBooking.Domain.Enums;
using AppointmentBooking.Application.Abstractions;


namespace AppointmentBooking.Application.Bookings;

public sealed record CreateBookingRequest(
    Guid RequestId,
    Guid ClientUserId,
    Guid StaffServiceId,
    DateTime StartAtUtc,
    // the expected price is the price that the client sees during the booking process. It is used to validate that the price has not changed since the client started the booking process.
    decimal ExpectedPrice, 
    int ExpectedDurationMinutes
    );

public sealed record BookingOffering(
    Guid Id,
    Guid StaffMemberId,
    decimal Price,
    int DurationMinutes,
    bool IsActive,
    bool IsStaffMemberActive,
    bool IsServiceActive
);

public sealed record BookingDto(
    Guid Id,
    Guid StaffServiceId,
    string ServiceName,
    decimal Price,
    int DurationMinutes,
    DateTime StartAtUtc,
    DateTime EndAtUtc,
    BookingStatus Status
    );


